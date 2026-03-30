using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace NoteUI;

public sealed partial class TaskbarWidget : Window
{
    private readonly ClipboardHistoryManager _clipboardHistory;
    private readonly SnippetManager _snippetManager;
    private readonly NotesManager? _notesManager;

    private IntPtr _hWnd;
    private IntPtr _taskbarHandle;
    private IntPtr _trayHandle;
    private DispatcherTimer? _positionTimer;
    private IntPtr _activeHwndBeforeFlyout;

    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private Dictionary<string, bool> _modules;

    private const int WindowHeight = 30;
    private const int RightPadding = 6;
    private const int FallbackRightPadding = 140;

    // Win32 constants
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_NCACTIVATE = 0x0086;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    // prevent GC of the delegate
    private static SubclassProc? _subclassProc;

    public TaskbarWidget(ClipboardHistoryManager clipboardHistory, SnippetManager snippetManager, NotesManager? notesManager = null)
    {
        this.InitializeComponent();
        _clipboardHistory = clipboardHistory;
        _snippetManager = snippetManager;
        _notesManager = notesManager;
        _modules = AppSettings.LoadWidgetModules();

        BuildButtons();

        _hWnd = WindowNative.GetWindowHandle(this);

        // Transparent title bar
        ExtendsContentIntoTitleBar = false;
        var transparent = new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
        AppWindow.TitleBar.BackgroundColor = transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = transparent;

        // Presenter setup
        var presenter = GetOverlappedPresenter();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;

        // Win32 styling
        RemoveWindowBorder();
        DisableRoundedCorners();
        SetWindowStyles();

        // Subclass to keep TOPMOST and handle activation
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(_hWnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);

        // Apply backdrop and theme
        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);

        // Right-click context menu
        RootGrid.AddHandler(
            UIElement.RightTappedEvent,
            (RightTappedEventHandler)RootGrid_RightTapped,
            handledEventsToo: true);

        // Position timer - keep placement tight
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => EnsureTaskbarPlacement();
        _positionTimer.Start();

        this.Closed += OnClosed;

        DispatcherQueue.TryEnqueue(EnsureTaskbarPlacement);
    }

    // ── Dynamic button building ─────────────────────────────────

    public void BuildButtons()
    {
        ButtonPanel.Children.Clear();
        _modules = AppSettings.LoadWidgetModules();

        var moduleDefinitions = new (string Key, string Glyph, Action<Button> OnClick)[]
        {
            ("clipboard", "\uE16F", btn => { _activeHwndBeforeFlyout = GetForegroundWindow(); BuildClipboardFlyout().ShowAt(btn); }),
            ("notes", "\uE70B", btn => { BuildNotesFlyout().ShowAt(btn); }),
            ("favorites", "\uE734", btn => { BuildFavoritesFlyout().ShowAt(btn); }),
            ("folders", "\uE8B7", btn => { BuildFoldersFlyout().ShowAt(btn); }),
            ("snippets", "\uE943", btn => { _activeHwndBeforeFlyout = GetForegroundWindow(); BuildSnippetsFlyout().ShowAt(btn); }),
        };

        foreach (var (key, glyph, onClick) in moduleDefinitions)
        {
            if (!_modules.TryGetValue(key, out var enabled) || !enabled) continue;

            var btn = new Button
            {
                Padding = new Thickness(4),
                MinWidth = 28, MinHeight = 26, MaxWidth = 28, MaxHeight = 26,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            };
            var capturedBtn = btn;
            btn.Click += (_, _) => onClick(capturedBtn);
            ButtonPanel.Children.Add(btn);
        }
    }

    private int GetWidgetWidth() => Math.Max(28, 28 * ButtonPanel.Children.Count);

    // ── Taskbar placement ────────────────────────────────────────

    private void EnsureTaskbarPlacement()
    {
        if (_hWnd == IntPtr.Zero)
            return;

        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == IntPtr.Zero)
            return;

        _taskbarHandle = taskbarHandle;
        if (!GetWindowRect(_taskbarHandle, out var taskbarRect))
            return;

        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        if (taskbarWidth <= 0 || taskbarHeight <= 0)
            return;

        // Hide when a fullscreen app is active
        if (IsFullscreenAppActive())
        {
            SetWindowPos(_hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }

        var dpiScale = GetDpiForWindow(_taskbarHandle) / 96.0;
        if (dpiScale <= 0)
            dpiScale = 1.0;

        var widthPx = Math.Max(24, (int)Math.Round(GetWidgetWidth() * dpiScale));
        var heightPx = Math.Max(10, (int)Math.Round(WindowHeight * dpiScale));

        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(widthPx, heightPx));
        }
        catch
        {
        }

        var left = ComputeLeftOffset(taskbarRect, taskbarWidth, widthPx);
        var top = Math.Max(0, (taskbarHeight - heightPx) / 2);

        SetWindowPos(_hWnd, HWND_TOPMOST,
            taskbarRect.Left + left, taskbarRect.Top + top,
            widthPx, heightPx,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private bool IsFullscreenAppActive()
    {
        var fgHwnd = GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero || fgHwnd == _hWnd) return false;

        // Ignore the desktop window (clicking the desktop should not hide the widget)
        if (fgHwnd == GetShellWindow()) return false;
        var className = new System.Text.StringBuilder(256);
        GetClassName(fgHwnd, className, className.Capacity);
        var cls = className.ToString();
        if (cls is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(fgHwnd, out var wndRect)) return false;

        // Get the monitor that contains the foreground window
        var monitor = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        // Fullscreen = window covers the entire monitor
        var screen = monitorInfo.rcMonitor;
        return wndRect.Left <= screen.Left && wndRect.Top <= screen.Top
            && wndRect.Right >= screen.Right && wndRect.Bottom >= screen.Bottom;
    }

    private int ComputeLeftOffset(RECT taskbarRect, int taskbarWidth, int widthPx)
    {
        if (_trayHandle == IntPtr.Zero)
            _trayHandle = FindWindowEx(_taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);

        if (_trayHandle != IntPtr.Zero && GetWindowRect(_trayHandle, out var trayRect))
        {
            var trayRelativeLeft = trayRect.Left - taskbarRect.Left;
            var candidate = trayRelativeLeft - widthPx - RightPadding;
            return Math.Clamp(candidate, 0, Math.Max(0, taskbarWidth - widthPx));
        }

        return Math.Max(0, taskbarWidth - widthPx - FallbackRightPadding);
    }

    // ── Subclass proc ────────────────────────────────────────────

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_WINDOWPOSCHANGING)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.hwndInsertAfter = HWND_TOPMOST;
            wp.flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }

        // Keep backdrop active even when shell focus changes.
        if (uMsg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) == 0)
            return DefSubclassProc(hWnd, uMsg, (IntPtr)1, lParam);
        if (uMsg == WM_NCACTIVATE)
            return DefSubclassProc(hWnd, uMsg, (IntPtr)1, lParam);

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Win32 helpers ────────────────────────────────────────────

    private OverlappedPresenter GetOverlappedPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter existing)
            return existing;
        var presenter = OverlappedPresenter.Create();
        AppWindow.SetPresenter(presenter);
        return presenter;
    }

    private void RemoveWindowBorder()
    {
        var style = (long)GetWindowLongPtr(_hWnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        SetWindowLongPtr(_hWnd, GWL_STYLE, (IntPtr)style);
        SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
    }

    private void DisableRoundedCorners()
    {
        int preference = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hWnd, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference, sizeof(int));
    }

    private void SetWindowStyles()
    {
        var exStyle = (long)GetWindowLongPtr(_hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hWnd, GWL_EXSTYLE, (IntPtr)exStyle);
    }

    // ── Clipboard flyout ─────────────────────────────────────────

    public event Action<string>? OpenNoteRequested;

    private Flyout BuildClipboardFlyout()
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(260, 340);

        var panel = new StackPanel { Spacing = 0 };

        // Header
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("clipboard_history"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 8, 10, 4),
            Opacity = 0.6
        });
        panel.Children.Add(CreateDivider());

        var entries = _clipboardHistory.GetSorted();
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_clipboard_entries"),
                FontSize = 12,
                Opacity = 0.4,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
        else
        {
            var count = Math.Min(entries.Count, 20);
            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];

                if (entry.ContentType == "image" && entry.ImageData != null)
                {
                    var imgEntry = entry;
                    var imgCard = CreateImageFlyoutItem(imgEntry);
                    imgCard.Tapped += (_, _) =>
                    {
                        flyout.Hide();
                        PasteToActiveWindow(() => CopyEntryToClipboard(imgEntry));
                    };
                    panel.Children.Add(imgCard);
                }
                else
                {
                    var preview = entry.TextContent?.Length > 50
                        ? entry.TextContent[..50].Replace("\n", " ") + "..."
                        : entry.TextContent?.Replace("\n", " ") ?? "";

                    var btn = CreateFlyoutButton(preview);
                    btn.Click += (_, _) =>
                    {
                        flyout.Hide();
                        PasteToActiveWindow(() => CopyEntryToClipboard(entry));
                    };
                    panel.Children.Add(btn);
                }
            }
        }

        // Clear all
        panel.Children.Add(CreateDivider());
        var clearBtn = CreateFlyoutButton(Lang.T("clipboard_clear_all"), "\uE74D", isDestructive: true);
        clearBtn.Click += (_, _) =>
        {
            _clipboardHistory.Clear();
            flyout.Hide();
        };
        panel.Children.Add(clearBtn);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400
        };

        flyout.Content = scrollViewer;
        return flyout;
    }

    // ── Favorites flyout ─────────────────────────────────────────

    private Flyout BuildFavoritesFlyout()
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(260, 340);

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("favorites"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 8, 10, 4),
            Opacity = 0.6
        });
        panel.Children.Add(CreateDivider());

        var favorites = _notesManager?.Notes
            .Where(n => n.IsFavorite && !n.IsArchived)
            .OrderByDescending(n => n.UpdatedAt)
            .ToList() ?? [];

        if (favorites.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_favorites"),
                FontSize = 12,
                Opacity = 0.4,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
        else
        {
            foreach (var note in favorites)
            {
                var noteId = note.Id;
                var title = string.IsNullOrWhiteSpace(note.Title) ? Lang.T("untitled") : note.Title;
                var btn = CreateFlyoutButton(title, "\uE734");
                btn.Click += (_, _) =>
                {
                    flyout.Hide();
                    OpenNoteRequested?.Invoke(noteId);
                };
                panel.Children.Add(btn);
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400
        };

        flyout.Content = scrollViewer;
        return flyout;
    }

    // ── Notes flyout ─────────────────────────────────────────────

    private Flyout BuildNotesFlyout()
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(260, 340);

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("notes"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 8, 10, 4),
            Opacity = 0.6
        });
        panel.Children.Add(CreateDivider());

        var recentNotes = _notesManager?.Notes
            .Where(n => !n.IsArchived)
            .OrderByDescending(n => n.UpdatedAt)
            .Take(15)
            .ToList() ?? [];

        if (recentNotes.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_notes_found"),
                FontSize = 12,
                Opacity = 0.4,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
        else
        {
            foreach (var note in recentNotes)
            {
                var noteId = note.Id;
                var title = string.IsNullOrWhiteSpace(note.Title) ? Lang.T("untitled") : note.Title;
                var btn = CreateFlyoutButton(title, "\uE70B");
                btn.Click += (_, _) =>
                {
                    flyout.Hide();
                    OpenNoteRequested?.Invoke(noteId);
                };
                panel.Children.Add(btn);
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400
        };

        flyout.Content = scrollViewer;
        return flyout;
    }

    // ── Folders flyout ───────────────────────────────────────────

    private Flyout BuildFoldersFlyout()
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(260, 340);

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("folders"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 8, 10, 4),
            Opacity = 0.6
        });
        panel.Children.Add(CreateDivider());

        var folders = _notesManager?.GetAllFolders() ?? [];

        if (folders.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_folders"),
                FontSize = 12,
                Opacity = 0.4,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
        else
        {
            foreach (var folder in folders)
            {
                // Folder header
                panel.Children.Add(new TextBlock
                {
                    Text = folder,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(10, 6, 10, 2),
                    Opacity = 0.5
                });

                var notesInFolder = _notesManager?.Notes
                    .Where(n => !n.IsArchived && string.Equals(n.Folder, folder, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.UpdatedAt)
                    .ToList() ?? [];

                if (notesInFolder.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Lang.T("no_notes_folder", folder),
                        FontSize = 12,
                        Opacity = 0.4,
                        Padding = new Thickness(18, 4, 10, 4)
                    });
                }
                else
                {
                    foreach (var note in notesInFolder)
                    {
                        var noteId = note.Id;
                        var title = string.IsNullOrWhiteSpace(note.Title) ? Lang.T("untitled") : note.Title;
                        var btn = CreateFlyoutButton(title, "\uE70B");
                        btn.Padding = new Thickness(18, 6, 10, 6);
                        btn.Click += (_, _) =>
                        {
                            flyout.Hide();
                            OpenNoteRequested?.Invoke(noteId);
                        };
                        panel.Children.Add(btn);
                    }
                }
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400
        };

        flyout.Content = scrollViewer;
        return flyout;
    }

    // ── Snippets flyout ──────────────────────────────────────────

    private Flyout BuildSnippetsFlyout()
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(260, 340);

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("snippet"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 8, 10, 4),
            Opacity = 0.6
        });
        panel.Children.Add(CreateDivider());

        var categories = _snippetManager.GetAllCategories();

        if (categories.Count == 0 && _snippetManager.Snippets.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_snippets"),
                FontSize = 12,
                Opacity = 0.4,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
        else
        {
            // Snippets with categories
            foreach (var category in categories)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = category,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(10, 6, 10, 2),
                    Opacity = 0.5
                });

                var snippetsInCat = _snippetManager.Snippets
                    .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var snippet in snippetsInCat)
                {
                    var s = snippet;
                    var label = $"{s.Keyword}: {(s.Content.Length > 40 ? s.Content[..40] + "..." : s.Content)}";
                    var btn = CreateFlyoutButton(label, "\uE943");
                    btn.Click += (_, _) =>
                    {
                        flyout.Hide();
                        PasteToActiveWindow(() =>
                        {
                            var dp = new DataPackage();
                            dp.SetText(s.Content);
                            Clipboard.SetContent(dp);
                        });
                    };
                    panel.Children.Add(btn);
                }
            }

            // Snippets without categories
            var uncategorized = _snippetManager.Snippets
                .Where(s => string.IsNullOrEmpty(s.Category))
                .ToList();

            if (uncategorized.Count > 0)
            {
                foreach (var snippet in uncategorized)
                {
                    var s = snippet;
                    var label = $"{s.Keyword}: {(s.Content.Length > 40 ? s.Content[..40] + "..." : s.Content)}";
                    var btn = CreateFlyoutButton(label, "\uE943");
                    btn.Click += (_, _) =>
                    {
                        flyout.Hide();
                        PasteToActiveWindow(() =>
                        {
                            var dp = new DataPackage();
                            dp.SetText(s.Content);
                            Clipboard.SetContent(dp);
                        });
                    };
                    panel.Children.Add(btn);
                }
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400
        };

        flyout.Content = scrollViewer;
        return flyout;
    }

    // ── Right-click context menu ─────────────────────────────────

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE711", Lang.T("close"), [], () => Close(), IsDestructive: true)
        };

        var flyout = ActionPanel.Create(Lang.T("widget"), actions);
        flyout.ShouldConstrainToRootBounds = false;
        flyout.ShowAt(RootGrid);
        e.Handled = true;
    }

    // ── UI helpers ───────────────────────────────────────────────

    private static Button CreateFlyoutButton(string label, string? glyph = null, bool isDestructive = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph != null)
            sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 280
        });

        var btn = new Button
        {
            Content = sp,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
        };

        if (isDestructive)
            btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);

        return btn;
    }

    private static Border CreateImageFlyoutItem(ClipboardHistoryEntry entry)
    {
        var img = new Image
        {
            MaxHeight = 48,
            MaxWidth = 200,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Load thumbnail from byte array
        _ = LoadImageFromBytes(img, entry.ImageData!);

        var border = new Border
        {
            Child = img,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CanDrag = true,
        };

        border.DragStarting += async (s, args) =>
        {
            args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            var tempPath = Path.Combine(Path.GetTempPath(), $"NoteUI_clip_{entry.Id[..8]}.png");
            await File.WriteAllBytesAsync(tempPath, entry.ImageData!);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tempPath);
            args.Data.SetStorageItems(new[] { file });
        };

        return border;
    }

    private static async Task LoadImageFromBytes(Image img, byte[] data)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(stream);
            img.Source = bmp;
        }
        catch { }
    }

    private static Border CreateDivider()
    {
        return new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(6, 2, 6, 2)
        };
    }

    private void PasteToActiveWindow(Action copyAction)
    {
        var targetHwnd = _activeHwndBeforeFlyout;
        copyAction();
        Task.Delay(100).ContinueWith(_ =>
        {
            SetForegroundWindow(targetHwnd);
            const byte VK_CONTROL = 0x11;
            const byte VK_V = 0x56;
            const uint KEYEVENTF_KEYUP = 0x02;
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        });
    }

    private static void CopyEntryToClipboard(ClipboardHistoryEntry entry)
    {
        var dp = new DataPackage();
        if (entry.ContentType == "image" && entry.ImageData != null)
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            stream.WriteAsync(entry.ImageData.AsBuffer()).AsTask().Wait();
            stream.Seek(0);
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
        }
        else
        {
            if (entry.RtfContent != null) dp.SetRtf(entry.RtfContent);
            if (entry.TextContent != null) dp.SetText(entry.TextContent);
        }
        Clipboard.SetContent(dp);
    }

    // ── Cleanup ──────────────────────────────────────────────────

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _positionTimer?.Stop();
        _acrylicController?.Dispose();
        if (_subclassProc != null)
            RemoveWindowSubclass(_hWnd, _subclassProc, IntPtr.Zero);
    }

    // ── P/Invoke ─────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }
}
