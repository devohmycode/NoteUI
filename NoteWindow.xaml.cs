using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

public sealed partial class NoteWindow : Window
{
    private readonly NotesManager _notesManager;
    private readonly NoteEntry _note;
    private bool _isPinnedOnTop;
    private bool _suppressTextChanged;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isCompact;
    private const int FullNoteHeight = 450;
    private const int CompactNoteHeight = 40;
    private int _targetHeight;
    private int _currentAnimHeight;
    private DispatcherTimer? _animTimer;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    public string NoteId => _note.Id;

    public event Action? NoteChanged;

    public record ParentRect(int X, int Y, int Width, int Height);

    public NoteWindow(NotesManager notesManager, NoteEntry note, ParentRect? parentPosition = null)
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;

        WindowHelper.RemoveWindowBorder(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 450));
        WindowShadow.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Apply saved settings
        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme);

        // Position next to parent window with slight vertical offset
        if (parentPosition != null)
        {
            var rng = new Random();
            var x = parentPosition.X + parentPosition.Width + 4;
            var y = parentPosition.Y + rng.Next(0, 60);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        else
        {
            WindowHelper.CenterOnScreen(this);
        }

        _notesManager = notesManager;
        _note = note;

        ApplyNoteColor(note.Color);
        TitleText.Text = note.Title;
        LoadNote();

        this.Closed += (_, _) =>
        {
            _animTimer?.Stop();
            _animTimer = null;
            _acrylicController?.Dispose();
        };
    }

    public void ApplyBackdrop(BackdropSettings settings)
    {
        AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
    }

    // ── Color ──────────────────────────────────────────────────

    private void ApplyNoteColor(string colorName)
    {
        var darker = NoteColors.GetDarker(colorName, 0.93);
        TitleBarGrid.Background = new SolidColorBrush(darker);
        ColorIndicator.Fill = new SolidColorBrush(NoteColors.Get(colorName));

        // Subtle fade on color change
        AnimationHelper.FadeIn(TitleBarGrid, 200);
    }

    // ── Note loading/saving ────────────────────────────────────

    private void LoadNote()
    {
        _suppressTextChanged = true;
        if (string.IsNullOrEmpty(_note.Content))
            NoteEditor.Document.SetText(TextSetOptions.None, "");
        else if (_note.Content.StartsWith("{\\rtf", StringComparison.Ordinal))
            NoteEditor.Document.SetText(TextSetOptions.FormatRtf, _note.Content);
        else
            NoteEditor.Document.SetText(TextSetOptions.None, _note.Content);
        _suppressTextChanged = false;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void SaveCurrentNote()
    {
        NoteEditor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        _note.Content = rtf;
        _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
        NoteChanged?.Invoke();
    }

    // ── Formatting helpers ─────────────────────────────────────

    private void ToggleBold()
    {
        var sel = NoteEditor.Document.Selection;
        if (sel == null) return;
        sel.CharacterFormat.Bold = sel.CharacterFormat.Bold == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void ToggleItalic()
    {
        var sel = NoteEditor.Document.Selection;
        if (sel == null) return;
        sel.CharacterFormat.Italic = sel.CharacterFormat.Italic == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void ToggleUnderline()
    {
        var sel = NoteEditor.Document.Selection;
        if (sel == null) return;
        sel.CharacterFormat.Underline = sel.CharacterFormat.Underline == UnderlineType.Single
            ? UnderlineType.None : UnderlineType.Single;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void ToggleStrikethrough()
    {
        var sel = NoteEditor.Document.Selection;
        if (sel == null) return;
        sel.CharacterFormat.Strikethrough = sel.CharacterFormat.Strikethrough == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void ToggleBullets()
    {
        var sel = NoteEditor.Document.Selection;
        if (sel == null) return;
        sel.ParagraphFormat.ListType = sel.ParagraphFormat.ListType == MarkerType.Bullet
            ? MarkerType.None : MarkerType.Bullet;
        NoteEditor.Focus(FocusState.Programmatic);
    }

    // ── Events ─────────────────────────────────────────────────

    private void NoteEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged) return;
        SaveCurrentNote();
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new(null, "Gras", ["Ctrl", "B"], ToggleBold,
                Icon: new TextBlock { Text = "B", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, "Italique", ["Ctrl", "I"], ToggleItalic,
                Icon: new TextBlock { Text = "I", FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, "Soulign\u00e9", ["Ctrl", "U"], ToggleUnderline,
                Icon: new TextBlock { Text = "S", TextDecorations = Windows.UI.Text.TextDecorations.Underline, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, "Barr\u00e9", [], ToggleStrikethrough,
                Icon: new TextBlock { Text = "ab", TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough, FontSize = 13,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new("\uE8FD", "Liste \u00e0 puces", [], ToggleBullets),
        };

        var flyout = ActionPanel.Create("Format", actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        flyout.ShowAt(FormatButton);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var flyout = ActionPanel.CreateColorPicker("Couleur", _note.Color, colorName =>
        {
            _note.Color = colorName;
            ApplyNoteColor(colorName);
            SaveCurrentNote();
        });
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight;
        flyout.ShowAt(ColorButton);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE74D", "Supprimer la note", [], () =>
            {
                _notesManager.DeleteNote(_note.Id);
                this.Close();
            }, IsDestructive: true),
        };

        var flyout = ActionPanel.Create("Actions", actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedOnTop = !_isPinnedOnTop;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinnedOnTop;

        PinIcon.Glyph = _isPinnedOnTop ? "\uE77A" : "\uE718";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        this.Close();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = !_isCompact;
        CompactIcon.Glyph = _isCompact ? "\uE70D" : "\uE70E";

        if (_isCompact)
        {
            TitleText.Text = _note.Title;
            // Fade out content then collapse
            AnimationHelper.FadeOut(NoteEditor, 120, () =>
            {
                NoteEditor.Visibility = Visibility.Collapsed;
            });
            AnimationHelper.FadeOut(StatusBar, 120, () =>
            {
                StatusBar.Visibility = Visibility.Collapsed;
            });
        }
        else
        {
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            NoteEditor.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            AnimationHelper.FadeIn(NoteEditor, 200, 100);
            AnimationHelper.FadeIn(StatusBar, 200, 150);
        }

        _targetHeight = _isCompact ? CompactNoteHeight : FullNoteHeight;
        _currentAnimHeight = AppWindow.Size.Height;
        _animTimer?.Stop();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _animTimer.Tick += CompactAnimTick;
        _animTimer.Start();
    }

    private void CompactAnimTick(object? sender, object e)
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, _currentAnimHeight));
    }

    // ── Inline title editing ────────────────────────────────────

    private void TitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Text = _note.Title;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.Focus(FocusState.Programmatic);
        TitleEditBox.SelectAll();
    }

    private void CommitTitleEdit()
    {
        var newTitle = TitleEditBox.Text.Trim();
        if (!string.IsNullOrEmpty(newTitle))
        {
            _note.Title = newTitle;
            _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
            NoteChanged?.Invoke();
        }
        TitleText.Text = _note.Title;
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TitleEditBox.Visibility == Visibility.Visible)
            CommitTitleEdit();
    }

    private void TitleEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitTitleEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SaveCurrentNote();
            Close();
            e.Handled = true;
        }
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
