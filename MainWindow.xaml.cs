using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

public sealed partial class MainWindow : Window
{
    private readonly NotesManager _notes = new();
    private readonly List<NoteWindow> _openNoteWindows = [];

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private TrayIcon? _trayIcon;
    private bool _isExiting;
    private AcrylicSettingsWindow? _acrylicSettingsWindow;

    private bool _isPinned;
    private bool _isCompact;
    private const int FullHeight = 650;
    private const int CompactHeight = 120;
    private Microsoft.UI.Xaml.DispatcherTimer? _animTimer;
    private int _targetHeight;
    private int _currentAnimHeight;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    public MainWindow()
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;

        WindowHelper.RemoveWindowBorder(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(380, 650));
        WindowHelper.CenterOnScreen(this);
        WindowShadow.Apply(this);

        // Set window icon (for taskbar / Alt+Tab)
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Apply saved settings
        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme);

        // System tray icon
        _trayIcon = new TrayIcon(this, "Notes");
        _trayIcon.ShowRequested += () =>
        {
            AppWindow.Show(true);
        };
        _trayIcon.ExitRequested += ExitApplication;

        // Hide to tray instead of closing
        AppWindow.Closing += (_, args) =>
        {
            if (!_isExiting)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        this.Closed += (_, _) =>
        {
            _acrylicController?.Dispose();
            _trayIcon?.Dispose();
            Environment.Exit(0);
        };

        _notes.Load();
        RefreshNotesList();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        foreach (var w in _openNoteWindows.ToList())
        {
            try { w.Close(); } catch { }
        }
        this.Close();
    }

    private void RefreshNotesList(string? search = null)
    {
        NotesList.Children.Clear();
        var notes = _notes.GetSorted();

        if (!string.IsNullOrEmpty(search))
        {
            notes = notes.Where(n =>
                n.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        foreach (var note in notes)
            NotesList.Children.Add(CreateNoteCard(note));
    }

    private UIElement CreateNoteCard(NoteEntry note)
    {
        var color = NoteColors.Get(note.Color);

        var border = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 10, 16, 16),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var dateText = new TextBlock
        {
            Text = note.DateDisplay,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 12,
            Foreground = new SolidColorBrush(new Windows.UI.Color { A = 160, R = 0, G = 0, B = 0 }),
        };
        Grid.SetRow(dateText, 0);

        var preview = note.Preview;
        if (string.IsNullOrEmpty(preview)) preview = note.Title;

        var contentText = new TextBlock
        {
            Text = preview,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(new Windows.UI.Color { A = 220, R = 0, G = 0, B = 0 }),
        };
        Grid.SetRow(contentText, 1);

        grid.Children.Add(dateText);
        grid.Children.Add(contentText);
        border.Child = grid;

        var noteId = note.Id;
        border.Tapped += (_, _) => OpenNote(noteId);
        border.RightTapped += (s, e) =>
        {
            e.Handled = true;
            ShowNoteContextMenu(noteId, (FrameworkElement)s);
        };

        return border;
    }

    private void ShowNoteContextMenu(string noteId, FrameworkElement target)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE70F", "Modifier", [], () => OpenNote(noteId)),
            new("\uE8C8", "Dupliquer", [], () =>
            {
                var copy = _notes.DuplicateNote(noteId);
                if (copy != null)
                {
                    RefreshNotesList(SearchBox.Text);
                    OpenNote(copy.Id);
                }
            }),
            new("\uE74D", "Supprimer", [], () =>
            {
                var existing = _openNoteWindows.FirstOrDefault(w => w.NoteId == noteId);
                if (existing != null)
                {
                    try { existing.Close(); } catch { }
                }
                _notes.DeleteNote(noteId);
                RefreshNotesList(SearchBox.Text);
            }, IsDestructive: true),
        };

        var flyout = ActionPanel.Create("Actions", actions);
        flyout.ShowAt(target);
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

        var pos = AppWindow.Position;
        var sz = AppWindow.Size;
        var parentRect = new NoteWindow.ParentRect(pos.X, pos.Y, sz.Width, sz.Height);
        var window = new NoteWindow(_notes, note, parentRect);
        _openNoteWindows.Add(window);
        window.NoteChanged += () => RefreshNotesList(SearchBox.Text);
        window.Closed += (_, _) =>
        {
            _openNoteWindows.Remove(window);
            RefreshNotesList(SearchBox.Text);
        };
        window.Activate();
    }

    // ── Events ─────────────────────────────────────────────────

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        var note = _notes.CreateNote();
        RefreshNotesList(SearchBox.Text);
        OpenNote(note.Id);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();

        var flyout = ActionPanel.CreateSettings(theme, settings.Type,
            onThemeSelected: t =>
            {
                AppSettings.SaveThemeSetting(t);
                AppSettings.ApplyThemeToWindow(this, t);
                foreach (var w in _openNoteWindows)
                    AppSettings.ApplyThemeToWindow(w, t);
            },
            onBackdropSelected: b =>
            {
                var current = AppSettings.LoadSettings();
                var newSettings = current with { Type = b };
                AppSettings.SaveBackdropSettings(newSettings);
                AppSettings.ApplyToWindow(this, newSettings, ref _acrylicController, ref _configSource);
                foreach (var w in _openNoteWindows)
                    w.ApplyBackdrop(newSettings);

                if (b == "acrylic_custom")
                    OpenAcrylicSettings();
            });

        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight;
        flyout.ShowAt(sender as FrameworkElement);
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

        // Position next to main window
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _acrylicSettingsWindow.SetPosition(pos.X + size.Width + 4, pos.Y);

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
        _isCompact = !_isCompact;
        _targetHeight = _isCompact ? CompactHeight : FullHeight;
        CompactIcon.Glyph = _isCompact ? "\uE70D" : "\uE70E";

        _currentAnimHeight = AppWindow.Size.Height;
        _animTimer?.Stop();
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
        if (Math.Abs(diff) < 3)
        {
            _currentAnimHeight = _targetHeight;
            _animTimer?.Stop();
            _animTimer = null;
        }
        else
        {
            _currentAnimHeight += diff / 4;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(380, _currentAnimHeight));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Hide to tray (note windows stay open)
        AppWindow.Hide();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshNotesList(SearchBox.Text);
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
}
