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

namespace NoteUI;

public sealed partial class ClipboardWidget : Window
{
    private readonly ClipboardHistoryManager _clipboardHistory;
    private readonly SnippetManager _snippetManager;
    private readonly NotesManager? _notesManager;
    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    private bool _isResizing;
    private POINT _resizeStartCursor;
    private Windows.Graphics.SizeInt32 _resizeStartSize;

    private bool _isCompact;
    private readonly IntPtr _previousHwnd;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public ClipboardWidget(ClipboardHistoryManager clipboardHistory, SnippetManager snippetManager, NotesManager? notesManager = null)
    {
        _previousHwnd = GetForegroundWindow();

        this.InitializeComponent();
        _clipboardHistory = clipboardHistory;
        _snippetManager = snippetManager;
        _notesManager = notesManager;

        ExtendsContentIntoTitleBar = true;
        var transparent = new Windows.UI.Color { A = 0 };
        AppWindow.TitleBar.BackgroundColor = transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = transparent;

        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);

        WindowHelper.RemoveWindowBorder(this);

        // Restore size
        var savedSize = AppSettings.LoadWidgetSize();
        var w = savedSize?.W ?? 300;
        var h = savedSize?.H ?? 420;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        WindowHelper.CenterOnScreen(this);

        // Restore position
        var savedPos = AppSettings.LoadWidgetPosition();
        if (savedPos.HasValue)
            AppWindow.Move(new Windows.Graphics.PointInt32(savedPos.Value.X, savedPos.Value.Y));

        WindowShadow.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);

        // Restore opacity
        var opacity = AppSettings.LoadWidgetOpacity();
        OpacitySlider.Value = opacity * 100;
        RootGrid.Opacity = opacity;

        // Restore compact mode
        _isCompact = AppSettings.LoadWidgetCompact();
        CompactIcon.Glyph = _isCompact ? "\uE1D8" : "\uE14C";

        this.Closed += (_, _) => _acrylicController?.Dispose();

        Refresh();
    }

    public void Refresh()
    {
        ContentPanel.Children.Clear();
        BuildHistorySection();
        BuildSnippetSection();
        BuildFavoritesSection();
        BuildFooter();
    }

    // ── Sections ─────────────────────────────────────────────────

    private void BuildHistorySection()
    {
        AddSectionHeader(Lang.T("clipboard_history"));

        var entries = _clipboardHistory.GetSorted();
        if (entries.Count == 0)
        {
            AddEmptyLabel(Lang.T("no_clipboard_entries"));
            return;
        }

        var historyBtn = CreateMenuButton("\uE81C", $"{Lang.T("clipboard_history")} ({entries.Count})");
        var subPanel = new StackPanel { Spacing = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(16, 0, 0, 0) };

        foreach (var entry in entries)
        {
            var capturedEntry = entry;
            var preview = entry.ContentType == "image"
                ? $"({Lang.T("clipboard_image")})"
                : (entry.TextContent?.Length > 45 ? entry.TextContent[..45].Replace("\n", " ") + "..." : entry.TextContent?.Replace("\n", " ") ?? "");

            var itemBtn = CreateItemButton(preview, _isCompact);
            itemBtn.Click += (_, _) => PasteToActiveWindow(() => CopyEntryToClipboard(capturedEntry));
            subPanel.Children.Add(itemBtn);
        }

        historyBtn.Click += (_, _) =>
        {
            subPanel.Visibility = subPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        };

        ContentPanel.Children.Add(historyBtn);
        ContentPanel.Children.Add(subPanel);
    }

    private void BuildSnippetSection()
    {
        AddDivider();
        AddSectionHeader(Lang.T("snippet"));

        var categories = _snippetManager.GetAllCategories();
        if (categories.Count == 0)
        {
            AddEmptyLabel(Lang.T("no_snippets"));
            return;
        }

        foreach (var category in categories)
        {
            var cat = category;
            var snippetsInCat = _snippetManager.Snippets
                .Where(s => string.Equals(s.Category, cat, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var catBtn = CreateMenuButton("\uE8B7", $"{cat} ({snippetsInCat.Count})");
            var subPanel = new StackPanel { Spacing = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(16, 0, 0, 0) };

            foreach (var snippet in snippetsInCat)
            {
                var s = snippet;
                var label = _isCompact ? s.Keyword : $"{s.Keyword}: {(s.Content.Length > 40 ? s.Content[..40] + "..." : s.Content)}";
                var itemBtn = CreateItemButton(label, _isCompact);
                itemBtn.Click += (_, _) =>
                {
                    PasteToActiveWindow(() =>
                    {
                        var dp = new DataPackage();
                        dp.SetText(s.Content);
                        Clipboard.SetContent(dp);
                    });
                };
                subPanel.Children.Add(itemBtn);
            }

            catBtn.Click += (_, _) =>
            {
                subPanel.Visibility = subPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
            };

            ContentPanel.Children.Add(catBtn);
            ContentPanel.Children.Add(subPanel);
        }
    }

    private void BuildFavoritesSection()
    {
        if (_notesManager == null) return;

        var favorites = _notesManager.Notes
            .Where(n => n.IsFavorite && !n.IsArchived)
            .ToList();

        if (favorites.Count == 0) return;

        AddDivider();
        AddSectionHeader(Lang.T("favorites"));

        foreach (var note in favorites)
        {
            var capturedNote = note;
            var title = string.IsNullOrWhiteSpace(note.Title) ? note.Content : note.Title;
            if (title.Length > 40) title = title[..40] + "...";

            var itemBtn = CreateItemButton(title, _isCompact, "\uE734");
            itemBtn.Click += (_, _) =>
            {
                PasteToActiveWindow(() =>
                {
                    var dp = new DataPackage();
                    dp.SetText(capturedNote.Content);
                    Clipboard.SetContent(dp);
                });
            };
            ContentPanel.Children.Add(itemBtn);
        }
    }

    private void BuildFooter()
    {
        AddDivider();
        var clearBtn = CreateMenuButton("\uE74D", Lang.T("clipboard_clear_all"));
        clearBtn.Click += (_, _) =>
        {
            _clipboardHistory.Clear();
            Refresh();
        };
        ContentPanel.Children.Add(clearBtn);
    }

    // ── Paste ────────────────────────────────────────────────────

    private void PasteToActiveWindow(Action copyAction)
    {
        copyAction();
        this.Close();
        Task.Delay(100).ContinueWith(_ =>
        {
            SetForegroundWindow(_previousHwnd);
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

    // ── UI helpers ───────────────────────────────────────────────

    private void AddSectionHeader(string text)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = _isCompact ? 11 : 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(12, _isCompact ? 4 : 6, 12, _isCompact ? 2 : 4),
            Opacity = 0.6
        });
    }

    private void AddEmptyLabel(string text)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = _isCompact ? 11 : 12,
            Opacity = 0.4,
            Padding = new Thickness(28, 4, 12, 4)
        });
    }

    private void AddDivider()
    {
        ContentPanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(8, _isCompact ? 2 : 4, 8, _isCompact ? 2 : 4)
        });
    }

    private static Button CreateMenuButton(string? glyph, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph != null)
            sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220
        });

        return new Button
        {
            Content = sp,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(4),
        };
    }

    private static Button CreateItemButton(string label, bool compact, string? glyph = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph != null)
            sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = compact ? 10 : 12, Opacity = 0.6 });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = compact ? 11 : 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = compact ? 1 : 2,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
        });

        return new Button
        {
            Content = sp,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, compact ? 3 : 6, 12, compact ? 3 : 6),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
        };
    }

    // ── Footer controls ──────────────────────────────────────────

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var opacity = e.NewValue / 100.0;
        RootGrid.Opacity = opacity;
        AppSettings.SaveWidgetOpacity(opacity);
    }

    private void CompactToggle_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = !_isCompact;
        CompactIcon.Glyph = _isCompact ? "\uE1D8" : "\uE14C";
        AppSettings.SaveWidgetCompact(_isCompact);
        Refresh();
    }

    // ── Drag ─────────────────────────────────────────────────────

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
            _dragStartPos.X + current.X - _dragStartCursor.X,
            _dragStartPos.Y + current.Y - _dragStartCursor.Y));
    }

    private void DragBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        var pos = AppWindow.Position;
        AppSettings.SaveWidgetPosition(pos.X, pos.Y);
    }

    // ── Resize ───────────────────────────────────────────────────

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizing = true;
        GetCursorPos(out _resizeStartCursor);
        _resizeStartSize = AppWindow.Size;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing) return;
        GetCursorPos(out var current);
        var newW = Math.Max(220, _resizeStartSize.Width + current.X - _resizeStartCursor.X);
        var newH = Math.Max(200, _resizeStartSize.Height + current.Y - _resizeStartCursor.Y);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(newW, newH));
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isResizing = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        var sz = AppWindow.Size;
        AppSettings.SaveWidgetSize(sz.Width, sz.Height);
    }
}
