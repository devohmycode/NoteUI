using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace NoteUI;

public sealed partial class MainWindow : Window
{
    private readonly NotesManager _notes = new();
    private readonly List<NoteWindow> _openNoteWindows = [];
    private ReminderService? _reminderService;

    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private TrayIcon? _trayIcon;
    private ClipboardMonitor? _clipboardMonitor;
    private bool _isExiting;
    private AcrylicSettingsWindow? _acrylicSettingsWindow;
    private VoiceNoteWindow? _voiceNoteWindow;

    private NotepadWindow? _notepadWindow;

    private readonly SnippetManager _snippetManager = new();
    private TextExpansionService? _textExpansion;

    private AiManager? _aiManager;
    private HotkeyService? _hotkeyService;

    private WindowAttachmentService? _attachmentService;
    private readonly Dictionary<string, NoteWindow> _attachedNoteWindows = new();
    private readonly Dictionary<string, IntPtr> _attachedTargetHwnds = new();

    private enum ViewMode { Notes, Favorites, Tags, TagFilter, Folders, FolderFilter, Archive }
    private ViewMode _currentView = ViewMode.Notes;
    private string? _currentTagFilter;
    private string? _currentFolderFilter;

    private bool _isPinned;
    private bool _isCompact;
    private const int DefaultFullHeight = 650;
    private const int CompactHeight = 40;
    private Microsoft.UI.Xaml.DispatcherTimer? _animTimer;
    private int _preCompactWidth = 380;
    private int _preCompactHeight = DefaultFullHeight;
    private int _targetHeight;
    private int _currentAnimHeight;

    // Inline note expansion
    private string? _expandedNoteId;
    private int _expandedCardIndex = -1;
    private RichEditBox? _expandedEditor;
    private bool _suppressInlineTextChanged;
    private bool _inlineEditorDirty;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    public MainWindow()
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = true;

        WindowHelper.RemoveWindowBorderKeepResize(this);
        var savedMainPos = AppSettings.LoadMainWindowPosition();
        if (savedMainPos is { } pos)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(pos.W, pos.H));
            _preCompactWidth = pos.W;
            _preCompactHeight = pos.H;
            WindowHelper.MoveToVisibleArea(this, pos.X, pos.Y);
        }
        else
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(380, 650));
            WindowHelper.MoveToBottomRight(this);
        }
        WindowShadow.Apply(this);
        WindowHelper.AddResizeGrips(this);

        // Set window icon (for taskbar / Alt+Tab)
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Apply saved settings
        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);
        if (Content is FrameworkElement rootFe)
        {
            ThemeHelper.Initialize(rootFe);
            rootFe.ActualThemeChanged += (_, _) => RefreshCurrentView();
        }

        // System tray icon
        _trayIcon = new TrayIcon(this, "Notes");
        _trayIcon.ShowRequested += () =>
        {
            AppWindow.Show(true);
        };
        _trayIcon.ExitRequested += ExitApplication;

        // Clipboard source tracking
        _clipboardMonitor = new ClipboardMonitor(this);

        // Global hotkeys
        RegisterGlobalHotkeys();

        // Collapse expanded note when window loses focus
        this.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                CollapseExpandedNote();
        };

        // Hide to tray instead of closing
        AppWindow.Closing += (_, args) =>
        {
            SaveMainWindowPosition();
            SaveNotePositions();
            if (!_isExiting)
            {
                SaveOpenedSecondaryWindows();
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        this.Closed += (_, _) =>
        {
            SaveMainWindowPosition();
            StopFirebaseListener();
            _attachmentService?.Dispose();
            foreach (var w in _attachedNoteWindows.Values.ToList())
            {
                try { w.Close(); } catch { }
            }
            _attachedNoteWindows.Clear();
            _attachedTargetHwnds.Clear();
            _textExpansion?.Stop();
            _reminderService?.Dispose();
            ReminderService.Shutdown();
            _hotkeyService?.Dispose();
            _acrylicController?.Dispose();
            _clipboardMonitor?.Dispose();
            _trayIcon?.Dispose();
            Environment.Exit(0);
        };

        _notes.Load();
        _snippetManager.Load();
        _textExpansion = new TextExpansionService(_snippetManager);
        _textExpansion.Start();
        ApplyLocalization();
        RefreshNotesList();
        SetCompactState(AppSettings.LoadMainWindowCompact(), animate: false);
        RestorePersistedWindows();

        _attachmentService = new WindowAttachmentService(_notes);
        _attachmentService.ShowRequested += OnAttachedNoteShow;
        _attachmentService.HideRequested += OnAttachedNoteHide;
        _attachmentService.Start();

        _reminderService = new ReminderService(_notes);
        _reminderService.ReminderFired += () => DispatcherQueue.TryEnqueue(() => RefreshCurrentView());

        // Auto-connect cloud sync if previously configured
        _ = InitCloudSync();
    }

    private void ApplyLocalization()
    {
        ToolTipService.SetToolTip(AddButton, Lang.T("tip_new"));
        ToolTipService.SetToolTip(CompactButton, Lang.T("tip_compact"));
        ToolTipService.SetToolTip(PinButton, Lang.T("tip_pin"));
        ToolTipService.SetToolTip(SettingsButton, Lang.T("tip_settings"));
        ToolTipService.SetToolTip(SyncButton, Lang.T("tip_sync"));
        ToolTipService.SetToolTip(CloseButton, Lang.T("tip_close"));
        ToolTipService.SetToolTip(QuickAccessButton, Lang.T("tip_quick_access"));
        SearchBox.PlaceholderText = Lang.T("search");
        TitleLabel.Text = Lang.T("notes");
    }

    private async Task InitCloudSync()
    {
        await _notes.InitFirebaseFromSettings();
        await _notes.InitWebDavFromSettings();
        UpdateSyncButtonVisibility();
        RefreshCurrentView();
        StartFirebaseListener();
    }

    /// <summary>Real-time SSE listener on RTDB notes, replaces periodic polling.</summary>
    private void StartFirebaseListener()
    {
        StopFirebaseListener();
        if (_notes.Firebase is not { IsConnected: true } fb) return;
        fb.StartListening(() =>
        {
            DispatcherQueue.TryEnqueue(() => _ = PullFirebaseInBackgroundAsync());
        });
    }

    private void StopFirebaseListener()
    {
        _notes.Firebase?.StopListening();
    }

    private async Task PullFirebaseInBackgroundAsync()
    {
        if (_notes.IsSyncing) return;
        if (_notes.Firebase is not { IsConnected: true }) return;
        await _notes.SyncFromFirebase();
        RefreshOpenNoteWindows();
        RefreshCurrentView();
    }

    private void RefreshOpenNoteWindows()
    {
        foreach (var w in _openNoteWindows)
        {
            var updated = _notes.Notes.FirstOrDefault(n => n.Id == w.NoteId);
            if (updated != null)
                w.ReloadAfterSync(updated);
        }
        foreach (var (id, w) in _attachedNoteWindows)
        {
            var updated = _notes.Notes.FirstOrDefault(n => n.Id == id);
            if (updated != null)
                w.ReloadAfterSync(updated);
        }
    }

    private void UpdateSyncButtonVisibility()
    {
        SyncButton.Visibility = _notes.Firebase is { IsConnected: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            await _notes.SyncSettingsFromFirebase();
            await _notes.SyncFromFirebase();
            RefreshOpenNoteWindows();
            RefreshCurrentView();
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        SaveMainWindowPosition();
        SaveNotePositions();
        SaveOpenedSecondaryWindows();
        foreach (var w in _openNoteWindows.ToList())
        {
            try { w.Close(); } catch { }
        }
        this.Close();
    }

    private void SaveNotePositions()
    {
        foreach (var w in _openNoteWindows)
        {
            try
            {
                var pos = w.AppWindow.Position;
                var sz = w.AppWindow.Size;
                var note = _notes.Notes.FirstOrDefault(n => n.Id == w.NoteId);
                if (note != null)
                {
                    note.PosX = pos.X;
                    note.PosY = pos.Y;
                    note.Width = w.IsCompact ? w.PreCompactWidth : sz.Width;
                    note.Height = w.IsCompact ? w.PreCompactHeight : sz.Height;
                    note.IsCompact = w.IsCompact;
                }
            }
            catch { }
        }
        _notes.Save();
    }

    private void RefreshNotesList(string? search = null)
    {
        ResetExpandedState();
        NotesList.Children.Clear();
        var notes = _notes.GetSorted(AppSettings.LoadSortPreference()).Where(n => !n.IsArchived).ToList();

        if (!string.IsNullOrEmpty(search))
        {
            notes = notes.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        var index = 0;
        foreach (var note in notes)
        {
            var card = CreateNoteCard(note);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void SaveMainWindowPosition()
    {
        AppSettings.SaveMainWindowPosition(AppWindow.Position, AppWindow.Size);
        AppSettings.SaveMainWindowCompact(_isCompact);
    }

    private void SaveOpenedSecondaryWindows()
    {
        var windows = new List<PersistedWindowState>();
        foreach (var noteWindow in _openNoteWindows.ToList())
        {
            var pos = noteWindow.AppWindow.Position;
            windows.Add(new PersistedWindowState("note", noteWindow.NoteId, pos.X, pos.Y, noteWindow.IsCompact));
        }

        if (_notepadWindow != null)
        {
            var pos = _notepadWindow.AppWindow.Position;
            windows.Add(new PersistedWindowState("notepad", "", pos.X, pos.Y));
        }

        AppSettings.SaveOpenWindows(windows);
    }

    private void RestorePersistedWindows()
    {
        var windows = AppSettings.LoadOpenWindows();
        if (windows.Count == 0)
            return;

        var notepadRestored = false;
        foreach (var window in windows)
        {
            if (window.Type == "note" && !string.IsNullOrWhiteSpace(window.NoteId))
            {
                var noteForRestore = _notes.Notes.FirstOrDefault(n => n.Id == window.NoteId);
                if (noteForRestore?.IsLocked == true)
                    continue;
                OpenNote(window.NoteId);
                var opened = _openNoteWindows.FirstOrDefault(w => w.NoteId == window.NoteId);
                if (opened != null)
                {
                    opened.SetCompactState(window.IsCompact, animate: false);
                    WindowHelper.MoveToVisibleArea(opened, window.X, window.Y);
                }
                continue;
            }

            if (window.Type == "notepad" && !notepadRestored)
            {
                OpenNotepad();
                if (_notepadWindow != null)
                    WindowHelper.MoveToVisibleArea(_notepadWindow, window.X, window.Y);
                notepadRestored = true;
            }
        }
    }

    private UIElement CreateNoteCard(NoteEntry note)
    {
        if (AppSettings.LoadCompactCards())
            return CreateCompactNoteCard(note);

        var color = NoteColors.Get(note.Color);
        var hasColor = !NoteColors.IsNone(note.Color);
        var isFull = hasColor && AppSettings.LoadNoteStyle() == "full";

        var cardColor = isFull
            ? color
            : hasColor
                ? ThemeHelper.CardBackgroundWithColor(color)
                : ThemeHelper.CardBackground;
        var cardBg = new SolidColorBrush(cardColor);

        var outer = new Button
        {
            Background = cardBg,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        if (isFull)
        {
            var blackBrush = new SolidColorBrush(Microsoft.UI.Colors.Black);
            var hoverBg = new SolidColorBrush(NoteColors.GetDarker(note.Color, 0.93));
            var pressedBg = new SolidColorBrush(NoteColors.GetDarker(note.Color, 0.88));
            outer.Foreground = blackBrush;
            outer.Resources["ButtonForeground"] = blackBrush;
            outer.Resources["ButtonForegroundPointerOver"] = blackBrush;
            outer.Resources["ButtonForegroundPressed"] = blackBrush;
            outer.Resources["ButtonBackground"] = cardBg;
            outer.Resources["ButtonBackgroundPointerOver"] = hoverBg;
            outer.Resources["ButtonBackgroundPressed"] = pressedBg;
        }

        var stack = new StackPanel();

        // Thin color bar at top (skip in full color mode — entire card is colored)
        if (hasColor && !isFull)
        {
            stack.Children.Add(new Border
            {
                Height = 4,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
            });
        }

        var content = new StackPanel { Padding = new Thickness(14, 10, 14, 14) };

        // Source row (clipboard origin)
        if (!string.IsNullOrEmpty(note.SourceExePath))
        {
            var sourcePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var sourceIcon = new Image
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            _ = IconHelper.LoadIconAsync(sourceIcon, note.SourceExePath);
            sourcePanel.Children.Add(sourceIcon);

            var sourceLabel = !string.IsNullOrEmpty(note.SourceTitle)
                ? note.SourceTitle
                : Path.GetFileNameWithoutExtension(note.SourceExePath);
            sourcePanel.Children.Add(new TextBlock
            {
                Text = sourceLabel,
                FontSize = 11,
                Opacity = 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            });

            content.Children.Add(sourcePanel);
        }

        // Title row: bold title left, meta right
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = note.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 0);
        titleRow.Children.Add(titleText);

        var metaPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (note.IsPinned)
        {
            metaPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE718",
                FontSize = 10,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (note.IsLocked)
        {
            metaPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE72E",
                FontSize = 10,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (!string.IsNullOrEmpty(note.Folder))
        {
            var folderIcon = new FontIcon
            {
                Glyph = "\uE8B7",
                FontSize = 10,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(folderIcon, note.Folder);
            metaPanel.Children.Add(folderIcon);
        }
        if (!string.IsNullOrEmpty(note.AttachMode))
        {
            var attachIcon = new FontIcon
            {
                Glyph = "\uE723",
                FontSize = 10,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(attachIcon,
                $"{Lang.T("attached_to")} {GetAttachTargetLabel(note)}");
            metaPanel.Children.Add(attachIcon);
        }
        if (!string.IsNullOrEmpty(note.TaskProgress))
        {
            metaPanel.Children.Add(new TextBlock
            {
                Text = note.TaskProgress,
                FontSize = 11,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        metaPanel.Children.Add(new TextBlock
        {
            Text = note.DateDisplay,
            FontSize = 12,
            Opacity = 0.45,
        });
        Grid.SetColumn(metaPanel, 1);
        titleRow.Children.Add(metaPanel);

        content.Children.Add(titleRow);

        // Preview text
        if (note.IsLocked)
        {
            content.Children.Add(new TextBlock
            {
                Text = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Opacity = 0.35,
            });
        }
        else
        {
            var preview = note.Preview;
            if (!string.IsNullOrEmpty(preview))
            {
                content.Children.Add(new TextBlock
                {
                    Text = preview,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 3,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0.65,
                });
            }

            // Image thumbnail (first embedded screenshot)
            var imageBytes = note.ExtractFirstImageBytes();
            if (imageBytes != null)
            {
                var img = new Image
                {
                    MaxHeight = 60,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                content.Children.Add(img);
                _ = LoadThumbnailAsync(img, imageBytes);
            }
        }

        stack.Children.Add(content);
        outer.Content = stack;

        var noteId = note.Id;
        outer.Tag = noteId;

        DispatcherTimer? clickTimer = null;
        bool doubleClicked = false;
        outer.Click += (_, _) =>
        {
            if (doubleClicked) { doubleClicked = false; return; }
            if (clickTimer != null) return;
            clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            clickTimer.Tick += (_, _) =>
            {
                clickTimer.Stop();
                clickTimer = null;
                if (!doubleClicked) ToggleNoteExpansion(noteId);
            };
            clickTimer.Start();
        };
        outer.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            doubleClicked = true;
            if (clickTimer != null) { clickTimer.Stop(); clickTimer = null; }
            OpenNote(noteId);
        };
        outer.RightTapped += (s, e) =>
        {
            e.Handled = true;
            ShowNoteContextMenu(noteId, (FrameworkElement)s);
        };

        return outer;
    }

    private static async Task LoadThumbnailAsync(Image img, byte[] data)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelHeight = 60 };
            await bitmap.SetSourceAsync(stream);
            img.Source = bitmap;
        }
        catch { }
    }

    private static string GetAttachTargetLabel(NoteEntry note)
    {
        if (string.IsNullOrWhiteSpace(note.AttachTarget))
            return "";

        return note.AttachMode switch
        {
            "process" => note.AttachTarget!,
            "folder" => Path.GetFileName(note.AttachTarget!),
            "title" => note.AttachTarget!,
            _ => note.AttachTarget!
        };
    }

    private void ShowNoteContextMenu(string noteId, FrameworkElement target)
    {
        var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
        var pinLabel = note?.IsPinned == true ? Lang.T("unpin") : Lang.T("pin");
        var pinGlyph = note?.IsPinned == true ? "\uE77A" : "\uE718";

        var actions = new List<ActionPanel.ActionItem>
        {
            new(pinGlyph, pinLabel, [], () =>
            {
                _notes.TogglePin(noteId);
                RefreshCurrentView();
            }),
            new("\uE70F", Lang.T("edit"), [], () => OpenNote(noteId)),
            new("\uE8C8", Lang.T("duplicate"), [], () =>
            {
                var copy = _notes.DuplicateNote(noteId);
                if (copy != null)
                {
                    RefreshCurrentView();
                    OpenNote(copy.Id);
                }
            }),
            new("\uE943", Lang.T("snippet"), [], () =>
            {
                ActionPanel.ShowSnippetFlyout(target, noteId, _snippetManager, note?.Content ?? "");
            }),
            new(note?.IsLocked == true ? "\uE785" : "\uE72E",
                note?.IsLocked == true ? Lang.T("unlock") : Lang.T("lock"), [], () =>
            {
                HandleLockToggle(noteId, target);
            }),
            new("\uE723",
                note != null && !string.IsNullOrEmpty(note.AttachMode)
                    ? $"{Lang.T("attached_to")} {GetAttachTargetLabel(note)}"
                    : Lang.T("attach_to_window"), [], () =>
            {
                if (note != null)
                    ShowAttachMenuFromList(note, target);
            }),
            new("\uE7B8", note?.IsArchived == true ? Lang.T("unarchive") : Lang.T("archive"), [], () =>
            {
                _notes.ToggleArchive(noteId);
                RefreshCurrentView();
            }),
            new("\uE74D", Lang.T("delete"), [], () =>
            {
                void DoDelete()
                {
                    var existing = _openNoteWindows.FirstOrDefault(w => w.NoteId == noteId);
                    if (existing != null)
                    {
                        try { existing.Close(); } catch { }
                    }
                    _notes.DeleteNote(noteId);
                    RefreshCurrentView();
                }

                if (note?.IsLocked == true)
                {
                    var storedHash = AppSettings.LoadMasterPasswordHash();
                    if (string.IsNullOrEmpty(storedHash)) return;
                    ActionPanel.ShowEnterPasswordFlyout(target, storedHash, DoDelete);
                }
                else
                {
                    DoDelete();
                }
            }, IsDestructive: true),
        };

        var flyout = ActionPanel.Create(Lang.T("actions"), actions);
        flyout.ShowAt(target);
    }

    private void ShowAttachMenuFromList(NoteEntry note, FrameworkElement target)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE737", Lang.T("attach_to_program"), [], () =>
            {
                ShowRunningProgramsPicker(note, target);
            }),
            new("\uE774", Lang.T("attach_to_website"), [], () =>
            {
                ShowWebTabsPicker(note, target);
            }),
            new("\uE8B7", Lang.T("attach_to_folder"), [], async () =>
            {
                await PickAttachFolder(note);
            }),
        };

        if (!string.IsNullOrEmpty(note.AttachMode))
        {
            actions.Add(new("\uE711", Lang.T("detach"), [], () =>
            {
                note.AttachTarget = null;
                note.AttachMode = null;
                note.AttachOffsetX = 0;
                note.AttachOffsetY = 0;
                _notes.Save();
                _attachmentService?.Refresh();
                RefreshCurrentView();
            }, IsDestructive: true));
        }

        var flyout = ActionPanel.Create(Lang.T("attach_to_window"), actions);
        flyout.ShowAt(target);
    }

    private void ShowRunningProgramsPicker(NoteEntry note, FrameworkElement target)
    {
        var programs = WindowAttachmentHelper.GetVisibleWindows();
        if (programs.Count == 0)
        {
            var empty = ActionPanel.Create(Lang.T("select_program"),
                [new(null, Lang.T("no_windows_found"), [], () => { })]);
            empty.ShowAt(target);
            return;
        }

        var actions = programs.Select(p =>
            new ActionPanel.ActionItem(null, $"{p.ProcessName}  —  {p.Title}", [], () =>
            {
                note.AttachTarget = p.ProcessName;
                note.AttachMode = "process";
                note.AttachOffsetX = 0;
                note.AttachOffsetY = 0;
                _notes.Save();
                _attachmentService?.Refresh();
                RefreshCurrentView();
            })
        ).ToList();

        var flyout = ActionPanel.Create(Lang.T("select_program"), actions);
        flyout.ShowAt(target);
    }

    private void ShowWebTabsPicker(NoteEntry note, FrameworkElement target)
    {
        var tabs = WindowAttachmentHelper.GetVisibleWebTabs();
        if (tabs.Count == 0)
        {
            var empty = ActionPanel.Create(Lang.T("select_website"),
                [new(null, Lang.T("no_web_tabs_found"), [], () => { })]);
            empty.ShowAt(target);
            return;
        }

        var actions = tabs.Select(tab =>
            new ActionPanel.ActionItem(null, $"{tab.ProcessName}  —  {tab.Title}", [], () =>
            {
                note.AttachTarget = tab.Title;
                note.AttachMode = "title";
                note.AttachOffsetX = 0;
                note.AttachOffsetY = 0;
                _notes.Save();
                _attachmentService?.Refresh();
                RefreshCurrentView();
            })
        ).ToList();

        var flyout = ActionPanel.Create(Lang.T("select_website"), actions);
        flyout.ShowAt(target);
    }

    private async Task PickAttachFolder(NoteEntry note)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        note.AttachTarget = folder.Path;
        note.AttachMode = "folder";
        note.AttachOffsetX = 0;
        note.AttachOffsetY = 0;
        _notes.Save();
        _attachmentService?.Refresh();
        RefreshCurrentView();
    }

    private void HandleLockToggle(string noteId, FrameworkElement target)
    {
        var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        if (note.IsLocked)
        {
            var storedHash = AppSettings.LoadMasterPasswordHash();
            if (string.IsNullOrEmpty(storedHash)) return;
            ActionPanel.ShowEnterPasswordFlyout(target, storedHash, () =>
            {
                _notes.ToggleLock(noteId);
                RefreshCurrentView();
            });
            return;
        }

        if (!AppSettings.HasMasterPassword())
        {
            ActionPanel.ShowCreatePasswordFlyout(target, password =>
            {
                var hash = AppSettings.HashPassword(password);
                AppSettings.SaveMasterPasswordHash(hash);
                _notes.ToggleLock(noteId);
                RefreshCurrentView();
                _ = _notes.SyncSettingsToFirebase();
            });
        }
        else
        {
            _notes.ToggleLock(noteId);
            RefreshCurrentView();
        }
    }

    private void OpenNote(string noteId)
    {
        var existing = _openNoteWindows.FirstOrDefault(w => w.NoteId == noteId);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        if (note.IsLocked)
        {
            var storedHash = AppSettings.LoadMasterPasswordHash();
            if (string.IsNullOrEmpty(storedHash)) return;
            ActionPanel.ShowEnterPasswordFlyout(NotesList, storedHash, () =>
            {
                OpenNoteUnlocked(noteId);
            });
            return;
        }

        OpenNoteUnlocked(noteId);
    }

    private void OpenNoteUnlocked(string noteId)
    {
        var existing = _openNoteWindows.FirstOrDefault(w => w.NoteId == noteId);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        var pos = AppWindow.Position;
        var sz = AppWindow.Size;
        var parentRect = new NoteWindow.ParentRect(pos.X, pos.Y, sz.Width, sz.Height);
        var window = new NoteWindow(_notes, note, parentRect);
        window.SetSnippetManager(_snippetManager);
        window.RefreshAiUi();
        _openNoteWindows.Add(window);
        window.NoteChanged += () =>
        {
            RefreshCurrentView();
            // Keep snippet content in sync
            var n = _notes.Notes.FirstOrDefault(n => n.Id == window.NoteId);
            if (n != null) _snippetManager.UpdateContent(n.Id, n.Content);
        };
        window.OpenInNotepadRequested += () =>
        {
            var note = _notes.Notes.FirstOrDefault(n => n.Id == window.NoteId);
            if (note == null) return;
            OpenNotepad();
            _notepadWindow?.LoadNoteContent(note.Title, note.Content, note.Id);
        };
        window.NoteLinkClicked += noteId => OpenNote(noteId);
        window.AttachmentChanged += () => HandleNoteAttachmentChanged(window);
        window.Closed += (_, _) =>
        {
            try
            {
                var p = window.AppWindow.Position;
                var s = window.AppWindow.Size;
                var n = _notes.Notes.FirstOrDefault(n => n.Id == window.NoteId);
                if (n != null)
                {
                    n.PosX = p.X;
                    n.PosY = p.Y;
                    n.Width = window.IsCompact ? window.PreCompactWidth : s.Width;
                    n.Height = window.IsCompact ? window.PreCompactHeight : s.Height;
                    n.IsCompact = window.IsCompact;
                    _notes.Save();
                }
            }
            catch { }
            _openNoteWindows.Remove(window);
            RefreshCurrentView();
        };

        if (note.PosX.HasValue && note.PosY.HasValue)
            WindowHelper.MoveToVisibleArea(window, note.PosX.Value, note.PosY.Value);
        if (note.IsCompact && note.Width.HasValue && note.Height.HasValue)
        {
            // Restore full size first so pre-compact values are correct, then compact
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(note.Width.Value, note.Height.Value));
            window.SetCompactState(true, animate: false);
        }
        else if (note.Width.HasValue && note.Height.HasValue)
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(note.Width.Value, note.Height.Value));
        }

        window.Activate();
    }

    private void HandleNoteAttachmentChanged(NoteWindow window)
    {
        var note = _notes.Notes.FirstOrDefault(n => n.Id == window.NoteId);
        if (note == null) return;

        if (string.IsNullOrEmpty(note.AttachMode))
        {
            // Detached — un-pin if was pinned by attachment
            window.SetPinnedOnTop(false);
            return;
        }

        if (note.AttachMode == "folder")
        {
            // Check if the folder is currently open in Explorer
            var openPaths = WindowAttachmentService.GetOpenExplorerPaths();
            string normalizedTarget;
            try { normalizedTarget = Path.GetFullPath(note.AttachTarget!); }
            catch { return; }

            var match = openPaths.FirstOrDefault(p =>
                WindowAttachmentService.IsSamePath(p.Path, normalizedTarget));

            if (match.Path != null)
            {
                // Folder is open — pin on top and move to bottom-right of Explorer
                window.SetPinnedOnTop(true);
                var noteSize = window.AppWindow.Size;
                var pos = WindowAttachmentService.GetAttachedPosition(note, match.Hwnd, noteSize.Width, noteSize.Height);
                if (pos != null)
                    WindowHelper.MoveToVisibleArea(window, pos.Value.X, pos.Value.Y);
            }
            else
            {
                // Folder not open — close the note
                try { window.Close(); } catch { }
            }
        }
        else if (note.AttachMode == "process")
        {
            // Check if the process is currently foreground
            window.SetPinnedOnTop(true);
            _attachmentService?.Refresh();
        }
        else if (note.AttachMode == "title")
        {
            // Check if the foreground window title matches
            window.SetPinnedOnTop(true);
            _attachmentService?.Refresh();
        }
    }

    // ── Window attachment ───────────────────────────────────────

    private void SaveAttachedOffset(NoteEntry note, NoteWindow window, string noteId)
    {
        if (string.IsNullOrEmpty(note.AttachMode))
            return;

        if (!_attachedTargetHwnds.TryGetValue(noteId, out var targetHwnd))
            return;

        if (targetHwnd == IntPtr.Zero)
            return;

        var pos = window.AppWindow.Position;
        WindowAttachmentService.SaveAttachOffset(note, targetHwnd, pos.X, pos.Y);
        _notes.Save();
    }

    private void OnAttachedNoteShow(NoteEntry note, IntPtr targetHwnd)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Skip if already open manually or as attached
            if (_openNoteWindows.Any(w => w.NoteId == note.Id)) return;
            if (_attachedNoteWindows.ContainsKey(note.Id))
            {
                _attachedTargetHwnds[note.Id] = targetHwnd;
                return;
            }
            if (note.IsLocked) return;

            var window = new NoteWindow(_notes, note);
            window.SetSnippetManager(_snippetManager);
            window.RefreshAiUi();

            // Always on top so the note stays above the Explorer/target window
            window.SetPinnedOnTop(true);

            // Position relative to target window (bottom-right by default)
            var noteSize = window.AppWindow.Size;
            var pos = WindowAttachmentService.GetAttachedPosition(note, targetHwnd, noteSize.Width, noteSize.Height);
            if (pos != null)
                WindowHelper.MoveToVisibleArea(window, pos.Value.X, pos.Value.Y);

            window.NoteChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                RefreshCurrentView();
                _attachmentService?.Refresh();
            });
            window.Closed += (_, _) =>
            {
                // Save relative offset before removing
                SaveAttachedOffset(note, window, note.Id);
                _attachedNoteWindows.Remove(note.Id);
                _attachedTargetHwnds.Remove(note.Id);
            };

            _attachedNoteWindows[note.Id] = window;
            _attachedTargetHwnds[note.Id] = targetHwnd;
            window.Activate();
        });
    }

    private void OnAttachedNoteHide(string noteId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_attachedNoteWindows.TryGetValue(noteId, out var window)) return;

            // Save relative offset
            var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
            if (note != null)
            {
                SaveAttachedOffset(note, window, noteId);
            }

            _attachedTargetHwnds.Remove(noteId);
            _attachedNoteWindows.Remove(noteId);
            try { window.Close(); } catch { }
        });
    }

    // ── Inline note expansion ──────────────────────────────────

    private void ToggleNoteExpansion(string noteId)
    {
        if (_expandedNoteId == noteId)
        {
            CollapseExpandedNote();
            return;
        }

        // Don't expand if already open in a NoteWindow
        if (_openNoteWindows.Any(w => w.NoteId == noteId))
        {
            _openNoteWindows.First(w => w.NoteId == noteId).Activate();
            return;
        }

        CollapseExpandedNote();

        var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        // Locked notes open in a separate window
        if (note.IsLocked) { OpenNote(noteId); return; }

        for (int i = 0; i < NotesList.Children.Count; i++)
        {
            if (NotesList.Children[i] is FrameworkElement fe && fe.Tag as string == noteId)
            {
                _expandedCardIndex = i;
                var expandedCard = CreateExpandedCard(note);
                NotesList.Children[i] = expandedCard;
                _expandedNoteId = noteId;
                AnimationHelper.FadeIn(expandedCard, durationMs: 200);
                return;
            }
        }
    }

    private void CollapseExpandedNote()
    {
        if (_expandedNoteId == null || _expandedCardIndex < 0) return;

        var noteId = _expandedNoteId;
        var cardIndex = _expandedCardIndex;
        var wasDirty = _inlineEditorDirty;

        SaveInlineEditor();
        ResetExpandedState();

        if (wasDirty)
        {
            // Content was modified — full refresh to apply correct sort order
            RefreshCurrentView();
        }
        else
        {
            // No changes — just replace expanded card with normal card
            var note = _notes.Notes.FirstOrDefault(n => n.Id == noteId);
            if (note != null && cardIndex < NotesList.Children.Count)
            {
                var card = CreateNoteCard(note);
                NotesList.Children[cardIndex] = card;
            }
        }
    }

    private void ResetExpandedState()
    {
        _expandedNoteId = null;
        _expandedCardIndex = -1;
        _expandedEditor = null;
        _inlineEditorDirty = false;
    }

    private void SaveInlineEditor()
    {
        if (_expandedEditor == null || _expandedNoteId == null) return;

        _expandedEditor.Document.GetText(Microsoft.UI.Text.TextGetOptions.FormatRtf, out var rtf);

        var note = _notes.Notes.FirstOrDefault(n => n.Id == _expandedNoteId);
        if (note == null) return;

        note.Content = rtf;

        if (_inlineEditorDirty)
            note.UpdatedAt = DateTime.Now;

        _notes.Save(localOnly: true);
    }

    private UIElement CreateCompactNoteCard(NoteEntry note)
    {
        var color = NoteColors.Get(note.Color);
        var hasColor = !NoteColors.IsNone(note.Color);
        var isFull = hasColor && AppSettings.LoadNoteStyle() == "full";

        var cardColor = isFull
            ? color
            : hasColor
                ? ThemeHelper.CardBackgroundWithColor(color)
                : ThemeHelper.CardBackground;
        var cardBg = new SolidColorBrush(cardColor);

        var outer = new Button
        {
            Background = cardBg,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        if (isFull)
        {
            var blackBrush = new SolidColorBrush(Microsoft.UI.Colors.Black);
            outer.Foreground = blackBrush;
            outer.Resources["ButtonForeground"] = blackBrush;
            outer.Resources["ButtonForegroundPointerOver"] = blackBrush;
            outer.Resources["ButtonForegroundPressed"] = blackBrush;
            outer.Resources["ButtonBackground"] = cardBg;
            outer.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(NoteColors.GetDarker(note.Color, 0.93));
            outer.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(NoteColors.GetDarker(note.Color, 0.88));
        }

        var grid = new Grid { Padding = new Thickness(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // color dot
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icons
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // preview
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // date

        // Color dot (only in non-full mode with color)
        if (hasColor && !isFull)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
        }

        // Status icons (pin, lock, folder)
        var iconsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        if (note.IsPinned)
            iconsPanel.Children.Add(new FontIcon { Glyph = "\uE718", FontSize = 9, Opacity = 0.45 });
        if (note.IsLocked)
            iconsPanel.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 9, Opacity = 0.45 });
        if (!string.IsNullOrEmpty(note.Folder))
            iconsPanel.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 9, Opacity = 0.45 });
        if (iconsPanel.Children.Count > 0)
        {
            Grid.SetColumn(iconsPanel, 1);
            grid.Children.Add(iconsPanel);
        }

        // Title
        var titleText = new TextBlock
        {
            Text = note.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 2);
        grid.Children.Add(titleText);

        // Short preview
        if (!note.IsLocked)
        {
            var preview = note.Preview;
            if (!string.IsNullOrEmpty(preview))
            {
                // Take first line only, truncated
                var firstLine = preview.Split('\n')[0];
                if (firstLine.Length > 40) firstLine = firstLine[..40];
                var previewText = new TextBlock
                {
                    Text = firstLine,
                    FontSize = 11,
                    Opacity = 0.4,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    MaxWidth = 80,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(previewText, 3);
                grid.Children.Add(previewText);
            }
        }

        // Date
        var dateText = new TextBlock
        {
            Text = note.DateDisplay,
            FontSize = 11,
            Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(dateText, 4);
        grid.Children.Add(dateText);

        outer.Content = grid;

        var noteId = note.Id;
        outer.Tag = noteId;

        DispatcherTimer? clickTimer = null;
        bool doubleClicked = false;
        outer.Click += (_, _) =>
        {
            if (doubleClicked) { doubleClicked = false; return; }
            if (clickTimer != null) return;
            clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            clickTimer.Tick += (_, _) =>
            {
                clickTimer.Stop();
                clickTimer = null;
                if (!doubleClicked) ToggleNoteExpansion(noteId);
            };
            clickTimer.Start();
        };
        outer.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            doubleClicked = true;
            if (clickTimer != null) { clickTimer.Stop(); clickTimer = null; }
            OpenNote(noteId);
        };
        outer.RightTapped += (s, e) =>
        {
            e.Handled = true;
            ShowNoteContextMenu(noteId, (FrameworkElement)s);
        };

        return outer;
    }

    private UIElement CreateExpandedCard(NoteEntry note)
    {
        var color = NoteColors.Get(note.Color);
        var hasColor = !NoteColors.IsNone(note.Color);
        var isFull = hasColor && AppSettings.LoadNoteStyle() == "full";

        var cardColor = isFull
            ? color
            : hasColor
                ? ThemeHelper.CardBackgroundWithColor(color)
                : ThemeHelper.CardBackground;
        var cardBg = new SolidColorBrush(cardColor);

        var border = new Border
        {
            Background = cardBg,
            CornerRadius = new CornerRadius(8),
            Tag = note.Id
        };

        if (hasColor && !isFull)
        {
            border.BorderBrush = new SolidColorBrush(color);
            border.BorderThickness = new Thickness(0, 4, 0, 0);
        }

        var stack = new StackPanel();

        // ── Header: source + title ──
        var header = new StackPanel { Padding = new Thickness(14, 10, 14, 4) };

        if (!string.IsNullOrEmpty(note.SourceExePath))
        {
            var sourcePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var sourceIcon = new Image { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            _ = IconHelper.LoadIconAsync(sourceIcon, note.SourceExePath);
            sourcePanel.Children.Add(sourceIcon);
            sourcePanel.Children.Add(new TextBlock
            {
                Text = note.SourceTitle ?? Path.GetFileNameWithoutExtension(note.SourceExePath),
                FontSize = 11,
                Opacity = 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isFull ? new SolidColorBrush(Microsoft.UI.Colors.Black) : null
            });
            header.Children.Add(sourcePanel);
        }

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = note.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isFull ? new SolidColorBrush(Microsoft.UI.Colors.Black) : null
        };
        Grid.SetColumn(titleText, 0);
        titleRow.Children.Add(titleText);

        var dateText = new TextBlock
        {
            Text = note.DateDisplay,
            FontSize = 12,
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isFull ? new SolidColorBrush(Microsoft.UI.Colors.Black) : null
        };
        Grid.SetColumn(dateText, 1);
        titleRow.Children.Add(dateText);

        header.Children.Add(titleRow);
        stack.Children.Add(header);

        // ── Editor ──
        // Size editor to fill ~60% of the visible scroll area, like Sticky Notes
        var scrollHeight = NotesScroll.ActualHeight;
        var editorHeight = Math.Max(120, scrollHeight * 0.55 - 80); // subtract header+statusbar

        var editor = new RichEditBox
        {
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 4, 12, 4),
            Height = editorHeight,
        };
        var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        editor.Resources["TextControlBackground"] = transparentBrush;
        editor.Resources["TextControlBackgroundPointerOver"] = transparentBrush;
        editor.Resources["TextControlBackgroundFocused"] = transparentBrush;
        editor.Resources["TextControlBorderBrush"] = transparentBrush;
        editor.Resources["TextControlBorderBrushPointerOver"] = transparentBrush;
        editor.Resources["TextControlBorderBrushFocused"] = transparentBrush;

        // Text color: force black for full-color mode, white for dark theme otherwise
        var textColor = isFull
            ? Microsoft.UI.Colors.Black
            : ThemeHelper.IsDark() ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var textBrush = new SolidColorBrush(textColor);
        editor.Foreground = textBrush;
        editor.Resources["TextControlForeground"] = textBrush;
        editor.Resources["TextControlForegroundPointerOver"] = textBrush;
        editor.Resources["TextControlForegroundFocused"] = textBrush;

        _expandedEditor = editor;

        var capturedNoteId = note.Id;
        editor.Loaded += (_, _) =>
        {
            _suppressInlineTextChanged = true;

            var content = note.Content ?? "";
            if (string.IsNullOrEmpty(content))
            {
                editor.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, "");
            }
            else if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
            {
                // Adapt RTF text colors for current display context
                if (isFull)
                {
                    // Full-color mode: force black text on colored background
                    content = content.Replace("\\red255\\green255\\blue255", "\\red0\\green0\\blue0");
                }
                else if (ThemeHelper.IsDark())
                {
                    // Dark theme: force white text on dark background
                    content = content.Replace("\\red0\\green0\\blue0", "\\red255\\green255\\blue255");
                }
                editor.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, content);
            }
            else
            {
                editor.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, content);
            }

            _suppressInlineTextChanged = false;
            editor.Focus(FocusState.Programmatic);
        };

        editor.TextChanged += (_, _) =>
        {
            if (_suppressInlineTextChanged) return;
            _inlineEditorDirty = true;
            SaveInlineEditor();
            // Live sync with open NoteWindow
            var openWin = _openNoteWindows.FirstOrDefault(w => w.NoteId == capturedNoteId);
            openWin?.ReloadFromDisk();
        };

        stack.Children.Add(editor);

        // ── Status bar ──
        var statusBrush = isFull
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(180, 0, 0, 0))
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var statusGrid = new Grid { Padding = new Thickness(4, 2, 4, 4) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Format button → opens flyout
        var fmtBtn = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
            Content = new FontIcon { Glyph = "\uE8D2", FontSize = 12, Foreground = statusBrush }
        };
        Grid.SetColumn(fmtBtn, 0);
        fmtBtn.Click += (s, _) =>
        {
            var flyout = CreateInlineFormatFlyout(editor);
            flyout.ShowAt((FrameworkElement)s);
        };
        statusGrid.Children.Add(fmtBtn);

        // Color button
        var colorBtn = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
        };
        var colorEllipse = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush(hasColor ? color : ThemeHelper.CardBackground)
        };
        colorBtn.Content = colorEllipse;
        Grid.SetColumn(colorBtn, 2);
        colorBtn.Click += (s, _) =>
        {
            var flyout = ActionPanel.CreateColorPicker(Lang.T("color"), note.Color, colorName =>
            {
                note.Color = colorName;
                _notes.UpdateNote(note.Id, note.Content, note.Title, colorName);
                CollapseExpandedNote();
                DispatcherQueue.TryEnqueue(() => ToggleNoteExpansion(note.Id));
            });
            flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight;
            flyout.ShowAt((FrameworkElement)s);
        };
        statusGrid.Children.Add(colorBtn);

        // Open in window button
        var openBtn = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4),
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 12, Foreground = statusBrush }
        };
        Grid.SetColumn(openBtn, 3);
        openBtn.Click += (_, _) => { CollapseExpandedNote(); OpenNote(note.Id); };
        statusGrid.Children.Add(openBtn);

        stack.Children.Add(statusGrid);

        border.Child = stack;

        // Right-click context menu
        border.RightTapped += (s, e) =>
        {
            e.Handled = true;
            ShowNoteContextMenu(note.Id, (FrameworkElement)s);
        };

        // Auto-collapse when editor loses focus (click outside)
        editor.LosingFocus += (_, args) =>
        {
            // Don't collapse if focus is moving to a status bar button within this card
            if (args.NewFocusedElement is FrameworkElement newFe)
            {
                DependencyObject parent = newFe;
                while (parent != null)
                {
                    if (parent == border) return; // focus stays inside expanded card
                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
                }
            }
            DispatcherQueue.TryEnqueue(CollapseExpandedNote);
        };

        return border;
    }

    private static Flyout CreateInlineFormatFlyout(RichEditBox editor)
    {
        var fgBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        var actions = new List<ActionPanel.ActionItem>
        {
            new(null, Lang.T("bold"), ["Ctrl", "B"], () =>
            {
                var sel = editor.Document.Selection; if (sel == null) return;
                sel.CharacterFormat.Bold = sel.CharacterFormat.Bold == Microsoft.UI.Text.FormatEffect.On
                    ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
                editor.Focus(FocusState.Programmatic);
            }, Icon: new TextBlock { Text = "B", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14,
                Foreground = fgBrush, VerticalAlignment = VerticalAlignment.Center }),

            new(null, Lang.T("italic"), ["Ctrl", "I"], () =>
            {
                var sel = editor.Document.Selection; if (sel == null) return;
                sel.CharacterFormat.Italic = sel.CharacterFormat.Italic == Microsoft.UI.Text.FormatEffect.On
                    ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
                editor.Focus(FocusState.Programmatic);
            }, Icon: new TextBlock { Text = "I", FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = 14,
                Foreground = fgBrush, VerticalAlignment = VerticalAlignment.Center }),

            new(null, Lang.T("underline"), ["Ctrl", "U"], () =>
            {
                var sel = editor.Document.Selection; if (sel == null) return;
                sel.CharacterFormat.Underline = sel.CharacterFormat.Underline == Microsoft.UI.Text.UnderlineType.Single
                    ? Microsoft.UI.Text.UnderlineType.None : Microsoft.UI.Text.UnderlineType.Single;
                editor.Focus(FocusState.Programmatic);
            }, Icon: new TextBlock { Text = "S", TextDecorations = Windows.UI.Text.TextDecorations.Underline, FontSize = 14,
                Foreground = fgBrush, VerticalAlignment = VerticalAlignment.Center }),

            new(null, Lang.T("strikethrough"), [], () =>
            {
                var sel = editor.Document.Selection; if (sel == null) return;
                sel.CharacterFormat.Strikethrough = sel.CharacterFormat.Strikethrough == Microsoft.UI.Text.FormatEffect.On
                    ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On;
                editor.Focus(FocusState.Programmatic);
            }, Icon: new TextBlock { Text = "ab", TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough, FontSize = 13,
                Foreground = fgBrush, VerticalAlignment = VerticalAlignment.Center }),

            new("\uE8FD", Lang.T("bullet_list"), [], () =>
            {
                var sel = editor.Document.Selection; if (sel == null) return;
                sel.ParagraphFormat.ListType = sel.ParagraphFormat.ListType == Microsoft.UI.Text.MarkerType.Bullet
                    ? Microsoft.UI.Text.MarkerType.None : Microsoft.UI.Text.MarkerType.Bullet;
                editor.Focus(FocusState.Programmatic);
            }),
        };

        var flyout = ActionPanel.Create(Lang.T("format"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        return flyout;
    }

    private void NotesScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_expandedNoteId != null)
            CollapseExpandedNote();
    }

    // ── Events ─────────────────────────────────────────────────

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var note = _notes.CreateNote();
        ApplyClipboardSource(note);
        RefreshCurrentView();
        OpenNote(note.Id);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    private async void PasteAsNewNote()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                return;

            var text = await content.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;

            // Create note with clipboard content
            var note = _notes.CreateNote();
            ApplyClipboardSource(note);

            // Set title from first line (trimmed)
            var firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Length > 50) firstLine = firstLine[..50] + "...";
            note.Title = firstLine;

            // Set content
            note.Content = text;
            _notes.Save(localOnly: true);

            AppWindow.Show(true);
            RefreshCurrentView();
            OpenNote(note.Id);
        }
        catch { }
    }

    private void ApplyClipboardSource(NoteEntry note)
    {
        if (_clipboardMonitor?.SourceExePath == null) return;
        note.SourceExePath = _clipboardMonitor.SourceExePath;
        note.SourceTitle = ClipboardMonitor.CleanTitle(
            _clipboardMonitor.SourceTitle, _clipboardMonitor.SourceExePath);
        _notes.Save(localOnly: true);
    }

    private void OpenNotepad()
    {
        if (_notepadWindow != null)
        {
            _notepadWindow.Activate();
            return;
        }

        _notepadWindow = new NotepadWindow(_notes);
        _notepadWindow.SetSnippetManager(_snippetManager);
        _notepadWindow.RefreshAiUi();
        _notepadWindow.NoteCreated += () => DispatcherQueue.TryEnqueue(() => RefreshCurrentView());
        _notepadWindow.Closed += (_, _) =>
        {
            if (_notepadWindow != null)
            {
                var p = _notepadWindow.AppWindow.Position;
                var s = _notepadWindow.AppWindow.Size;
                AppSettings.SaveNotepadPosition(p.X, p.Y, s.Width, s.Height);
            }
            _notepadWindow = null;
        };

        var savedPos = AppSettings.LoadNotepadPosition();
        if (savedPos.HasValue)
        {
            _notepadWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(savedPos.Value.W, savedPos.Value.H));
            WindowHelper.MoveToVisibleArea(_notepadWindow, savedPos.Value.X, savedPos.Value.Y);
        }

        _notepadWindow.Activate();
    }

    private void OpenVoiceNote()
    {
        if (_voiceNoteWindow != null)
        {
            _voiceNoteWindow.Activate();
            return;
        }

        _aiManager ??= new AiManager();
        _aiManager.Load();
        _voiceNoteWindow = new VoiceNoteWindow(_notes, _aiManager);
        _voiceNoteWindow.NoteCreated += () => DispatcherQueue.TryEnqueue(() => RefreshCurrentView());
        _voiceNoteWindow.Closed += (_, _) => _voiceNoteWindow = null;
        _voiceNoteWindow.Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();

        var flyout = ActionPanel.CreateSettings(theme, settings.Type,
            _notes.CurrentFolder, AppSettings.GetDefaultNotesFolder(),
            _notes.Firebase is { IsConnected: true }, _notes.Firebase?.Email,
            _notes.WebDav is { IsConfigured: true }, _notes.WebDav?.ServerUrl,
            onThemeSelected: t =>
            {
                AppSettings.SaveThemeSetting(t);
                AppSettings.ApplyThemeToWindow(this, t, _configSource);
                foreach (var w in _openNoteWindows)
                    w.ApplyTheme(t);
                _ = _notes.SyncSettingsToFirebase();
            },
            onBackdropSelected: b =>
            {
                var current = AppSettings.LoadSettings();
                var newSettings = current with { Type = b };
                AppSettings.SaveBackdropSettings(newSettings);
                AppSettings.ApplyToWindow(this, newSettings, ref _acrylicController, ref _configSource);
                foreach (var w in _openNoteWindows)
                    w.ApplyBackdrop(newSettings);
                _ = _notes.SyncSettingsToFirebase();

                if (b == "acrylic_custom")
                    OpenAcrylicSettings();
                else
                    _acrylicSettingsWindow?.Close();
            },
            onChangeFolder: async () => await PickNotesFolder(),
            onResetFolder: () =>
            {
                _notes.ChangeFolder(AppSettings.GetDefaultNotesFolder());
                SwitchView(ViewMode.Notes);
            },
            onConfigureFirebase: async () => await ShowFirebaseConfigDialog(),
            onDisconnectFirebase: () =>
            {
                StopFirebaseListener();
                _notes.DisconnectFirebase();
                UpdateSyncButtonVisibility();
            },
            onSyncFirebase: async () =>
            {
                await _notes.SyncFromFirebase();
                RefreshOpenNoteWindows();
                RefreshCurrentView();
            },
            onConfigureWebDav: async () => await ShowWebDavConfigDialog(),
            onDisconnectWebDav: () =>
            {
                _notes.DisconnectWebDav();
            },
            onSyncWebDav: async () =>
            {
                await _notes.SyncFromWebDav();
                RefreshCurrentView();
            },
            onShowVoiceModels: f => ShowVoiceModelsInSettings(f),
            onShowShortcuts: f => ShowShortcutsInSettings(f),
            currentLanguage: Lang.Current,
            slashEnabled: AppSettings.LoadSlashEnabled(),
            onLanguageSelected: lang =>
            {
                Lang.SetLanguage(lang);
                AppSettings.SaveLanguage(lang);
                ApplyLocalization();
                RefreshCurrentView();
                _ = _notes.SyncSettingsToFirebase();
            },
            onSlashToggled: enabled =>
            {
                AppSettings.SaveSlashEnabled(enabled);
                _ = _notes.SyncSettingsToFirebase();
            },
            onShowAi: f => ShowAiInSettings(f),
            onShowPrompts: f => ShowPromptsInSettings(f),
            currentNoteStyle: AppSettings.LoadNoteStyle(),
            onNoteStyleSelected: style =>
            {
                AppSettings.SaveNoteStyle(style);
                foreach (var w in _openNoteWindows)
                    w.ApplyNoteStyle(style);
                RefreshCurrentView();
                _ = _notes.SyncSettingsToFirebase();
            },
            currentFont: AppSettings.LoadFontSetting(),
            onFontSelected: font =>
            {
                AppSettings.SaveFontSetting(font);
                App.ApplyFontResource(font);
                var fontFamily = AppSettings.GetFontFamily(font);
                if (Content is FrameworkElement root)
                    AppSettings.ApplyFontToTree(root, fontFamily);
                foreach (var w in _openNoteWindows)
                    if (w.Content is FrameworkElement noteRoot)
                        AppSettings.ApplyFontToTree(noteRoot, fontFamily);
                _ = _notes.SyncSettingsToFirebase();
            },
            onResetPassword: () =>
            {
                AppSettings.DeleteMasterPassword();
            },
            onResetNotes: () =>
            {
                foreach (var w in _openNoteWindows.ToList())
                    try { w.Close(); } catch { }
                _openNoteWindows.Clear();
                _notes.DeleteAllNotes();
                RefreshCurrentView();
            },
            startWithWindows: AppSettings.LoadStartWithWindows(),
            startMinimized: AppSettings.LoadStartMinimized(),
            onStartWithWindowsToggled: enabled =>
            {
                AppSettings.SaveStartWithWindows(enabled);
            },
            onStartMinimizedToggled: enabled =>
            {
                AppSettings.SaveStartMinimized(enabled);
            },
            currentSort: AppSettings.LoadSortPreference(),
            onSortSelected: mode =>
            {
                AppSettings.SaveSortPreference(mode);
                RefreshCurrentView();
                _ = _notes.SyncSettingsToFirebase();
            },
            compactCards: AppSettings.LoadCompactCards(),
            onCompactToggled: enabled =>
            {
                AppSettings.SaveCompactCards(enabled);
                RefreshCurrentView();
            });

        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight;
        flyout.ShowAt(sender as FrameworkElement);
    }

    private void ShowVoiceModelsInSettings(Flyout flyout)
    {
        _aiManager ??= new AiManager();
        _aiManager.Load();

        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
        var settingsPath = Path.Combine(settingsDir, "voice_settings.json");
        string? currentModelId = null;
        try
        {
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                currentModelId = doc.RootElement.GetProperty("selectedModelId").GetString();
            }
        }
        catch { }

        void RebuildModelsPanel()
        {
            ActionPanel.ShowVoiceModelsPanel(flyout, currentModelId, Content.XamlRoot,
                onSelectModel: model =>
                {
                    currentModelId = model.Id;
                    try
                    {
                        Directory.CreateDirectory(settingsDir);
                        var json = System.Text.Json.JsonSerializer.Serialize(new { selectedModelId = model.Id });
                        File.WriteAllText(settingsPath, json);
                    }
                    catch { }
                },
                onDeleteModel: model =>
                {
                    try
                    {
                        if (Directory.Exists(model.ModelDir))
                            Directory.Delete(model.ModelDir, true);
                    }
                    catch { }
                    if (currentModelId == model.Id)
                    {
                        currentModelId = null;
                        try { File.Delete(settingsPath); } catch { }
                    }
                    // Rebuild the models list in-place (flyout stays open)
                    RebuildModelsPanel();
                },
                onBack: () =>
                {
                    // Re-show main settings
                    Settings_Click(SettingsButton, new RoutedEventArgs());
                },
                onRebuild: RebuildModelsPanel,
                aiManager: _aiManager);
        }

        RebuildModelsPanel();
    }

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI", "debug.log");

    private static void LogDebug(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    private void ShowAiInSettings(Flyout flyout)
    {
        LogDebug("ShowAiInSettings called");
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            LogDebug("AiManager loaded OK");
        }
        catch (Exception ex)
        {
            LogDebug($"AiManager error: {ex}");
            flyout.Content = new TextBlock { Text = $"AI error: {ex.Message}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
            return;
        }

        ActionPanel.ShowAiPanel(flyout, _aiManager, Content.XamlRoot,
            onBack: () => Settings_Click(SettingsButton, new RoutedEventArgs()),
            onAiStateChanged: RefreshAiUiAcrossOpenWindows);
        LogDebug("ShowAiPanel done");
    }

    private void ShowPromptsInSettings(Flyout flyout)
    {
        LogDebug("ShowPromptsInSettings called");
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            LogDebug("AiManager loaded OK");
        }
        catch (Exception ex)
        {
            LogDebug($"AiManager error: {ex}");
            flyout.Content = new TextBlock { Text = $"AI error: {ex.Message}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
            return;
        }

        ActionPanel.ShowPromptsPanel(flyout, _aiManager,
            onBack: () => Settings_Click(SettingsButton, new RoutedEventArgs()));
        LogDebug("ShowPromptsPanel done");
    }

    private void RefreshAiUiAcrossOpenWindows()
    {
        foreach (var window in _openNoteWindows.ToList())
        {
            try { window.RefreshAiUi(); } catch { }
        }

        try { _notepadWindow?.RefreshAiUi(); } catch { }
    }

    private void RegisterGlobalHotkeys()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotkeyService?.Dispose();
        _hotkeyService = new HotkeyService(hwnd);

        var shortcuts = HotkeyService.Load();
        foreach (var s in shortcuts)
        {
            switch (s.Name)
            {
                case "show":
                    _hotkeyService.Register(HotkeyService.HOTKEY_SHOW,
                        s.Modifiers, s.VirtualKey, () =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (AppWindow.IsVisible)
                                    AppWindow.Hide();
                                else
                                    AppWindow.Show(true);
                            });
                        });
                    break;
                case "new_note":
                    _hotkeyService.Register(HotkeyService.HOTKEY_NEW_NOTE,
                        s.Modifiers, s.VirtualKey, () =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                AppWindow.Show(true);
                                AddButton_Click(this, new RoutedEventArgs());
                            });
                        });
                    break;
                case "paste_note":
                    _hotkeyService.Register(HotkeyService.HOTKEY_PASTE_NOTE,
                        s.Modifiers, s.VirtualKey, () =>
                        {
                            // Release all modifier keys from the hotkey combo first
                            const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10;
                            const byte VK_C = 0x43, VK_LWIN = 0x5B;
                            const int KEYEVENTF_KEYUP = 0x02;
                            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);

                            // Now send clean Ctrl+C while source app has focus
                            keybd_event(VK_CONTROL, 0, 0, 0);
                            keybd_event(VK_C, 0, 0, 0);
                            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);

                            // Create the note after clipboard updates
                            DispatcherQueue.TryEnqueue(async () =>
                            {
                                await Task.Delay(300);
                                PasteAsNewNote();
                            });
                        });
                    break;
            }
        }
    }

    private void ShowShortcutsInSettings(Flyout flyout)
    {
        var shortcuts = HotkeyService.Load();
        ActionPanel.ShowShortcutsPanel(flyout, shortcuts,
            onSave: entries =>
            {
                HotkeyService.Save(entries);
                RegisterGlobalHotkeys();
            },
            onBack: () =>
            {
                Settings_Click(SettingsButton, new RoutedEventArgs());
            });
    }

    private async Task PickNotesFolder()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            foreach (var w in _openNoteWindows.ToList())
            {
                try { w.Close(); } catch { }
            }
            _notes.ChangeFolder(folder.Path);
            SwitchView(ViewMode.Notes);
        }
    }

    private async Task ShowWebDavConfigDialog()
    {
        var urlBox = new TextBox
        {
            PlaceholderText = "https://cloud.exemple.com/remote.php/dav/files/user/Notes",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var userBox = new TextBox
        {
            PlaceholderText = Lang.T("username"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var passBox = new PasswordBox
        {
            PlaceholderText = Lang.T("password"),
            FontSize = 12
        };
        var errorText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var (savedUrl, savedUser, _) = AppSettings.LoadWebDavSettings();
        if (!string.IsNullOrEmpty(savedUrl)) urlBox.Text = savedUrl;
        if (!string.IsNullOrEmpty(savedUser)) userBox.Text = savedUser;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Lang.T("webdav_url"), FontSize = 12 });
        panel.Children.Add(urlBox);
        panel.Children.Add(new TextBlock { Text = Lang.T("username"), FontSize = 12 });
        panel.Children.Add(userBox);
        panel.Children.Add(new TextBlock { Text = Lang.T("password"), FontSize = 12 });
        panel.Children.Add(passBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "WebDAV / Nextcloud",
            Content = panel,
            PrimaryButtonText = Lang.T("connect"),
            CloseButtonText = Lang.T("cancel"),
            XamlRoot = this.Content.XamlRoot
        };

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) break;

            var url = urlBox.Text.Trim();
            var user = userBox.Text.Trim();
            var pass = passBox.Password;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user))
            {
                errorText.Text = Lang.T("url_user_required");
                errorText.Visibility = Visibility.Visible;
                continue;
            }

            var (success, error) = await _notes.ConnectWebDav(url, user, pass);
            if (success)
            {
                await _notes.SyncFromWebDav();
                RefreshCurrentView();
                break;
            }

            errorText.Text = error ?? Lang.T("connection_error");
            errorText.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowFirebaseConfigDialog()
    {
        var emailBox = new TextBox
        {
            PlaceholderText = "email@exemple.com",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var passwordBox = new PasswordBox
        {
            PlaceholderText = Lang.T("password"),
            FontSize = 12
        };
        var errorText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // Google sign-in button
        var googleBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 10, 0, 10),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var googleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        googleContent.Children.Add(new TextBlock { Text = "G", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 66, 133, 244)) });
        googleContent.Children.Add(new TextBlock { Text = Lang.T("continue_with_google"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        googleBtn.Content = googleContent;

        var separator = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        separator.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        separator.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        separator.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var line1 = new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };
        var line2 = new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };
        var orText = new TextBlock { Text = Lang.T("or"), FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], Margin = new Thickness(12, 0, 12, 0) };
        Grid.SetColumn(line1, 0); Grid.SetColumn(orText, 1); Grid.SetColumn(line2, 2);
        separator.Children.Add(line1); separator.Children.Add(orText); separator.Children.Add(line2);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(googleBtn);
        panel.Children.Add(separator);
        panel.Children.Add(new TextBlock { Text = Lang.T("email"), FontSize = 12 });
        panel.Children.Add(emailBox);
        panel.Children.Add(new TextBlock { Text = Lang.T("password"), FontSize = 12 });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Firebase",
            Content = panel,
            PrimaryButtonText = Lang.T("sign_in"),
            SecondaryButtonText = Lang.T("sign_up"),
            CloseButtonText = Lang.T("cancel"),
            XamlRoot = this.Content.XamlRoot
        };

        if (!RuntimeSecrets.TryGetFirebaseConfig(out var firebaseUrl, out var apiKey))
        {
            var missingConfigDialog = new ContentDialog
            {
                Title = Lang.T("firebase_config_missing"),
                Content = Lang.T("firebase_config_message"),
                CloseButtonText = Lang.T("ok"),
                XamlRoot = this.Content.XamlRoot
            };
            await missingConfigDialog.ShowAsync();
            return;
        }

        var googleSignInDone = false;
        googleBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            var (success, error) = await _notes.SignInFirebaseWithGoogle(firebaseUrl, apiKey);
            if (success)
            {
                await _notes.SyncFromFirebase();
                RefreshOpenNoteWindows();
                UpdateSyncButtonVisibility();
                RefreshCurrentView();
                StartFirebaseListener();
                googleSignInDone = true;
            }
            else
            {
                errorText.Text = error ?? Lang.T("error_google");
                errorText.Visibility = Visibility.Visible;
                // Re-show dialog
                await dialog.ShowAsync();
            }
        };

        while (!googleSignInDone)
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) break;

            var email = emailBox.Text.Trim();
            var password = passwordBox.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                errorText.Text = Lang.T("email_password_required");
                errorText.Visibility = Visibility.Visible;
                continue;
            }

            var (success, error) = result == ContentDialogResult.Primary
                ? await _notes.SignInFirebase(firebaseUrl, apiKey, email, password)
                : await _notes.SignUpFirebase(firebaseUrl, apiKey, email, password);

            if (success)
            {
                await _notes.SyncFromFirebase();
                RefreshOpenNoteWindows();
                UpdateSyncButtonVisibility();
                RefreshCurrentView();
                StartFirebaseListener();
                break;
            }

            errorText.Text = error ?? Lang.T("connection_error");
            errorText.Visibility = Visibility.Visible;
        }
    }

    private void OpenAcrylicSettings()
    {
        if (_acrylicSettingsWindow != null)
        {
            _acrylicSettingsWindow.Activate();
            return;
        }

        var currentSettings = AppSettings.LoadSettings();
        _acrylicSettingsWindow = new AcrylicSettingsWindow(currentSettings, settings =>
        {
            AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
            foreach (var w in _openNoteWindows)
                w.ApplyBackdrop(settings);
        });

        // Position next to main window (left if not enough space on right)
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        const int settingsWidth = 280;
        const int gap = 4;

        int settingsX;
        if (pos.X + size.Width + gap + settingsWidth <= workArea.X + workArea.Width)
            settingsX = pos.X + size.Width + gap;
        else
            settingsX = pos.X - settingsWidth - gap;

        _acrylicSettingsWindow.SetPosition(settingsX, pos.Y);

        _acrylicSettingsWindow.Closed += (_, _) => _acrylicSettingsWindow = null;
        _acrylicSettingsWindow.Activate();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinned;
        PinIcon.Glyph = _isPinned ? "\uE77A" : "\uE718";
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        SetCompactState(!_isCompact);
    }

    private void SetCompactState(bool compact, bool animate = true)
    {
        if (_isCompact == compact)
            return;

        if (!_isCompact)
        {
            _preCompactWidth = AppWindow.Size.Width;
            _preCompactHeight = AppWindow.Size.Height;
        }

        _isCompact = compact;
        _targetHeight = _isCompact ? CompactHeight : _preCompactHeight;
        CompactIcon.Glyph = _isCompact ? "\uE70D" : "\uE70E";

        if (_isCompact)
        {
            if (animate)
            {
                AnimationHelper.FadeOut(TitlePanel, 100);
                AnimationHelper.FadeOut(SearchGrid, 100);
                AnimationHelper.FadeOut(NotesScroll, 120);
                AnimationHelper.FadeOut(StatusBar, 100);
            }
            else
            {
                TitlePanel.Opacity = 0;
                SearchGrid.Opacity = 0;
                NotesScroll.Opacity = 0;
                StatusBar.Opacity = 0;
            }
        }
        else
        {
            if (animate)
            {
                AnimationHelper.FadeIn(TitlePanel, 200, 80);
                AnimationHelper.FadeIn(SearchGrid, 200, 120);
                AnimationHelper.FadeIn(NotesScroll, 250, 160);
                AnimationHelper.FadeIn(StatusBar, 200, 160);
            }
            else
            {
                TitlePanel.Opacity = 1;
                SearchGrid.Opacity = 1;
                NotesScroll.Opacity = 1;
                StatusBar.Opacity = 1;
            }
        }

        _animTimer?.Stop();
        _animTimer = null;
        if (!animate)
        {
            _currentAnimHeight = _targetHeight;
            var finalWidth = _isCompact ? AppWindow.Size.Width : _preCompactWidth;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(finalWidth, _targetHeight));
            return;
        }

        _currentAnimHeight = AppWindow.Size.Height;
        _animTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _animTimer.Tick += AnimTimer_Tick;
        _animTimer.Start();
    }

    private void AnimTimer_Tick(object? sender, object e)
    {
        var diff = _targetHeight - _currentAnimHeight;
        var step = diff / 4;
        if (step == 0 || Math.Abs(diff) < 3)
        {
            _currentAnimHeight = _targetHeight;
            var finalWidth = _isCompact ? AppWindow.Size.Width : _preCompactWidth;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(finalWidth, _currentAnimHeight));
            _animTimer?.Stop();
            _animTimer = null;
            return;
        }
        _currentAnimHeight += step;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, _currentAnimHeight));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Hide to tray (note windows stay open)
        AppWindow.Hide();
    }

    private void QuickAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView != ViewMode.Notes)
        {
            GoBack();
            return;
        }

        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE734", Lang.T("favorites"), [], () => SwitchView(ViewMode.Favorites)),
            new("\uE8B7", Lang.T("folders"), [], () => SwitchView(ViewMode.Folders)),
            new("\uE1CB", Lang.T("tags"), [], () => SwitchView(ViewMode.Tags)),
            new("\uE7B8", Lang.T("archive"), [], () => SwitchView(ViewMode.Archive)),
            new("\uE7E8", Lang.T("quit"), [], ExitApplication, IsDestructive: true),
        };

        var flyout = ActionPanel.Create(Lang.T("quick_access"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        flyout.ShowAt(QuickAccessButton);
    }

    private void SwitchView(ViewMode mode)
    {
        _currentView = mode;
        _currentTagFilter = null;
        _currentFolderFilter = null;
        RefreshCurrentView();
    }

    private void GoBack()
    {
        if (_currentView == ViewMode.TagFilter)
            SwitchView(ViewMode.Tags);
        else if (_currentView == ViewMode.FolderFilter)
            SwitchView(ViewMode.Folders);
        else
            SwitchView(ViewMode.Notes);
    }

    private void BackIcon_Tapped(object sender, TappedRoutedEventArgs e) => GoBack();

    private void TitleLabel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_currentView != ViewMode.Notes)
            GoBack();
    }

    private void RefreshCurrentView()
    {
        // Swap QuickAccess icon: back arrow when not on Notes, hamburger otherwise
        var qaIcon = (FontIcon)QuickAccessButton.Content;
        if (_currentView == ViewMode.Notes)
        {
            qaIcon.Glyph = "\uE700";
            BackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            qaIcon.Glyph = "\uE72B";
            BackIcon.Visibility = Visibility.Collapsed;
        }

        switch (_currentView)
        {
            case ViewMode.Notes:
                TitleLabel.Text = Lang.T("notes");
                RefreshNotesList(SearchBox.Text);
                break;
            case ViewMode.Favorites:
                TitleLabel.Text = Lang.T("favorites");
                ShowFavorites(SearchBox.Text);
                break;
            case ViewMode.Tags:
                TitleLabel.Text = Lang.T("tags");
                ShowTags(SearchBox.Text);
                break;
            case ViewMode.TagFilter:
                TitleLabel.Text = $"#{_currentTagFilter}";
                ShowNotesForTag(_currentTagFilter!, SearchBox.Text);
                break;
            case ViewMode.Folders:
                TitleLabel.Text = Lang.T("folders");
                ShowFolders(SearchBox.Text);
                break;
            case ViewMode.FolderFilter:
                TitleLabel.Text = _currentFolderFilter!;
                ShowNotesForFolder(_currentFolderFilter!, SearchBox.Text);
                break;
            case ViewMode.Archive:
                TitleLabel.Text = Lang.T("archive");
                ShowArchived(SearchBox.Text);
                break;
        }
    }

    private void ShowFavorites(string? search = null)
    {
        NotesList.Children.Clear();
        var favorites = _notes.GetSorted(AppSettings.LoadSortPreference()).Where(n => n.IsFavorite && !n.IsArchived).ToList();

        if (!string.IsNullOrEmpty(search))
        {
            favorites = favorites.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (favorites.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE734", Lang.T("no_favorites")));
            return;
        }

        var index = 0;
        foreach (var note in favorites)
        {
            var card = CreateNoteCard(note);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void ShowTags(string? search = null)
    {
        NotesList.Children.Clear();
        var tags = _notes.GetAllTags();

        if (!string.IsNullOrEmpty(search))
        {
            tags = tags.Where(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (tags.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE1CB", Lang.T("no_tags")));
            return;
        }

        var index = 0;
        foreach (var tag in tags)
        {
            var noteCount = _notes.Notes.Count(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            var card = CreateTagCard(tag, noteCount);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void ShowArchived(string? search = null)
    {
        NotesList.Children.Clear();
        var archived = _notes.GetSorted(AppSettings.LoadSortPreference()).Where(n => n.IsArchived).ToList();

        if (!string.IsNullOrEmpty(search))
        {
            archived = archived.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (archived.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE7B8", Lang.T("no_archived")));
            return;
        }

        var index = 0;
        foreach (var note in archived)
        {
            var card = CreateNoteCard(note);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void ShowNotesForTag(string tag, string? search = null)
    {
        NotesList.Children.Clear();
        var notes = _notes.GetSorted(AppSettings.LoadSortPreference())
            .Where(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) && !n.IsArchived)
            .ToList();

        if (!string.IsNullOrEmpty(search))
        {
            notes = notes.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (notes.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE1CB", Lang.T("no_notes_tag", tag)));
            return;
        }

        var index = 0;
        foreach (var note in notes)
        {
            var card = CreateNoteCard(note);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void ShowFolders(string? search = null)
    {
        NotesList.Children.Clear();
        var folders = _notes.GetAllFolders();

        if (!string.IsNullOrEmpty(search))
            folders = folders.Where(f => f.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        if (folders.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE8B7", Lang.T("no_folders")));
            return;
        }

        var index = 0;
        foreach (var folder in folders)
        {
            var noteCount = _notes.Notes.Count(n =>
                string.Equals(n.Folder, folder, StringComparison.OrdinalIgnoreCase) && !n.IsArchived);
            var card = CreateFolderCard(folder, noteCount);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private void ShowNotesForFolder(string folder, string? search = null)
    {
        NotesList.Children.Clear();
        var notes = _notes.GetSorted(AppSettings.LoadSortPreference())
            .Where(n => string.Equals(n.Folder, folder, StringComparison.OrdinalIgnoreCase) && !n.IsArchived)
            .ToList();

        if (!string.IsNullOrEmpty(search))
        {
            notes = notes.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (notes.Count == 0)
        {
            NotesList.Children.Add(CreateEmptyState("\uE8B7", Lang.T("no_notes_folder", folder)));
            return;
        }

        var index = 0;
        foreach (var note in notes)
        {
            var card = CreateNoteCard(note);
            NotesList.Children.Add(card);
            AnimationHelper.FadeSlideIn(card, delayMs: index * 30, durationMs: 250);
            index++;
        }
    }

    private UIElement CreateFolderCard(string folder, int noteCount)
    {
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            CornerRadius = new CornerRadius(8),
        };

        // Header: icon + name + count
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 14,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);

        var label = new TextBlock
        {
            Text = folder,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        var count = new TextBlock
        {
            Text = noteCount.ToString(),
            FontSize = 12,
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 2);

        headerGrid.Children.Add(icon);
        headerGrid.Children.Add(label);
        headerGrid.Children.Add(count);
        expander.Header = headerGrid;

        // Content: note cards (lazy-loaded on expand)
        var folderName = folder;
        var contentPanel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 4, 0, 4) };
        var loaded = false;

        expander.Expanding += (_, _) =>
        {
            if (loaded) return;
            loaded = true;

            var notes = _notes.GetSorted(AppSettings.LoadSortPreference())
                .Where(n => string.Equals(n.Folder, folderName, StringComparison.OrdinalIgnoreCase) && !n.IsArchived)
                .ToList();

            foreach (var note in notes)
            {
                var card = CreateNoteCard(note);
                contentPanel.Children.Add(card);
            }

            if (notes.Count == 0)
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = Lang.T("no_notes_folder", folderName),
                    FontSize = 12,
                    Opacity = 0.45,
                    Margin = new Thickness(4, 8, 4, 8)
                });
            }
        };

        expander.Content = contentPanel;
        return expander;
    }

    private UIElement CreateTagCard(string tag, int noteCount)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(ThemeHelper.CardBackground),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE1CB",
            FontSize = 14,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(icon, 0);

        var label = new TextBlock
        {
            Text = $"#{tag}",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        var count = new TextBlock
        {
            Text = noteCount.ToString(),
            FontSize = 12,
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 2);

        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(count);
        border.Child = grid;

        var tagName = tag;
        border.Tapped += (_, _) =>
        {
            _currentView = ViewMode.TagFilter;
            _currentTagFilter = tagName;
            RefreshCurrentView();
        };

        return border;
    }

    private static UIElement CreateEmptyState(string glyph, string message)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0),
            Spacing = 8
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 28,
            Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return panel;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCurrentView();
    }

    // ── Drag ───────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private void DragBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        GetCursorPos(out _dragStartCursor);
        _dragStartPos = AppWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void DragBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        GetCursorPos(out var current);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _dragStartPos.X + (current.X - _dragStartCursor.X),
            _dragStartPos.Y + (current.Y - _dragStartCursor.Y)));
    }

    private void DragBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    // ── Animated buttons ──────────────────────────────────────

    private void AnimatedButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
            var compositor = visual.Compositor;
            var anim = compositor.CreateSpringVector3Animation();
            anim.FinalValue = new System.Numerics.Vector3(1.15f);
            anim.DampingRatio = 0.6f;
            anim.Period = TimeSpan.FromMilliseconds(50);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(((FrameworkElement)el).ActualWidth / 2),
                (float)(((FrameworkElement)el).ActualHeight / 2), 0);
            visual.StartAnimation("Scale", anim);
        }
    }

    private void AnimatedButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
            var compositor = visual.Compositor;
            var anim = compositor.CreateSpringVector3Animation();
            anim.FinalValue = new System.Numerics.Vector3(1.0f);
            anim.DampingRatio = 0.6f;
            anim.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", anim);
        }
    }

    private void SettingsButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimatedIcon.SetState(SettingsAnimatedIcon, "PointerOver");
    }

    private void SettingsButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimatedIcon.SetState(SettingsAnimatedIcon, "Normal");
    }
}
