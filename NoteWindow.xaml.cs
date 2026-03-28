using System.Runtime.InteropServices;
using System.Text;
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
    private NoteEntry _note;
    private SnippetManager? _snippetManager;
    private bool _isPinnedOnTop;
    private bool _suppressTextChanged;

    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isCompact;
    private const int DefaultNoteHeight = 450;
    private const int CompactNoteHeight = 40;
    private int _preCompactWidth = 400;
    private int _preCompactHeight = DefaultNoteHeight;
    private int _targetHeight;
    private int _currentAnimHeight;
    private DispatcherTimer? _animTimer;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    private Flyout? _slashFlyout;
    private bool _slashUndoing;
    private Flyout? _noteLinkFlyout;

    private bool _autoResize;
    private const int AutoResizeMin = 150;
    private const int AutoResizeMax = 800;
    private const int NoteWindowWidth = 400;

    // Note style
    private string _noteStyle = "titlebar";

    // Voice dictation
    private ISpeechRecognizer? _voiceRecognizer;
    private bool _isVoiceRecording;
    private AiManager? _aiManager;

    // Image drag-out to Explorer
    private bool _imageDragPending;
    private Windows.Foundation.Point _imageDragStart;
    private RichEditBox? _imageDragEditor;
    private int _imageDragPosition = -1;
    private string? _tempDragImagePath;

    public string NoteId => _note.Id;
    public bool IsCompact => _isCompact;
    public int PreCompactWidth => _preCompactWidth;
    public int PreCompactHeight => _preCompactHeight;

    public event Action? NoteChanged;
    public event Action? OpenInNotepadRequested;
    public event Action? ArchiveRequested;
    public event Action? AttachmentChanged;

    public record ParentRect(int X, int Y, int Width, int Height);

    public void SetSnippetManager(SnippetManager snippetManager) => _snippetManager = snippetManager;

    public NoteWindow(NotesManager notesManager, NoteEntry note, ParentRect? parentPosition = null)
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var transparent = new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
        AppWindow.TitleBar.BackgroundColor = transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = transparent;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = true;

        WindowHelper.RemoveWindowBorderKeepResize(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 450));
        WindowShadow.Apply(this);
        WindowHelper.AddResizeGrips(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Apply saved settings
        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);

        // Position next to parent window and slight vertical offset
        if (parentPosition != null)
        {
            var rng = new Random();
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = display.WorkArea;
            const int noteWidth = 400;
            const int gap = 4;

            int x;
            if (parentPosition.X + parentPosition.Width + gap + noteWidth <= workArea.X + workArea.Width)
                x = parentPosition.X + parentPosition.Width + gap;
            else
                x = parentPosition.X - noteWidth - gap;

            var y = parentPosition.Y + rng.Next(0, 60);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        else
        {
            WindowHelper.CenterOnScreen(this);
        }

        _notesManager = notesManager;
        _note = note;

        _noteStyle = AppSettings.LoadNoteStyle();
        TitleText.Text = note.Title;
        LockIcon.Glyph = note.IsLocked ? "\uE785" : "\uE72E";
        ToolTipService.SetToolTip(LockButton, Lang.T("lock_note"));
        UpdateMenuIcon();
        ApplyNoteLocalization();
        RefreshAiUi();
        NoteEditor.ContextRequested += NoteEditor_ContextRequested;
        TaskNoteEditor.ContextRequested += TaskNoteEditor_ContextRequested;
        UpdateAttachIcon();
        TitleBarGrid.SizeChanged += TitleBarGrid_SizeChanged;

        if (note.NoteType == "tasklist")
        {
            NoteEditor.Visibility = Visibility.Collapsed;
            TaskListScroll.Visibility = Visibility.Visible;
            FormatButton.Visibility = Visibility.Collapsed;
            LoadTaskList();
            LoadTaskNoteContent();
        }
        else
        {
            // Inline LoadNote without resetting _suppressTextChanged
            _suppressTextChanged = true;
            if (string.IsNullOrEmpty(_note.Content))
                NoteEditor.Document.SetText(TextSetOptions.None, "");
            else if (_note.Content.StartsWith("{\\rtf", StringComparison.Ordinal))
                NoteEditor.Document.SetText(TextSetOptions.FormatRtf, _note.Content);
            else
                NoteEditor.Document.SetText(TextSetOptions.None, _note.Content);
        }

        // _suppressTextChanged stays true — ApplyNoteColor triggers TextChanged
        ApplyNoteColor(note.Color);

        // Repair links once editor is in the visual tree (Loaded fires after rendering)
        NoteEditor.Loaded += NoteEditor_RepairOnLoad;

        // Intercept Ctrl+Click on note links BEFORE RichEditBox opens the browser
        NoteEditor.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(NoteEditor_PointerPressedForLink), true);

        // Drag images OUT of editor to Explorer
        NoteEditor.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Editor_PointerPressedForImageDrag), true);
        NoteEditor.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(Editor_PointerMovedForImageDrag), true);
        NoteEditor.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(Editor_PointerReleasedForImageDrag), true);
        TaskNoteEditor.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Editor_PointerPressedForImageDrag), true);
        TaskNoteEditor.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(Editor_PointerMovedForImageDrag), true);
        TaskNoteEditor.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(Editor_PointerReleasedForImageDrag), true);

        this.Closed += (_, _) =>
        {
            if (_isVoiceRecording) StopVoiceRecording();
            _animTimer?.Stop();
            _animTimer = null;
            _acrylicController?.Dispose();
        };
    }

    private void ApplyNoteLocalization()
    {
        ToolTipService.SetToolTip(MenuButton, Lang.T("tip_menu"));
        ToolTipService.SetToolTip(CompactButton, Lang.T("tip_compact"));
        ToolTipService.SetToolTip(PinButton, Lang.T("tip_pin"));
        ToolTipService.SetToolTip(NoteCloseButton, Lang.T("tip_close"));
        ToolTipService.SetToolTip(FormatButton, Lang.T("tip_format"));
        ToolTipService.SetToolTip(VoiceButton, Lang.T("tip_voice"));
        ToolTipService.SetToolTip(AiButton, Lang.T("tip_ai"));
        ToolTipService.SetToolTip(ColorButton, Lang.T("tip_color"));
        TaskNoteEditor.PlaceholderText = Lang.T("notes_placeholder");
    }

    public void ApplyBackdrop(BackdropSettings settings)
    {
        AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
    }

    public void ApplyTheme(string theme)
    {
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);
    }

    public void SetCompactState(bool compact, bool animate = true)
    {
        if (_isCompact == compact)
            return;

        if (!_isCompact && compact)
        {
            // Save current size before compacting
            _preCompactWidth = AppWindow.Size.Width;
            _preCompactHeight = AppWindow.Size.Height;
        }

        _isCompact = compact;

        // CompactIcon may not be rendered yet (ActualWidth==0 at init),
        // so use a fixed center point matching the icon size.
        var iconVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CompactIcon);
        var cx = CompactIcon.ActualWidth > 0 ? (float)(CompactIcon.ActualWidth / 2) : 6f;
        var cy = CompactIcon.ActualHeight > 0 ? (float)(CompactIcon.ActualHeight / 2) : 6f;
        iconVisual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0);

        if (animate)
        {
            var compositor = iconVisual.Compositor;
            var rotAnim = compositor.CreateSpringScalarAnimation();
            rotAnim.FinalValue = _isCompact ? 180f : 0f;
            rotAnim.DampingRatio = 0.7f;
            rotAnim.Period = TimeSpan.FromMilliseconds(60);
            iconVisual.StartAnimation("RotationAngleInDegrees", rotAnim);
        }
        else
        {
            iconVisual.StopAnimation("RotationAngleInDegrees");
            iconVisual.RotationAngleInDegrees = _isCompact ? 180f : 0f;
        }

        if (_isCompact)
        {
            TitleText.Text = _note.Title;
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;

            if (animate)
                AnimationHelper.FadeOut(MenuButton, 100, () => MenuButton.Visibility = Visibility.Collapsed);
            else
                MenuButton.Visibility = Visibility.Collapsed;

            if (_note.NoteType == "tasklist")
            {
                if (animate)
                {
                    AnimationHelper.FadeOut(TaskListScroll, 120, () =>
                    {
                        TaskListScroll.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    TaskListScroll.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                if (animate)
                {
                    AnimationHelper.FadeOut(NoteEditor, 120, () =>
                    {
                        NoteEditor.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    NoteEditor.Visibility = Visibility.Collapsed;
                }
            }

            if (animate)
            {
                AnimationHelper.FadeOut(StatusBar, 120, () =>
                {
                    StatusBar.Visibility = Visibility.Collapsed;
                });
            }
            else
            {
                StatusBar.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            MenuButton.Visibility = Visibility.Visible;
            if (animate)
                AnimationHelper.FadeIn(MenuButton, 200, 60);
            else
                MenuButton.Opacity = 1;

            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            if (_note.NoteType == "tasklist")
            {
                TaskListScroll.Visibility = Visibility.Visible;
                if (animate)
                    AnimationHelper.FadeIn(TaskListScroll, 200, 100);
                else
                    TaskListScroll.Opacity = 1;
            }
            else
            {
                NoteEditor.Visibility = Visibility.Visible;
                if (animate)
                    AnimationHelper.FadeIn(NoteEditor, 200, 100);
                else
                    NoteEditor.Opacity = 1;
            }

            StatusBar.Visibility = Visibility.Visible;
            if (animate)
                AnimationHelper.FadeIn(StatusBar, 200, 150);
            else
                StatusBar.Opacity = 1;
        }

        _targetHeight = _isCompact ? CompactNoteHeight : _preCompactHeight;
        _animTimer?.Stop();
        _animTimer = null;

        if (!animate)
        {
            _currentAnimHeight = _targetHeight;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                _isCompact ? AppWindow.Size.Width : _preCompactWidth, _targetHeight));
            return;
        }

        _currentAnimHeight = AppWindow.Size.Height;
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _animTimer.Tick += CompactAnimTick;
        _animTimer.Start();
    }

    // ── Color ──────────────────────────────────────────────────

    private void ApplyNoteColor(string colorName)
    {
        var isNone = NoteColors.IsNone(colorName);
        var isFull = _noteStyle == "full" && !isNone;
        var transparent = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });

        if (isNone)
        {
            TitleBarGrid.Background = transparent;
            RootGrid.Background = transparent;
            StatusBar.Background = transparent;
            StatusBar.BorderThickness = new Thickness(0, 1, 0, 0);
            StatusBar.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
            ColorIndicator.Fill = transparent;
            ColorIndicator.Stroke = new SolidColorBrush(new Windows.UI.Color { A = 80, R = 128, G = 128, B = 128 });
            ColorIndicator.StrokeThickness = 1.5;
        }
        else if (isFull)
        {
            var color = NoteColors.Get(colorName);
            var colorBrush = new SolidColorBrush(color);
            TitleBarGrid.Background = colorBrush;
            RootGrid.Background = colorBrush;
            StatusBar.Background = colorBrush;
            StatusBar.BorderThickness = new Thickness(0);
            ColorIndicator.Fill = new SolidColorBrush(NoteColors.GetDarker(colorName, 0.85));
            ColorIndicator.Stroke = null;
            ColorIndicator.StrokeThickness = 0;
        }
        else
        {
            var darker = NoteColors.GetDarker(colorName, 0.93);
            TitleBarGrid.Background = new SolidColorBrush(darker);
            RootGrid.Background = transparent;
            StatusBar.Background = transparent;
            StatusBar.BorderThickness = new Thickness(0, 1, 0, 0);
            StatusBar.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
            ColorIndicator.Fill = new SolidColorBrush(NoteColors.Get(colorName));
            ColorIndicator.Stroke = null;
            ColorIndicator.StrokeThickness = 0;
        }

        // Foreground: black on colored backgrounds, theme default otherwise
        var titleForeground = isNone
            ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Black);
        MenuIcon.Foreground = titleForeground;
        TitleText.Foreground = titleForeground;
        LockIcon.Foreground = titleForeground;
        CompactIcon.Foreground = titleForeground;
        PinIcon.Foreground = titleForeground;
        CloseIcon.Foreground = titleForeground;

        // Status bar icons: black in full mode with color, theme default otherwise
        var statusForeground = isFull
            ? new SolidColorBrush(Microsoft.UI.Colors.Black)
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        FormatIcon.Foreground = statusForeground;
        VoiceIcon.Foreground = statusForeground;
        AiIcon.Foreground = statusForeground;

        // Editor text color: force black in full mode (pastel backgrounds)
        if (isFull)
        {
            var blackBrush = new SolidColorBrush(Microsoft.UI.Colors.Black);
            NoteEditor.Foreground = blackBrush;
            TaskNoteEditor.Foreground = blackBrush;
            SetEditorTextColor(NoteEditor, Microsoft.UI.Colors.Black);
            SetEditorTextColor(TaskNoteEditor, Microsoft.UI.Colors.Black);
            // Override visual-state foreground so hover/focus don't revert to white
            NoteEditor.Resources["TextControlForeground"] = blackBrush;
            NoteEditor.Resources["TextControlForegroundPointerOver"] = blackBrush;
            NoteEditor.Resources["TextControlForegroundFocused"] = blackBrush;
            TaskNoteEditor.Resources["TextControlForeground"] = blackBrush;
            TaskNoteEditor.Resources["TextControlForegroundPointerOver"] = blackBrush;
            TaskNoteEditor.Resources["TextControlForegroundFocused"] = blackBrush;
        }
        else
        {
            NoteEditor.ClearValue(RichEditBox.ForegroundProperty);
            TaskNoteEditor.ClearValue(RichEditBox.ForegroundProperty);
            NoteEditor.Resources.Remove("TextControlForeground");
            NoteEditor.Resources.Remove("TextControlForegroundPointerOver");
            NoteEditor.Resources.Remove("TextControlForegroundFocused");
            TaskNoteEditor.Resources.Remove("TextControlForeground");
            TaskNoteEditor.Resources.Remove("TextControlForegroundPointerOver");
            TaskNoteEditor.Resources.Remove("TextControlForegroundFocused");
        }

        // Subtle fade on color change
        AnimationHelper.FadeIn(TitleBarGrid, 200);
    }

    public void ApplyNoteStyle(string style)
    {
        _noteStyle = style;
        ApplyNoteColor(_note.Color);
    }

    private static void SetEditorTextColor(RichEditBox editor, Windows.UI.Color color)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        if (string.IsNullOrEmpty(text.TrimEnd('\r', '\n')))
            return;
        var range = editor.Document.GetRange(0, int.MaxValue);
        var fmt = range.CharacterFormat;
        fmt.ForegroundColor = color;
        range.CharacterFormat = fmt;
    }

    private void UpdateMenuIcon()
    {
        MenuIcon.Glyph = _note.IsFavorite ? "\uE735" : "\uE712";
    }

    private void NoteEditor_RepairOnLoad(object sender, RoutedEventArgs e)
    {
        NoteEditor.Loaded -= NoteEditor_RepairOnLoad;
        _suppressTextChanged = true;
        NoteLinkHelper.RepairHiddenLinks(NoteEditor, _notesManager);
        _suppressTextChanged = false;
        NoteEditor.Focus(FocusState.Programmatic);
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

    public void ReloadFromDisk(bool focus = true)
    {
        _note = _notesManager.Notes.FirstOrDefault(n => n.Id == _note.Id) ?? _note;
        _suppressTextChanged = true;
        if (string.IsNullOrEmpty(_note.Content))
            NoteEditor.Document.SetText(TextSetOptions.None, "");
        else if (_note.Content.StartsWith("{\\rtf", StringComparison.Ordinal))
            NoteEditor.Document.SetText(TextSetOptions.FormatRtf, _note.Content);
        else
            NoteEditor.Document.SetText(TextSetOptions.None, _note.Content);
        if (focus) NoteEditor.Focus(FocusState.Programmatic);
        ApplyNoteColor(_note.Color);
        NoteLinkHelper.RepairHiddenLinks(NoteEditor, _notesManager);
        _suppressTextChanged = false;
    }

    private void SaveCurrentNote()
    {
        NoteEditor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        _note.Content = rtf;
        _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
        NoteChanged?.Invoke();
    }

    public void ReloadAfterSync(NoteEntry updatedNote)
    {
        var changed = _note.Content != updatedNote.Content
                   || _note.Title != updatedNote.Title
                   || _note.Color != updatedNote.Color;
        _note = updatedNote;
        if (!changed) return;
        TitleText.Text = _note.Title;
        _suppressTextChanged = true;
        if (_note.NoteType == "tasklist")
            LoadTaskNoteContent();
        else
            LoadNote();
        ApplyNoteColor(_note.Color);
        NoteLinkHelper.RepairHiddenLinks(NoteEditor, _notesManager);
        _suppressTextChanged = false;
    }

    // ── Task note (free-form text in tasklist mode) ─────────────

    private void LoadTaskNoteContent()
    {
        _suppressTextChanged = true;
        if (string.IsNullOrEmpty(_note.Content))
            TaskNoteEditor.Document.SetText(TextSetOptions.None, "");
        else if (_note.Content.StartsWith("{\\rtf", StringComparison.Ordinal))
            TaskNoteEditor.Document.SetText(TextSetOptions.FormatRtf, _note.Content);
        else
            TaskNoteEditor.Document.SetText(TextSetOptions.None, _note.Content);
        _suppressTextChanged = false;
    }

    private void SaveTaskNoteContent()
    {
        TaskNoteEditor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        _note.Content = rtf;
        _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
        NoteChanged?.Invoke();
    }

    private void AutoResizeWindow()
    {
        if (!_autoResize || _isCompact) return;

        int totalVisualLines;
        const int chromeHeight = 82; // TitleBar(40) + StatusBar(42)

        if (_note.NoteType == "tasklist")
        {
            // Count task rows + estimate task note text lines
            var taskLines = _note.Tasks.Count;
            var taskRowHeight = taskLines * 40; // ~40px per task row

            TaskNoteEditor.Document.GetText(TextGetOptions.None, out var noteText);
            noteText = noteText.TrimEnd('\r', '\n');
            var editorWidth = Math.Max(200, NoteWindowWidth - 48);
            const double avgCharWidth = 14 * 0.52;
            int noteVisualLines = 0;
            if (!string.IsNullOrEmpty(noteText))
            {
                foreach (var line in noteText.Split('\r'))
                {
                    var lineWidth = line.Length * avgCharWidth;
                    noteVisualLines += Math.Max(1, (int)Math.Ceiling(lineWidth / editorWidth));
                }
            }
            noteVisualLines = Math.Max(noteVisualLines, 2); // min space for placeholder

            var contentHeight = taskRowHeight + noteVisualLines * 21 + 24;
            var totalHeight = (int)(chromeHeight + contentHeight);
            totalHeight = Math.Clamp(totalHeight, AutoResizeMin, AutoResizeMax);

            if (Math.Abs(AppWindow.Size.Height - totalHeight) > 10)
                AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, totalHeight));
        }
        else
        {
            NoteEditor.Document.GetText(TextGetOptions.None, out var text);
            text = text.TrimEnd('\r', '\n');

            var editorWidth = Math.Max(200, NoteWindowWidth - 36);
            const double avgCharWidth = 14 * 0.52;
            totalVisualLines = 0;
            var textLines = string.IsNullOrEmpty(text) ? new[] { "" } : text.Split('\r');
            foreach (var line in textLines)
            {
                var lineWidth = line.Length * avgCharWidth;
                totalVisualLines += Math.Max(1, (int)Math.Ceiling(lineWidth / editorWidth));
            }
            totalVisualLines = Math.Max(totalVisualLines, 3); // min 3 lines

            var lineHeight = 14.0 * 1.5;
            var contentHeight = totalVisualLines * lineHeight + 24;
            var totalHeight = (int)(chromeHeight + contentHeight);
            totalHeight = Math.Clamp(totalHeight, AutoResizeMin, AutoResizeMax);

            if (Math.Abs(AppWindow.Size.Height - totalHeight) > 10)
                AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, totalHeight));
        }
    }

    private void TaskNoteEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged || _slashUndoing) return;
        SaveTaskNoteContent();
        AutoResizeWindow();

        if (_slashFlyout == null && AppSettings.LoadSlashEnabled())
        {
            var slashPos = SlashCommands.DetectSlash(TaskNoteEditor);
            if (slashPos >= 0)
            {
                // Check if "/" replaced a selection — undo to restore it
                _slashUndoing = true;
                TaskNoteEditor.Document.Undo();
                _slashUndoing = false;

                var sel = TaskNoteEditor.Document.Selection;
                bool hadSelection = sel.StartPosition != sel.EndPosition;

                if (hadSelection)
                {
                    slashPos = -1;
                }
                else
                {
                    _slashUndoing = true;
                    TaskNoteEditor.Document.Redo();
                    _slashUndoing = false;
                }

                var formatActions = SlashCommands.RichEditActions(TaskNoteEditor, slashPos);
                var aiActions = IsAiEnabled() ? CreateSlashAiActions(TaskNoteEditor, slashPos) : null;
                var sp = slashPos;

                var topActions = new List<ActionPanel.ActionItem>();

                void ReopenMain() =>
                    _slashFlyout = SlashCommands.Show(TaskNoteEditor, topActions, () => _slashFlyout = null);

                // Format submenu
                topActions.Add(new("\uE8D2", Lang.T("format"), [], () =>
                {
                    _slashFlyout = SlashCommands.ShowSubFlyout(TaskNoteEditor,
                        Lang.T("format"), formatActions,
                        onClosed: () => _slashFlyout = null,
                        onEscBack: ReopenMain);
                }));

                // IA submenu
                if (aiActions != null)
                {
                    topActions.Add(new("\uE99A", Lang.T("ai_section"), [], () =>
                    {
                        _slashFlyout = SlashCommands.ShowSubFlyout(TaskNoteEditor,
                            Lang.T("ai_section"), aiActions,
                            onClosed: () => _slashFlyout = null,
                            onEscBack: ReopenMain);
                    }));
                }

                topActions.Add(new("\uE720", Lang.T("audio"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartVoiceRecording();
                }));
                topActions.Add(new("\uE71B", Lang.T("link"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    DispatcherQueue.TryEnqueue(() => ShowTaskNoteLinkFlyout());
                }));
                topActions.Add(new("\uE722", Lang.T("capture"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartScreenCapture(TaskNoteEditor);
                }));
                topActions.Add(new("\uE8F4", Lang.T("extract_text"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartOcrCapture(TaskNoteEditor);
                }));
                topActions.Add(new("\uE70F", Lang.T("text_editor"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    SaveTaskNoteContent();
                    OpenInNotepadRequested?.Invoke();
                }));
                if (_snippetManager != null)
                {
                    topActions.Add(new("\uE943", Lang.T("snippet"), [], () =>
                    {
                        SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                        SaveTaskNoteContent();
                        ActionPanel.ShowSnippetFlyout(TaskNoteEditor, _note.Id, _snippetManager, _note.Content);
                    }));
                }
                _slashFlyout = SlashCommands.Show(TaskNoteEditor, topActions, () => _slashFlyout = null);
            }
        }
    }

    private async void ShowTaskNoteLinkFlyout()
    {
        var sel = TaskNoteEditor.Document.Selection;
        sel.GetText(Microsoft.UI.Text.TextGetOptions.None, out var selectedText);
        selectedText = selectedText.TrimEnd('\r', '\n');

        var displayBox = new TextBox
        {
            PlaceholderText = Lang.T("link_text_optional"),
            Header = Lang.T("display_text"),
            FontSize = 13,
            Text = selectedText ?? ""
        };

        var urlBox = new TextBox
        {
            PlaceholderText = Lang.T("link_to_webpage"),
            Header = Lang.T("address"),
            FontSize = 13
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(displayBox);
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            Title = Lang.T("link"),
            Content = panel,
            PrimaryButtonText = Lang.T("insert"),
            CloseButtonText = Lang.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var url = urlBox.Text.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                var display = displayBox.Text.Trim();
                var docSel = TaskNoteEditor.Document.Selection;

                if (!string.IsNullOrEmpty(display) && string.IsNullOrEmpty(selectedText))
                {
                    docSel.TypeText(display);
                    docSel.SetRange(docSel.EndPosition - display.Length, docSel.EndPosition);
                }

                docSel.Link = "\"" + url + "\"";
                docSel.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 96, 180, 255);
            }
        }
        TaskNoteEditor.Focus(FocusState.Programmatic);
    }

    // ── Task list ────────────────────────────────────────────────

    private bool _suppressTaskChanges;

    private void LoadTaskList()
    {
        _suppressTaskChanges = true;
        TaskListPanel.Children.Clear();
        foreach (var task in _note.Tasks)
            TaskListPanel.Children.Add(CreateTaskRow(task));
        TaskListPanel.Children.Add(CreateAddTaskButton());
        _suppressTaskChanges = false;
    }

    private UIElement CreateTaskRow(TaskItem task)
    {
        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var tertiaryBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var disabledBrush = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];

        var grid = new Grid
        {
            Padding = new Thickness(4, 2, 4, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Tag = task;

        var checkBox = new CheckBox
        {
            IsChecked = task.IsDone,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var textBox = new TextBox
        {
            Text = task.Text,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            FontSize = 13,
            Padding = new Thickness(6, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            FontStyle = task.IsDone ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Foreground = task.IsDone ? disabledBrush : primaryBrush
        };
        textBox.Resources["TextControlBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        textBox.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        textBox.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // Reminder button (bell icon, visible if reminder set or on hover)
        var bellIcon = new FontIcon
        {
            Glyph = task.ReminderAt != null ? "\uEA8F" : "\uE823",
            FontSize = 11,
            Foreground = task.ReminderAt != null ? primaryBrush : tertiaryBrush
        };
        var reminderBtn = new Button
        {
            Content = bellIcon,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 4, 5, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = task.ReminderAt != null ? 1 : 0,
        };
        if (task.ReminderAt != null)
            ToolTipService.SetToolTip(reminderBtn, $"{task.ReminderAt:dd/MM HH:mm}");

        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10, Foreground = tertiaryBrush },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 4, 5, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0
        };

        // Show buttons on hover
        grid.PointerEntered += (_, _) =>
        {
            reminderBtn.Opacity = 1;
            deleteBtn.Opacity = 1;
        };
        grid.PointerExited += (_, _) =>
        {
            reminderBtn.Opacity = task.ReminderAt != null ? 1 : 0;
            deleteBtn.Opacity = 0;
        };

        checkBox.Checked += (_, _) =>
        {
            task.IsDone = true;
            textBox.FontStyle = Windows.UI.Text.FontStyle.Italic;
            textBox.Foreground = disabledBrush;
            SaveTasks();
        };
        checkBox.Unchecked += (_, _) =>
        {
            task.IsDone = false;
            textBox.FontStyle = Windows.UI.Text.FontStyle.Normal;
            textBox.Foreground = primaryBrush;
            SaveTasks();
        };

        textBox.TextChanged += (_, _) =>
        {
            task.Text = textBox.Text;
            SaveTasks();

            if (!_suppressTaskChanges && _slashFlyout == null && AppSettings.LoadSlashEnabled())
            {
                var sp = SlashCommands.DetectSlash(textBox);
                if (sp >= 0)
                {
                    var cmds = SlashCommands.TextBoxActions(textBox, sp);
                    _slashFlyout = SlashCommands.Show(textBox,
                        new Windows.Foundation.Point(0, textBox.ActualHeight + 2),
                        cmds, () => _slashFlyout = null);
                }
            }
        };

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                AddNewTask(grid);
            }
            else if (e.Key == Windows.System.VirtualKey.Back && string.IsNullOrEmpty(textBox.Text))
            {
                e.Handled = true;
                RemoveTask(grid);
            }
        };

        reminderBtn.Click += (_, _) => ShowTaskReminderDialog(task, bellIcon, reminderBtn);
        deleteBtn.Click += (_, _) => RemoveTask(grid);

        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(textBox, 1);
        Grid.SetColumn(reminderBtn, 2);
        Grid.SetColumn(deleteBtn, 3);

        grid.Children.Add(checkBox);
        grid.Children.Add(textBox);
        grid.Children.Add(reminderBtn);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    private Button CreateAddTaskButton()
    {
        var tertiaryBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(28, 6, 8, 6),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 12, Foreground = tertiaryBrush });
    content.Children.Add(new TextBlock { Text = Lang.T("add_task"), FontSize = 13, Foreground = tertiaryBrush });
        btn.Content = content;
        btn.Click += (_, _) => AddNewTask(null);
        return btn;
    }

    private void AddNewTask(Grid? afterRow)
    {
        var newTask = new TaskItem();
        int insertIndex;

        if (afterRow != null)
        {
            var currentTask = afterRow.Tag as TaskItem;
            var taskIndex = _note.Tasks.IndexOf(currentTask!);
            insertIndex = taskIndex + 1;
        }
        else
        {
            insertIndex = _note.Tasks.Count;
        }

        _note.Tasks.Insert(insertIndex, newTask);
        var row = CreateTaskRow(newTask);

        // Insert before the add button (last child)
        var panelIndex = afterRow != null
            ? TaskListPanel.Children.IndexOf(afterRow) + 1
            : TaskListPanel.Children.Count - 1;
        TaskListPanel.Children.Insert(panelIndex, row);

        SaveTasks();

        // Focus the new textbox
        if (row is Grid g && g.Children.Count > 1 && g.Children[1] is TextBox tb)
        {
            tb.Focus(FocusState.Programmatic);
        }
    }

    private void RemoveTask(Grid row)
    {
        var task = row.Tag as TaskItem;
        if (task == null || !TaskListPanel.Children.Contains(row)) return;

        var index = TaskListPanel.Children.IndexOf(row);

        _note.Tasks.Remove(task);
        TaskListPanel.Children.Remove(row);

        SaveTasks();

        // If all tasks removed, convert back to regular note
        if (_note.Tasks.Count == 0)
        {
            // Preserve any task note content
            TaskNoteEditor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
            TaskNoteEditor.Document.GetText(TextGetOptions.None, out var plain);
            var hasContent = !string.IsNullOrWhiteSpace(plain?.TrimEnd('\r', '\n'));

            _note.NoteType = "note";
            _note.Content = hasContent ? rtf : "";
            _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);

            TaskListScroll.Visibility = Visibility.Collapsed;
            NoteEditor.Visibility = Visibility.Visible;
            FormatButton.Visibility = Visibility.Visible;

            _suppressTextChanged = true;
            if (hasContent)
                NoteEditor.Document.SetText(TextSetOptions.FormatRtf, rtf);
            else
                NoteEditor.Document.SetText(TextSetOptions.None, "");
            _suppressTextChanged = false;

            NoteEditor.Focus(FocusState.Programmatic);
            NoteChanged?.Invoke();
            return;
        }

        // Focus previous task
        var focusIndex = Math.Max(0, index - 1);
        if (focusIndex < TaskListPanel.Children.Count && TaskListPanel.Children[focusIndex] is Grid g
            && g.Children.Count > 1 && g.Children[1] is TextBox tb)
        {
            tb.Focus(FocusState.Programmatic);
        }
    }

    private void SaveTasks()
    {
        if (_suppressTaskChanges) return;
        _notesManager.UpdateTasks(_note.Id, _note.Tasks);
        NoteChanged?.Invoke();
        AutoResizeWindow();
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

    private void ConvertToTaskList(int slashPos)
    {
        // Remove the "/" and get all text
        SlashCommands.DeleteSlash(NoteEditor, slashPos);
        NoteEditor.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var text);

        // Split lines into tasks (non-empty lines become tasks)
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        _note.Tasks.Clear();
        if (lines.Count > 0)
        {
            foreach (var line in lines)
                _note.Tasks.Add(new TaskItem { Text = line });
        }
        else
        {
            _note.Tasks.Add(new TaskItem());
        }

        // Switch to tasklist mode
        _note.NoteType = "tasklist";
        _note.Content = "";
        _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
        _notesManager.UpdateTasks(_note.Id, _note.Tasks);

        // Switch UI
        NoteEditor.Visibility = Visibility.Collapsed;
        TaskListScroll.Visibility = Visibility.Visible;
        FormatButton.Visibility = Visibility.Collapsed;
        LoadTaskList();
        NoteChanged?.Invoke();
    }

    private void NoteEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged || _slashUndoing) return;
        SaveCurrentNote();
        AutoResizeWindow();

        if (_slashFlyout == null && AppSettings.LoadSlashEnabled())
        {
            var slashPos = SlashCommands.DetectSlash(NoteEditor);
            if (slashPos >= 0)
            {
                // Check if "/" replaced a selection — undo to restore it
                _slashUndoing = true;
                NoteEditor.Document.Undo();
                _slashUndoing = false;

                var sel = NoteEditor.Document.Selection;
                bool hadSelection = sel.StartPosition != sel.EndPosition;

                if (hadSelection)
                {
                    slashPos = -1;
                }
                else
                {
                    _slashUndoing = true;
                    NoteEditor.Document.Redo();
                    _slashUndoing = false;
                }

                var formatActions = SlashCommands.RichEditActions(NoteEditor, slashPos);
                var aiActions = IsAiEnabled() ? CreateSlashAiActions(NoteEditor, slashPos) : null;
                var sp = slashPos;

                var topActions = new List<ActionPanel.ActionItem>();

                void ReopenMain() =>
                    _slashFlyout = SlashCommands.Show(NoteEditor, topActions, () => _slashFlyout = null);

                // Format submenu
                topActions.Add(new("\uE8D2", Lang.T("format"), [], () =>
                {
                    _slashFlyout = SlashCommands.ShowSubFlyout(NoteEditor,
                        Lang.T("format"), formatActions,
                        onClosed: () => _slashFlyout = null,
                        onEscBack: ReopenMain);
                }));

                // IA submenu
                if (aiActions != null)
                {
                    topActions.Add(new("\uE99A", Lang.T("ai_section"), [], () =>
                    {
                        _slashFlyout = SlashCommands.ShowSubFlyout(NoteEditor,
                            Lang.T("ai_section"), aiActions,
                            onClosed: () => _slashFlyout = null,
                            onEscBack: ReopenMain);
                    }));
                }

                topActions.Add(new("\uE73A", Lang.T("task"), [], () => ConvertToTaskList(sp)));
                topActions.Add(new("\uE720", Lang.T("audio"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartVoiceRecording();
                }));
                topActions.Add(new("\uE71B", Lang.T("link"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    DispatcherQueue.TryEnqueue(() => ShowLinkFlyout());
                }));
                topActions.Add(new("\uE722", Lang.T("capture"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartScreenCapture(NoteEditor);
                }));
                topActions.Add(new("\uE8F4", Lang.T("extract_text"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartOcrCapture(NoteEditor);
                }));
                topActions.Add(new("\uE70F", Lang.T("text_editor"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    SaveCurrentNote();
                    OpenInNotepadRequested?.Invoke();
                }));
                if (_snippetManager != null)
                {
                    topActions.Add(new("\uE943", Lang.T("snippet"), [], () =>
                    {
                        SlashCommands.DeleteSlash(NoteEditor, sp);
                        SaveCurrentNote();
                        ActionPanel.ShowSnippetFlyout(NoteEditor, _note.Id, _snippetManager, _note.Content);
                    }));
                }
                _slashFlyout = SlashCommands.Show(NoteEditor, topActions, () => _slashFlyout = null);
            }
        }

        // Detect [[ for note linking
        if (_noteLinkFlyout == null)
            DetectNoteLinkTrigger(NoteEditor);
    }

    private void NoteEditor_PointerPressedForLink(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichEditBox editor) return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        DispatcherQueue.TryEnqueue(() => HandleNoteLinkClick(editor));
    }

    private void NoteEditor_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        ShowAiContextMenu(NoteEditor, e);
    }

    private void TaskNoteEditor_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        ShowAiContextMenu(TaskNoteEditor, e);
    }

    private void ShowAiContextMenu(RichEditBox editor, ContextRequestedEventArgs e)
    {
        if (!IsAiEnabled())
            return;

        var selection = editor.Document.Selection;
        selection.GetText(TextGetOptions.None, out var selectedText);
        if (string.IsNullOrWhiteSpace(selectedText))
            return;

        e.Handled = true;
        ShowAiActionsFlyout(editor, editor);
    }

    private void AiMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAiEnabled())
            return;

        var editor = GetCurrentAiEditor();
        ShowAiActionsFlyout(AiButton, editor);
    }

    private RichEditBox GetCurrentAiEditor()
    {
        if (_note.NoteType == "tasklist")
            return TaskNoteEditor;

        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (focused == TaskNoteEditor)
            return TaskNoteEditor;

        return NoteEditor;
    }

    private void ShowAiActionsFlyout(FrameworkElement target, RichEditBox editor)
    {
        if (!IsAiEnabled())
            return;

        var actions = CreateAiActions(editor);
        var flyout = ActionPanel.Create(Lang.T("ai_section"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        flyout.Opened += (_, _) => AnimateAiFlyoutItems(flyout);
        flyout.ShowAt(target);
    }

    private string AiPrompt(string key) => _aiManager?.GetPrompt(key) ?? AiManager.GetDefaultPrompt(key);

    private List<ActionPanel.ActionItem> CreateAiActions(RichEditBox editor)
    {
        var list = new List<ActionPanel.ActionItem>
        {
            new("\uE8D2", Lang.T("ai_improve_writing"), [],
                () => _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_improve_writing"))),
            new("\uE8FD", Lang.T("ai_fix_grammar_spelling"), [],
                () => _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_fix_grammar_spelling"))),
            new("\uE8D3", Lang.T("ai_tone_professional"), [],
                () => _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_professional"))),
            new("\uE8D4", Lang.T("ai_tone_friendly"), [],
                () => _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_friendly"))),
            new("\uE8D5", Lang.T("ai_tone_concise"), [],
                () => _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_concise"))),
        };
        if (_aiManager != null)
            foreach (var cp in _aiManager.Settings.CustomPrompts)
            {
                var instruction = cp.Instruction;
                list.Add(new("\uE945", cp.Title, [],
                    () => _ = ApplyAiToSelectionAsync(editor, instruction)));
            }
        return list;
    }

    private List<ActionPanel.ActionItem> CreateSlashAiActions(RichEditBox editor, int slashPos)
    {
        var list = new List<ActionPanel.ActionItem>
        {
            new("\uE8D2", Lang.T("ai_improve_writing"), [],
                () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_improve_writing")); }),
            new("\uE8FD", Lang.T("ai_fix_grammar_spelling"), [],
                () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_fix_grammar_spelling")); }),
            new("\uE8D3", Lang.T("ai_tone_professional"), [],
                () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_professional")); }),
            new("\uE8D4", Lang.T("ai_tone_friendly"), [],
                () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_friendly")); }),
            new("\uE8D5", Lang.T("ai_tone_concise"), [],
                () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, AiPrompt("ai_tone_concise")); }),
        };
        if (_aiManager != null)
            foreach (var cp in _aiManager.Settings.CustomPrompts)
            {
                var instruction = cp.Instruction;
                list.Add(new("\uE945", cp.Title, [],
                    () => { SlashCommands.DeleteSlash(editor, slashPos); _ = ApplyAiToSelectionAsync(editor, instruction); }));
            }
        return list;
    }

    private static void AnimateAiFlyoutItems(Flyout flyout)
    {
        if (flyout.Content is not Panel panel) return;
        var delay = 0;
        foreach (var child in panel.Children)
        {
            if (child is Button btn)
            {
                AnimationHelper.FadeSlideIn(btn, delay, 180);
                delay += 24;
            }
        }
    }

    private async Task ApplyAiToSelectionAsync(RichEditBox editor, string instruction)
    {
        var selection = editor.Document.Selection;
        var start = selection.StartPosition;
        var end = selection.EndPosition;
        if (start >= end)
        {
            await ShowAiMessageAsync(Lang.T("ai_select_text_first"));
            return;
        }

        selection.GetText(TextGetOptions.None, out var selectedText);
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await ShowAiMessageAsync(Lang.T("ai_select_text_first"));
            return;
        }

        var rewritten = await TransformSelectionWithAiAsync(selectedText, instruction);
        if (string.IsNullOrWhiteSpace(rewritten))
            return;

        var range = editor.Document.GetRange(start, end);
        range.SetText(TextSetOptions.None, rewritten);
        editor.Document.Selection.SetRange(start, start + rewritten.Length);
        editor.Focus(FocusState.Programmatic);

        if (editor == NoteEditor)
            SaveCurrentNote();
        else
            SaveTaskNoteContent();
    }

    private async Task<string?> TransformSelectionWithAiAsync(string selectedText, string instruction)
    {
        try { _aiManager ??= new AiManager(); _aiManager.Load(); }
        catch { await ShowAiMessageAsync("AI unavailable"); return null; }
        if (!_aiManager.IsEnabled)
        {
            await ShowAiMessageAsync(Lang.T("ai_no_model_selected"));
            return null;
        }

        var prompt = BuildAiPrompt(selectedText, instruction);
        var history = new List<AiManager.ChatMessage>();
        var output = new StringBuilder();

        try
        {
            if (TryGetActiveCloud(out var provider, out var apiKey, out var modelId))
            {
                await foreach (var token in provider.StreamChatAsync(
                    apiKey,
                    modelId,
                    _aiManager.Settings.SystemPrompt,
                    history,
                    prompt,
                    _aiManager.Settings.Temperature,
                    _aiManager.Settings.MaxTokens))
                {
                    output.Append(token);
                }
            }
            else if (TryGetActiveLocal(out var localModelFile))
            {
                await _aiManager.LoadModelAsync(localModelFile);
                await foreach (var token in _aiManager.ChatLocalAsync(prompt, history))
                    output.Append(token);
            }
            else
            {
                await ShowAiMessageAsync(Lang.T("ai_no_model_selected"));
                return null;
            }
        }
        catch (Exception ex)
        {
            await ShowAiMessageAsync(Lang.T("ai_transform_failed", ex.Message));
            return null;
        }

        var text = NormalizeAiOutput(output.ToString());
        if (string.IsNullOrWhiteSpace(text))
        {
            await ShowAiMessageAsync(Lang.T("ai_transform_empty"));
            return null;
        }
        return text;
    }

    private bool TryGetActiveCloud(out ICloudAiProvider provider, out string apiKey, out string modelId)
    {
        provider = default!;
        apiKey = "";
        modelId = "";

        var providerId = _aiManager?.Settings.LastProviderId ?? "";
        modelId = _aiManager?.Settings.LastModelId ?? "";
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return false;

        var p = _aiManager!.GetProvider(providerId);
        if (p == null)
            return false;

        apiKey = _aiManager.GetApiKey(providerId);
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        provider = p;
        return true;
    }

    private bool TryGetActiveLocal(out string localModelFile)
    {
        localModelFile = _aiManager?.Settings.LastLocalModelFileName ?? "";
        if (string.IsNullOrWhiteSpace(localModelFile))
            return false;

        var modelPath = Path.Combine(AiManager.ModelsDir, localModelFile);
        return File.Exists(modelPath);
    }

    private static string BuildAiPrompt(string selectedText, string instruction) =>
        $"""
        You are rewriting user text.
        Task: {instruction}

        Rules:
        - IMPORTANT: Reply in the SAME language as the original text. If the text is in French, reply in French. If in English, reply in English.
        - Preserve the original meaning and facts.
        - Return ONLY the rewritten text. No title, no explanation, no bullet list, no prefix like "assistant" or "Here is".

        Text:
        <<<
        {selectedText}
        >>>
        """;

    private static string NormalizeAiOutput(string raw)
    {
        var text = raw.Trim();

        // Strip "assistant" role prefix emitted by some local models (Llama, etc.)
        if (text.StartsWith("assistant", StringComparison.OrdinalIgnoreCase))
        {
            text = text["assistant".Length..].TrimStart(':', ' ', '\n', '\r');
        }

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = text.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                text = text[(firstLineBreak + 1)..];
                var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                    text = text[..closingFence];
            }
        }

        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        return text.Trim();
    }

    public void RefreshAiUi()
    {
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            if (!_aiManager.IsEnabled)
                _aiManager.UnloadModel();
            AiButton.Visibility = _aiManager.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            AiButton.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsAiEnabled()
    {
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            return _aiManager.IsEnabled;
        }
        catch { return false; }
    }

    private async Task ShowAiMessageAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = Lang.T("ai_section"),
            Content = message,
            CloseButtonText = Lang.T("ok"),
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new(null, Lang.T("bold"), ["Ctrl", "B"], ToggleBold,
                Icon: new TextBlock { Text = "B", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, Lang.T("italic"), ["Ctrl", "I"], ToggleItalic,
                Icon: new TextBlock { Text = "I", FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, Lang.T("underline"), ["Ctrl", "U"], ToggleUnderline,
                Icon: new TextBlock { Text = "S", TextDecorations = Windows.UI.Text.TextDecorations.Underline, FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new(null, Lang.T("strikethrough"), [], ToggleStrikethrough,
                Icon: new TextBlock { Text = "ab", TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough, FontSize = 13,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center }),
            new("\uE8FD", Lang.T("bullet_list"), [], ToggleBullets),
            new("\uE71B", Lang.T("link"), [], () => DispatcherQueue.TryEnqueue(() => ShowLinkFlyout())),
            new("\uE722", Lang.T("screenshot"), [], () => StartScreenCapture(NoteEditor)),
            new("\uE8F4", Lang.T("extract_text"), [], () => StartOcrCapture(NoteEditor)),
        };

        var flyout = ActionPanel.Create(Lang.T("format"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        flyout.ShowAt(FormatButton);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var flyout = ActionPanel.CreateColorPicker(Lang.T("color"), _note.Color, colorName =>
        {
            _note.Color = colorName;
            ApplyNoteColor(colorName);
            _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
            NoteChanged?.Invoke();
        });
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight;
        flyout.ShowAt(ColorButton);
    }

    // ── Screen capture ──────────────────────────────────────────

    // ── Note links [[note name]] ──────────────────────────────

    private void DetectNoteLinkTrigger(RichEditBox editor)
    {
        var sel = editor.Document.Selection;
        if (sel == null) return;

        // Get text before cursor (up to 100 chars back)
        int cursorPos = sel.StartPosition;
        int lookBack = Math.Min(cursorPos, 100);
        if (lookBack < 2) return;

        var range = editor.Document.GetRange(cursorPos - lookBack, cursorPos);
        range.GetText(TextGetOptions.None, out var textBefore);
        if (textBefore == null) return;

        // Find last [[ that isn't closed
        int openBracket = textBefore.LastIndexOf("[[", StringComparison.Ordinal);
        if (openBracket < 0) return;

        // Check no ]] between [[ and cursor
        var afterBracket = textBefore[(openBracket + 2)..];
        if (afterBracket.Contains("]]", StringComparison.Ordinal)) return;
        if (afterBracket.Contains('\r') || afterBracket.Contains('\n')) return;

        var query = afterBracket;
        var bracketAbsPos = cursorPos - lookBack + openBracket;
        ShowNoteLinkFlyout(editor, query, bracketAbsPos);
    }

    private void ShowNoteLinkFlyout(RichEditBox editor, string query, int bracketPos)
    {
        _noteLinkFlyout?.Hide();

        var notes = string.IsNullOrEmpty(query)
            ? _notesManager.Notes.Where(n => n.Id != _note.Id && !n.IsArchived)
                .OrderByDescending(n => n.UpdatedAt).Take(10).ToList()
            : _notesManager.SearchByTitle(query)
                .Where(n => n.Id != _note.Id && !n.IsArchived).Take(10).ToList();

        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(220, 280);

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(ActionPanel.CreateHeader(Lang.T("link_note")));
        panel.Children.Add(ActionPanel.CreateSeparator());

        if (notes.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("no_notes_found"),
                FontSize = 12,
                Opacity = 0.5,
                Margin = new Thickness(10, 8, 10, 8)
            });
        }

        foreach (var note in notes)
        {
            var noteRef = note;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(4),
            };

            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock
            {
                Text = note.Title,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });

            var preview = note.Preview;
            if (!string.IsNullOrEmpty(preview))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = preview,
                    FontSize = 11,
                    Opacity = 0.4,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                });
            }

            btn.Content = sp;
            btn.Click += (_, _) =>
            {
                InsertNoteLink(editor, noteRef, bracketPos);
                flyout.Hide();
            };
            panel.Children.Add(btn);
        }

        flyout.Content = panel;
        flyout.ShouldConstrainToRootBounds = false;
        flyout.Closed += (_, _) => _noteLinkFlyout = null;

        _noteLinkFlyout = flyout;
        flyout.ShowAt(editor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
        });
    }

    private static readonly Windows.UI.Color NoteLinkColor = Windows.UI.Color.FromArgb(255, 140, 200, 120);

    private void InsertNoteLink(RichEditBox editor, NoteEntry target, int bracketPos)
    {
        _suppressTextChanged = true;

        // Delete from [[ to current cursor position
        var sel = editor.Document.Selection;
        int cursorPos = sel.StartPosition;
        var range = editor.Document.GetRange(bracketPos, cursorPos);
        range.Delete(TextRangeUnit.Character, 0);

        // Insert: "Title⟨hidden-id⟩" — title in green underline, ID as hidden text
        sel = editor.Document.Selection;
        sel.TypeText(target.Title);
        sel.SetRange(sel.EndPosition - target.Title.Length, sel.EndPosition);
        sel.CharacterFormat.ForegroundColor = NoteLinkColor;
        sel.CharacterFormat.Underline = UnderlineType.Single;

        // Append hidden note ID (zero-width, hidden formatting)
        sel.SetRange(sel.EndPosition, sel.EndPosition);
        sel.TypeText("\u200B" + target.Id + "\u200B");
        sel.SetRange(sel.EndPosition - target.Id.Length - 2, sel.EndPosition);
        sel.CharacterFormat.Hidden = FormatEffect.On;
        sel.CharacterFormat.ForegroundColor = NoteLinkColor;

        // Move cursor after and reset format
        sel.SetRange(sel.EndPosition, sel.EndPosition);
        sel.CharacterFormat.Hidden = FormatEffect.Off;
        sel.CharacterFormat.ForegroundColor = ThemeHelper.IsDark()
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 0, 0, 0);
        sel.CharacterFormat.Underline = UnderlineType.None;

        _suppressTextChanged = false;
        SaveCurrentNote();
        editor.Focus(FocusState.Programmatic);
    }

    public event Action<string>? NoteLinkClicked;

    private void HandleNoteLinkClick(RichEditBox editor)
    {
        var sel = editor.Document.Selection;
        if (sel == null) return;

        var pos = sel.StartPosition;

        // Check if character at cursor is green (note link)
        var fg = editor.Document.GetRange(pos, pos + 1).CharacterFormat.ForegroundColor;
        if (!IsNoteLinkColor(fg) && pos > 0)
            fg = editor.Document.GetRange(pos - 1, pos).CharacterFormat.ForegroundColor;
        if (!IsNoteLinkColor(fg)) return;

        // Walk forward from cursor through ALL green text (including hidden ID)
        // to find the hidden GUID after the visible title
        int end = pos;
        editor.Document.GetText(TextGetOptions.None, out var plainText);
        int textLen = plainText.TrimEnd('\r', '\n').Length;
        while (end < textLen)
        {
            var next = editor.Document.GetRange(end, end + 1);
            if (!IsNoteLinkColor(next.CharacterFormat.ForegroundColor)) break;
            end++;
        }

        // Get the full green range including hidden text
        var fullRange = editor.Document.GetRange(pos > 0 ? pos - 1 : 0, end);
        // Walk back to start of green
        int start = fullRange.StartPosition;
        while (start > 0)
        {
            var prev = editor.Document.GetRange(start - 1, start);
            prev.GetText(TextGetOptions.None, out var ch);
            if (ch == "\r" || ch == "\n" || !IsNoteLinkColor(prev.CharacterFormat.ForegroundColor)) break;
            start--;
        }

        // Read visible text (IncludeNumbering skips hidden) and full text (None includes hidden)
        var linkRange = editor.Document.GetRange(start, end);
        linkRange.GetText(TextGetOptions.IncludeNumbering, out var visibleText);
        linkRange.GetText(TextGetOptions.None, out var fullText2);
        visibleText = visibleText?.TrimEnd('\r', '\n') ?? "";
        fullText2 = fullText2?.TrimEnd('\r', '\n') ?? "";

        // The hidden ID is the part of fullText that's NOT in visibleText
        string? noteId = null;
        if (fullText2.Length > visibleText.Length && fullText2.StartsWith(visibleText, StringComparison.Ordinal))
        {
            noteId = fullText2[visibleText.Length..].Trim('\u200B');
        }

        // Fallback: search by visible title
        if (string.IsNullOrEmpty(noteId))
        {
            var target = _notesManager.GetByTitle(visibleText);
            if (target != null) noteId = target.Id;
        }

        if (noteId != null)
            NoteLinkClicked?.Invoke(noteId);
    }

    private static bool IsNoteLinkColor(Windows.UI.Color c)
        => c.R == NoteLinkColor.R && c.G == NoteLinkColor.G && c.B == NoteLinkColor.B;

    // ── Export ──────────────────────────────────────────────────

    private string GetPlainText()
    {
        NoteEditor.Document.GetText(TextGetOptions.None, out var text);
        return text.TrimEnd('\r', '\n');
    }

    private string ConvertNoteToMarkdown()
    {
        var doc = NoteEditor.Document;
        doc.GetText(TextGetOptions.None, out var fullText);
        fullText = fullText.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(fullText)) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"# {_note.Title}");
        sb.AppendLine();

        int totalLen = fullText.Length;
        int pos = 0;

        while (pos < totalLen)
        {
            int paraEnd = fullText.IndexOf('\r', pos);
            if (paraEnd < 0) paraEnd = totalLen;

            if (paraEnd == pos) { sb.AppendLine(); pos = paraEnd + 1; continue; }

            var paraRange = doc.GetRange(pos, paraEnd);
            bool isBullet = paraRange.ParagraphFormat.ListType == MarkerType.Bullet;

            var firstChar = doc.GetRange(pos, pos + 1);
            float headingSize = firstChar.CharacterFormat.Size;
            string prefix = "";
            if (headingSize >= 24f) prefix = "# ";
            else if (headingSize >= 20f) prefix = "## ";
            else if (headingSize >= 16f) prefix = "### ";
            if (isBullet) prefix = "- ";

            sb.Append(prefix);
            bool isHeading = prefix.StartsWith('#');

            int runPos = pos;
            while (runPos < paraEnd)
            {
                var range = doc.GetRange(runPos, runPos);
                int moved = range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
                if (moved == 0 || range.EndPosition <= runPos) { runPos++; continue; }
                if (range.EndPosition > paraEnd) range.SetRange(runPos, paraEnd);

                var fmt = range.CharacterFormat;
                range.GetText(TextGetOptions.None, out var runText);
                runText = runText.TrimEnd('\r');

                bool bold = fmt.Bold == FormatEffect.On && !isHeading;
                bool italic = fmt.Italic == FormatEffect.On;
                bool strike = fmt.Strikethrough == FormatEffect.On;
                string link = range.Link?.Trim('"') ?? "";

                if (!string.IsNullOrEmpty(link))
                {
                    sb.Append($"[{runText}]({link})");
                }
                else
                {
                    if (strike) sb.Append("~~");
                    if (bold && italic) sb.Append("***");
                    else if (bold) sb.Append("**");
                    else if (italic) sb.Append('*');

                    sb.Append(runText);

                    if (bold && italic) sb.Append("***");
                    else if (bold) sb.Append("**");
                    else if (italic) sb.Append('*');
                    if (strike) sb.Append("~~");
                }

                runPos = range.EndPosition;
            }

            sb.AppendLine();
            pos = paraEnd + 1;
        }

        return sb.ToString().TrimEnd();
    }

    private string GetTaskListAsText()
    {
        var lines = _note.Tasks.Select(t => (t.IsDone ? "[x] " : "[ ] ") + t.Text);
        return string.Join("\n", lines);
    }

    private string GetTaskListAsMarkdown()
    {
        var lines = _note.Tasks.Select(t => (t.IsDone ? "- [x] " : "- [ ] ") + t.Text);
        return $"# {_note.Title}\n\n" + string.Join("\n", lines);
    }

    private string GetExportContent(bool asMarkdown)
    {
        if (asMarkdown)
            return _note.NoteType == "tasklist" ? GetTaskListAsMarkdown() : ConvertNoteToMarkdown();
        return _note.NoteType == "tasklist" ? GetTaskListAsText() : GetPlainText();
    }

    private void ShowExportMenu()
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE8A5", Lang.T("export_markdown"), [], () => ExportAsFile(".md")),
            new("\uE8A5", Lang.T("export_text"), [], () => ExportAsFile(".txt")),
            new("\uE8C8", Lang.T("copy_clipboard"), [], () => CopyNoteToClipboard(false)),
            new("\uE8C8", Lang.T("copy_markdown"), [], () => CopyNoteToClipboard(true)),
        };

        var flyout = ActionPanel.Create(Lang.T("export"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    private async void ExportAsFile(string preferredExt)
    {
        SaveCurrentNote();

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

        if (preferredExt == ".md")
        {
            picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
            picker.FileTypeChoices.Add("Text", new[] { ".txt" });
        }
        else
        {
            picker.FileTypeChoices.Add("Text", new[] { ".txt" });
            picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
        }
        picker.SuggestedFileName = _note.Title;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var content = GetExportContent(file.FileType == ".md");
        await System.IO.File.WriteAllTextAsync(file.Path, content);
    }

    private void CopyNoteToClipboard(bool asMarkdown)
    {
        SaveCurrentNote();
        var content = GetExportContent(asMarkdown);
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(content);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    // ── Drag & drop images ────────────────────────────────────

    private void NoteEditor_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Lang.T("insert");
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void NoteEditor_Drop(object sender, DragEventArgs e)
    {
        if (sender is not RichEditBox editor) return;

        try
        {
            // Handle dropped files (images)
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file)
                    {
                        var ext = file.FileType.ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                        {
                            await InsertImageFromFile(editor, file);
                        }
                    }
                }
            }
            // Handle dropped bitmap data (e.g. drag from browser)
            else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                var streamRef = await e.DataView.GetBitmapAsync();
                using var origStream = await streamRef.OpenReadAsync();
                await InsertImageFromStream(editor, origStream);
            }
        }
        catch { }
    }

    private async Task InsertImageFromFile(RichEditBox editor, Windows.Storage.StorageFile file)
    {
        using var fileStream = await file.OpenReadAsync();
        await InsertImageFromStream(editor, fileStream);
    }

    private async Task InsertImageFromStream(RichEditBox editor, Windows.Storage.Streams.IRandomAccessStream sourceStream)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(sourceStream);
        var w = (int)decoder.PixelWidth;
        var h = (int)decoder.PixelHeight;

        var softBitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var pngStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, pngStream);
        encoder.SetSoftwareBitmap(softBitmap);
        await encoder.FlushAsync();

        const int maxWidth = 340;
        if (w > maxWidth)
        {
            h = (int)(h * ((double)maxWidth / w));
            w = maxWidth;
        }

        pngStream.Seek(0);
        editor.Document.Selection.InsertImage(
            w, h, 0,
            Microsoft.UI.Text.VerticalCharacterAlignment.Baseline,
            "image",
            pngStream);
        // pngStream NOT disposed — RichEditBox holds reference for rendering

        editor.Focus(FocusState.Programmatic);

        if (editor == NoteEditor)
            SaveCurrentNote();
        else if (editor == TaskNoteEditor)
            SaveTaskNoteContent();
        AutoResizeWindow();
    }

    // ── Drag images out to Explorer ────────────────────────────

    private void Editor_PointerPressedForImageDrag(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichEditBox editor) return;
        _imageDragPending = false;
        _imageDragEditor = null;
        _imageDragPosition = -1;

        if (!e.GetCurrentPoint(editor).Properties.IsLeftButtonPressed) return;

        var pos = FindImageAtCursor(editor);
        if (pos >= 0)
        {
            _imageDragPending = true;
            _imageDragStart = e.GetCurrentPoint(editor).Position;
            _imageDragEditor = editor;
            _imageDragPosition = pos;
        }
    }

    private async void Editor_PointerMovedForImageDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!_imageDragPending || sender is not RichEditBox editor || editor != _imageDragEditor)
            return;

        var point = e.GetCurrentPoint(editor).Position;
        if (Math.Abs(point.X - _imageDragStart.X) < 8 &&
            Math.Abs(point.Y - _imageDragStart.Y) < 8)
            return;

        _imageDragPending = false;

        try
        {
            var imgRange = editor.Document.GetRange(_imageDragPosition, _imageDragPosition + 1);
            imgRange.GetText(TextGetOptions.FormatRtf, out var rtf);

            var bytes = ExtractImageBytesFromRtf(rtf);
            if (bytes == null) return;

            var ext = (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) ? ".jpg" : ".png";
            var safeTitle = string.Join("_",
                (_note.Title ?? "Image").Split(Path.GetInvalidFileNameChars()));
            if (safeTitle.Length > 30) safeTitle = safeTitle[..30];
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"{safeTitle}_{DateTime.Now:HHmmss}{ext}");
            await File.WriteAllBytesAsync(tempPath, bytes);
            _tempDragImagePath = tempPath;

            editor.CanDrag = true;
            editor.DragStarting += Editor_ImageDragStarting;

            try
            {
                await editor.StartDragAsync(e.GetCurrentPoint(editor));
            }
            finally
            {
                editor.CanDrag = false;
                editor.DragStarting -= Editor_ImageDragStarting;
                var path = _tempDragImagePath;
                _tempDragImagePath = null;
                if (path != null)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        try { File.Delete(path); } catch { }
                    });
            }
        }
        catch { }
    }

    private void Editor_PointerReleasedForImageDrag(object sender, PointerRoutedEventArgs e)
    {
        _imageDragPending = false;
        _imageDragEditor = null;
        _imageDragPosition = -1;
    }

    private async void Editor_ImageDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(_tempDragImagePath) || !File.Exists(_tempDragImagePath))
        {
            e.Cancel = true;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_tempDragImagePath);
            e.Data.SetStorageItems(new[] { file });
            e.Data.RequestedOperation =
                Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
        catch
        {
            e.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static int FindImageAtCursor(RichEditBox editor)
    {
        var sel = editor.Document.Selection;
        var start = sel.StartPosition;

        // Image selected (e.g. double-click or prior selection)
        if (sel.EndPosition - start == 1)
        {
            var r = editor.Document.GetRange(start, start + 1);
            r.GetText(TextGetOptions.None, out var ch);
            if (ch == "\uFFFC") return start;
        }

        // Cursor placed right before an image
        {
            var r = editor.Document.GetRange(start, start + 1);
            r.GetText(TextGetOptions.None, out var ch);
            if (!string.IsNullOrEmpty(ch) && ch[0] == '\uFFFC') return start;
        }

        // Cursor placed right after an image
        if (start > 0)
        {
            var r = editor.Document.GetRange(start - 1, start);
            r.GetText(TextGetOptions.None, out var ch);
            if (!string.IsNullOrEmpty(ch) && ch[0] == '\uFFFC') return start - 1;
        }

        return -1;
    }

    private static byte[]? ExtractImageBytesFromRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return null;

        var markerIdx = rtf.IndexOf("\\pngblip", StringComparison.Ordinal);
        if (markerIdx < 0) markerIdx = rtf.IndexOf("\\jpegblip", StringComparison.Ordinal);
        if (markerIdx < 0) return null;

        var i = markerIdx + 1;
        while (i < rtf.Length && rtf[i] != ' ' && rtf[i] != '\\' && rtf[i] != '}') i++;

        while (i < rtf.Length && rtf[i] != '}')
        {
            if (rtf[i] == '\\')
            {
                i++;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                if (i < rtf.Length && rtf[i] == ' ') i++;
            }
            else if (rtf[i] == ' ' || rtf[i] == '\r' || rtf[i] == '\n')
                i++;
            else break;
        }

        var sb = new StringBuilder();
        while (i < rtf.Length && rtf[i] != '}')
        {
            var c = rtf[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                sb.Append(c);
            i++;
        }

        var hex = sb.ToString();
        if (hex.Length < 16) return null;

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (int j = 0; j < bytes.Length; j++)
                bytes[j] = byte.Parse(hex.AsSpan(j * 2, 2),
                    System.Globalization.NumberStyles.HexNumber);
            return bytes;
        }
        catch { return null; }
    }

    // ── Screen capture ──────────────────────────────────────────

    private void StartScreenCapture(RichEditBox editor)
    {
        ScreenCaptureService.CaptureAndInsert(this, editor, () =>
        {
            if (editor == NoteEditor)
                SaveCurrentNote();
            else if (editor == TaskNoteEditor)
                SaveTaskNoteContent();
            AutoResizeWindow();
        });
    }

    // ── OCR capture ─────────────────────────────────────────────

    private void StartOcrCapture(RichEditBox editor)
    {
        var overlay = new OcrCaptureOverlay();
        overlay.CaptureCanceled += (_, _) => { };
        overlay.RegionCaptured += async (_, region) =>
        {
            var text = await OcrService.ExtractTextAsync(region.Pixels, region.Width, region.Height);
            if (!string.IsNullOrEmpty(text))
            {
                editor.Document.Selection.TypeText(text);
                editor.Focus(FocusState.Programmatic);
                if (editor == NoteEditor)
                    SaveCurrentNote();
                else if (editor == TaskNoteEditor)
                    SaveTaskNoteContent();
                AutoResizeWindow();
            }
        };
        overlay.Activate();
    }

    // ── Voice dictation ─────────────────────────────────────────

    private void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (_isVoiceRecording)
            StopVoiceRecording();
        else
            StartVoiceRecording();
    }

    private void StartVoiceRecording()
    {
        // Load saved model from VoiceNoteWindow settings
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteUI", "voice_settings.json");

        SttModelInfo? model = null;
        try
        {
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var modelId = doc.RootElement.GetProperty("selectedModelId").GetString();
                if (modelId != null)
                    model = SttModels.Available.FirstOrDefault(m => m.Id == modelId);
            }
        }
        catch { }

        if (model == null || !model.IsDownloaded)
        {
            // No model configured — show a tip
            var tip = new TeachingTip
            {
            Title = Lang.T("voice_model_not_configured"),
                Subtitle = Lang.T("configure_model_hint"),
                IsLightDismissEnabled = true,
                PreferredPlacement = TeachingTipPlacementMode.Top,
                Target = VoiceButton,
            };
            RootGrid.Children.Add(tip);
            tip.IsOpen = true;
            tip.Closed += (_, _) => RootGrid.Children.Remove(tip);
            return;
        }

        // For Groq cloud models, check API key
        string? groqKey = null;
        if (model.Engine == SttEngine.GroqCloud)
        {
            try { _aiManager ??= new AiManager(); _aiManager.Load(); } catch { }
            groqKey = _aiManager?.GetApiKey("groq");
            if (string.IsNullOrWhiteSpace(groqKey))
            {
                var tip = new TeachingTip
                {
                    Title = Lang.T("groq_key_required"),
                    Subtitle = Lang.T("configure_model_hint"),
                    IsLightDismissEnabled = true,
                    PreferredPlacement = TeachingTipPlacementMode.Top,
                    Target = VoiceButton,
                };
                RootGrid.Children.Add(tip);
                tip.IsOpen = true;
                tip.Closed += (_, _) => RootGrid.Children.Remove(tip);
                return;
            }
        }

        try
        {
            _voiceRecognizer = SpeechRecognizerFactory.Create(model, groqKey);
        }
        catch
        {
            return;
        }

        _voiceRecognizer.OnFinalResult += text =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isVoiceRecording) return;
                if (_note.NoteType == "tasklist") return;
                var sel = NoteEditor.Document.Selection;
                if (sel != null)
                {
                    sel.TypeText(text + " ");
                    SaveCurrentNote();
                }
            });
        };

        _voiceRecognizer.Start();
        _isVoiceRecording = true;
        VoiceIcon.Glyph = "\uE71A"; // Stop icon
        VoiceIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
    }

    private void StopVoiceRecording()
    {
        _isVoiceRecording = false;
        var recognizer = _voiceRecognizer;
        _voiceRecognizer = null;

        VoiceIcon.Glyph = "\uE720"; // Mic icon
        VoiceIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // Stop and dispose on background thread to avoid UI deadlock
        if (recognizer != null)
        {
            Task.Run(() =>
            {
                try { recognizer.Stop(); } catch { }
                try { recognizer.Dispose(); } catch { }
            });
        }
    }

    private async void ShowLinkFlyout()
    {
        var sel = NoteEditor.Document.Selection;
        sel.GetText(Microsoft.UI.Text.TextGetOptions.None, out var selectedText);
        selectedText = selectedText.TrimEnd('\r', '\n');

        var displayBox = new TextBox
        {
            PlaceholderText = Lang.T("link_text_optional"),
            Header = Lang.T("display_text"),
            FontSize = 13,
            Text = selectedText ?? ""
        };

        var urlBox = new TextBox
        {
            PlaceholderText = Lang.T("link_to_webpage"),
            Header = Lang.T("address"),
            FontSize = 13
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(displayBox);
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            Title = Lang.T("link"),
            Content = panel,
            PrimaryButtonText = Lang.T("insert"),
            CloseButtonText = Lang.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var url = urlBox.Text.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                var display = displayBox.Text.Trim();
                var docSel = NoteEditor.Document.Selection;

                if (!string.IsNullOrEmpty(display) && string.IsNullOrEmpty(selectedText))
                {
                    docSel.TypeText(display);
                    docSel.SetRange(docSel.EndPosition - display.Length, docSel.EndPosition);
                }

                docSel.Link = "\"" + url + "\"";
                docSel.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 96, 180, 255);
            }
        }
        NoteEditor.Focus(FocusState.Programmatic);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var favLabel = _note.IsFavorite ? Lang.T("remove_favorite") : Lang.T("add_favorite");
        var favGlyph = _note.IsFavorite ? "\uE735" : "\uE734";
        var reminderLabel = _note.ReminderAt != null
            ? $"{Lang.T("reminder")} ({_note.ReminderAt:dd/MM HH:mm})"
            : Lang.T("set_reminder");
        var tagLabel = _note.Tags.Count > 0
            ? $"{Lang.T("tags")} ({string.Join(", ", _note.Tags)})"
            : Lang.T("tags");
        var folderLabel = !string.IsNullOrEmpty(_note.Folder)
            ? $"{Lang.T("folder")} ({_note.Folder})"
            : Lang.T("folder");

        var autoResizeLabel = _autoResize ? Lang.T("disable_auto_resize") : Lang.T("auto_resize");
        var autoResizeGlyph = _autoResize ? "\uE73F" : "\uE740";

        var actions = new List<ActionPanel.ActionItem>
        {
            new(favGlyph, favLabel, [], () =>
            {
                _notesManager.ToggleFavorite(_note.Id);
                UpdateMenuIcon();
                NoteChanged?.Invoke();
            }),
            new("\uE823", reminderLabel, [], () => ShowNoteReminderMenu()),
            new("\uE8EC", tagLabel, [], () => ShowTagMenu()),
            new("\uE8B7", folderLabel, [], () => ShowFolderMenu()),
            new(autoResizeGlyph, autoResizeLabel, [], () =>
            {
                _autoResize = !_autoResize;
                if (_autoResize) AutoResizeWindow();
            }),
            new("\uE70F", Lang.T("text_editor"), [], () =>
            {
                SaveCurrentNote();
                OpenInNotepadRequested?.Invoke();
            }),
            new("\uE943", Lang.T("snippet"), [], () =>
            {
                if (_snippetManager == null) return;
                SaveCurrentNote();
                ActionPanel.ShowSnippetFlyout(MenuButton, _note.Id, _snippetManager, _note.Content);
            }),
            new("\uE723",
                !string.IsNullOrEmpty(_note.AttachMode)
                    ? $"{Lang.T("attached_to")} {GetAttachTargetLabel()}"
                    : Lang.T("attach_to_window"), [], () => ShowAttachMenu()),
            new("\uE8A5", Lang.T("export"), [], () => ShowExportMenu()),
            new("\uE7B8", _note.IsArchived ? Lang.T("unarchive") : Lang.T("archive"), [], () =>
            {
                _notesManager.ToggleArchive(_note.Id);
                NoteChanged?.Invoke();
                ArchiveRequested?.Invoke();
                if (_note.IsArchived) this.Close();
            }),
            new("\uE74D", Lang.T("delete_note"), [], () =>
            {
                _notesManager.DeleteNote(_note.Id);
                this.Close();
            }, IsDestructive: true),
        };

        var flyout = ActionPanel.Create(Lang.T("actions"), actions);
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    // ── Attachment menu ────────────────────────────────────────

    private void ShowAttachMenu()
    {
        var actions = new List<ActionPanel.ActionItem>
        {
            new("\uE737", Lang.T("attach_to_program"), [], () => ShowRunningProgramsList()),
            new("\uE774", Lang.T("attach_to_website"), [], () => ShowWebTabsList()),
            new("\uE8B7", Lang.T("attach_to_folder"), [], async () => await PickAttachFolder()),
        };

        if (!string.IsNullOrEmpty(_note.AttachMode))
        {
            actions.Add(new("\uE711", Lang.T("detach"), [], () =>
            {
                _note.AttachTarget = null;
                _note.AttachMode = null;
                _note.AttachOffsetX = 0;
                _note.AttachOffsetY = 0;
                _notesManager.Save();
                UpdateAttachIcon();
                NoteChanged?.Invoke();
                AttachmentChanged?.Invoke();
            }, IsDestructive: true));
        }

        var flyout = ActionPanel.Create(Lang.T("attach_to_window"), actions);
        flyout.ShowAt(MenuButton);
    }

    private void ShowRunningProgramsList()
    {
        var programs = WindowAttachmentHelper.GetVisibleWindows();
        if (programs.Count == 0)
        {
            var empty = ActionPanel.Create(Lang.T("select_program"),
                [new(null, Lang.T("no_windows_found"), [], () => { })]);
            empty.ShowAt(MenuButton);
            return;
        }

        var actions = programs.Select(p =>
            new ActionPanel.ActionItem(null, $"{p.ProcessName}  —  {p.Title}", [], () =>
            {
                _note.AttachTarget = p.ProcessName;
                _note.AttachMode = "process";
                _note.AttachOffsetX = 0;
                _note.AttachOffsetY = 0;
                _notesManager.Save();
                UpdateAttachIcon();
                NoteChanged?.Invoke();
                AttachmentChanged?.Invoke();
            })
        ).ToList();

        var flyout = ActionPanel.Create(Lang.T("select_program"), actions);
        flyout.ShowAt(MenuButton);
    }

    private void ShowWebTabsList()
    {
        var tabs = WindowAttachmentHelper.GetVisibleWebTabs();
        if (tabs.Count == 0)
        {
            var empty = ActionPanel.Create(Lang.T("select_website"),
                [new(null, Lang.T("no_web_tabs_found"), [], () => { })]);
            empty.ShowAt(MenuButton);
            return;
        }

        var actions = tabs.Select(tab =>
            new ActionPanel.ActionItem(null, $"{tab.ProcessName}  —  {tab.Title}", [], () =>
            {
                _note.AttachTarget = tab.Title;
                _note.AttachMode = "title";
                _note.AttachOffsetX = 0;
                _note.AttachOffsetY = 0;
                _notesManager.Save();
                UpdateAttachIcon();
                NoteChanged?.Invoke();
                AttachmentChanged?.Invoke();
            })
        ).ToList();

        var flyout = ActionPanel.Create(Lang.T("select_website"), actions);
        flyout.ShowAt(MenuButton);
    }

    private async Task PickAttachFolder()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _note.AttachTarget = folder.Path;
        _note.AttachMode = "folder";
        _note.AttachOffsetX = 0;
        _note.AttachOffsetY = 0;
        _notesManager.Save();
        UpdateAttachIcon();
        NoteChanged?.Invoke();
        AttachmentChanged?.Invoke();
    }

    private void TitleBarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hide title when window is too narrow to avoid overlap with buttons
        // MenuButton ~40px + right buttons ~160px = ~200px reserved
        const double minWidthForTitle = 240;
        var shouldShow = e.NewSize.Width >= minWidthForTitle && !_isCompact || _isCompact;
        TitleText.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAttachIcon()
    {
        if (AttachIcon != null)
        {
            AttachIcon.Visibility = !string.IsNullOrEmpty(_note.AttachMode)
                ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(_note.AttachMode))
            {
                ToolTipService.SetToolTip(AttachIcon, $"{Lang.T("attached_to")} {GetAttachTargetLabel()}");
            }
        }
    }

    private string GetAttachTargetLabel()
    {
        if (string.IsNullOrWhiteSpace(_note.AttachTarget))
            return "";

        return _note.AttachMode switch
        {
            "process" => _note.AttachTarget!,
            "folder" => System.IO.Path.GetFileName(_note.AttachTarget!),
            "title" => _note.AttachTarget!,
            _ => _note.AttachTarget!
        };
    }

    private void ShowTaskReminderDialog(TaskItem task, FontIcon bellIcon, Button reminderBtn)
    {
        var tertiaryBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        // If reminder already set, offer to change or remove
        if (task.ReminderAt != null)
        {
            var actions = new List<ActionPanel.ActionItem>
            {
                new("\uE70F", $"{Lang.T("edit")} ({task.ReminderAt:dd/MM HH:mm})", [], () => EditTaskReminder(task, bellIcon, reminderBtn)),
                new("\uE711", Lang.T("delete_reminder"), [], () =>
                {
                    task.ReminderAt = null;
                    bellIcon.Glyph = "\uE823";
                    bellIcon.Foreground = tertiaryBrush;
                    reminderBtn.Opacity = 0;
                    ToolTipService.SetToolTip(reminderBtn, null);
                    SaveTasks();
                }),
            };
            var flyout = ActionPanel.Create(Lang.T("reminder"), actions);
            flyout.ShowAt(reminderBtn);
            return;
        }

        EditTaskReminder(task, bellIcon, reminderBtn);
    }

    private async Task EditTaskReminder(TaskItem task, FontIcon bellIcon, Button reminderBtn)
    {
        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var now = DateTime.Now;
        var defaultDate = task.ReminderAt ?? now.AddHours(1);

        var datePicker = new DatePicker
        {
            Date = new DateTimeOffset(defaultDate.Date),
            MinYear = new DateTimeOffset(now.Date),
            MaxYear = new DateTimeOffset(now.Date.AddYears(1)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var timePicker = new TimePicker
        {
            Time = defaultDate.TimeOfDay,
            ClockIdentifier = "24HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16), MinWidth = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("set_reminder"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(datePicker);
        panel.Children.Add(timePicker);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var cancelBtn = new Button { Content = Lang.T("cancel") };
        var setBtn = new Button
        {
            Content = Lang.T("set_reminder"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(setBtn);
        panel.Children.Add(btnRow);

        var flyout = new Flyout { Content = panel };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(300, 360);
        cancelBtn.Click += (_, _) => flyout.Hide();
        setBtn.Click += (_, _) =>
        {
            var reminderDate = datePicker.Date.DateTime.Date + timePicker.Time;
            if (reminderDate <= DateTime.Now)
                reminderDate = reminderDate.AddDays(1);

            task.ReminderAt = reminderDate;
            bellIcon.Glyph = "\uEA8F";
            bellIcon.Foreground = primaryBrush;
            reminderBtn.Opacity = 1;
            ToolTipService.SetToolTip(reminderBtn, $"{reminderDate:dd/MM HH:mm}");
            SaveTasks();
            flyout.Hide();
        };
        flyout.ShowAt(reminderBtn);
    }

    // ── Tags (menu) ─────────────────────────────────────────

    private void ShowTagMenu()
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(220, 280);

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(ActionPanel.CreateHeader(Lang.T("tags")));
        panel.Children.Add(ActionPanel.CreateSeparator());

        var allTags = _notesManager.GetAllTags();
        var noteTags = new HashSet<string>(_note.Tags, StringComparer.OrdinalIgnoreCase);

        foreach (var tag in allTags)
        {
            var tagName = tag;
            var isSelected = noteTags.Contains(tag);
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(5),
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new FontIcon
            {
                Glyph = "\uEA3B",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dot, 0);

            var text = new TextBlock
            {
                Text = tag,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);

            grid.Children.Add(dot);
            grid.Children.Add(text);

            if (isSelected)
            {
                var check = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(check, 2);
                grid.Children.Add(check);
            }

            btn.Content = grid;
            btn.Click += (_, _) =>
            {
                if (_note.Tags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                    _note.Tags.RemoveAll(t => t.Equals(tagName, StringComparison.OrdinalIgnoreCase));
                else
                    _note.Tags.Add(tagName);
                _notesManager.UpdateNoteTags(_note.Id, _note.Tags);
                NoteChanged?.Invoke();
                flyout.Hide();
            };
            panel.Children.Add(btn);
        }

        panel.Children.Add(ActionPanel.CreateSeparator());
        var addBox = new TextBox
        {
            PlaceholderText = Lang.T("new_tag"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        addBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                var newTag = addBox.Text.Trim();
                if (!string.IsNullOrEmpty(newTag) && !_note.Tags.Contains(newTag, StringComparer.OrdinalIgnoreCase))
                {
                    _note.Tags.Add(newTag);
                    _notesManager.UpdateNoteTags(_note.Id, _note.Tags);
                    NoteChanged?.Invoke();
                }
                flyout.Hide();
                args.Handled = true;
            }
        };
        panel.Children.Add(addBox);

        flyout.Content = panel;
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    private void ShowFolderMenu()
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(220, 280);

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(ActionPanel.CreateHeader(Lang.T("folder")));
        panel.Children.Add(ActionPanel.CreateSeparator());

        var allFolders = _notesManager.GetAllFolders();
        var currentFolder = _note.Folder ?? "";

        // "None" option to remove from folder
        var noneBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(5),
        };
        var noneGrid = new Grid { ColumnSpacing = 8 };
        noneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noneGrid.Children.Add(new TextBlock
        {
            Text = Lang.T("no_folder"),
            FontSize = 13,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (string.IsNullOrEmpty(currentFolder))
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E", FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 1);
            noneGrid.Children.Add(check);
        }
        noneBtn.Content = noneGrid;
        noneBtn.Click += (_, _) =>
        {
            _notesManager.UpdateNoteFolder(_note.Id, null);
            _note.Folder = null;
            NoteChanged?.Invoke();
            flyout.Hide();
        };
        panel.Children.Add(noneBtn);

        // Existing folders
        foreach (var folder in allFolders)
        {
            var folderName = folder;
            var isSelected = string.Equals(currentFolder, folder, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(5),
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = "\uE8B7", FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);

            var text = new TextBlock
            {
                Text = folder, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);

            grid.Children.Add(icon);
            grid.Children.Add(text);

            if (isSelected)
            {
                var check = new FontIcon
                {
                    Glyph = "\uE73E", FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(check, 2);
                grid.Children.Add(check);
            }

            btn.Content = grid;
            btn.Click += (_, _) =>
            {
                _notesManager.UpdateNoteFolder(_note.Id, folderName);
                _note.Folder = folderName;
                NoteChanged?.Invoke();
                flyout.Hide();
            };
            panel.Children.Add(btn);
        }

        // New folder input
        panel.Children.Add(ActionPanel.CreateSeparator());
        var addBox = new TextBox
        {
            PlaceholderText = Lang.T("new_folder"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        addBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                var newFolder = addBox.Text.Trim();
                if (!string.IsNullOrEmpty(newFolder))
                {
                    _notesManager.UpdateNoteFolder(_note.Id, newFolder);
                    _note.Folder = newFolder;
                    NoteChanged?.Invoke();
                }
                flyout.Hide();
                args.Handled = true;
            }
        };
        panel.Children.Add(addBox);

        flyout.Content = panel;
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(MenuButton);
    }

    // ── Reminder (menu) ──────────────────────────────────────

    private void ShowNoteReminderMenu()
    {
        if (_note.ReminderAt != null)
        {
            var actions = new List<ActionPanel.ActionItem>
            {
                new("\uE70F", $"{Lang.T("edit")} ({_note.ReminderAt:dd/MM HH:mm})", [], () => EditNoteReminder()),
                new("\uE711", Lang.T("delete_reminder"), [], () =>
                {
                    _note.ReminderAt = null;
                    _notesManager.UpdateNoteReminder(_note.Id, null);
                    NoteChanged?.Invoke();
                }),
            };
            var flyout = ActionPanel.Create(Lang.T("reminder"), actions);
            flyout.ShowAt(MenuButton);
            return;
        }

        EditNoteReminder();
    }

    private void EditNoteReminder()
    {
        var now = DateTime.Now;
        var defaultDate = _note.ReminderAt ?? now.AddHours(1);

        var datePicker = new DatePicker
        {
            Date = new DateTimeOffset(defaultDate.Date),
            MinYear = new DateTimeOffset(now.Date),
            MaxYear = new DateTimeOffset(now.Date.AddYears(1)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var timePicker = new TimePicker
        {
            Time = defaultDate.TimeOfDay,
            ClockIdentifier = "24HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16), MinWidth = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("set_reminder"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(datePicker);
        panel.Children.Add(timePicker);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var cancelBtn = new Button { Content = Lang.T("cancel") };
        var setBtn = new Button
        {
            Content = Lang.T("set_reminder"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(setBtn);
        panel.Children.Add(btnRow);

        var flyout = new Flyout { Content = panel };
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(300, 360);
        cancelBtn.Click += (_, _) => flyout.Hide();
        setBtn.Click += (_, _) =>
        {
            var reminderDate = datePicker.Date.DateTime.Date + timePicker.Time;
            if (reminderDate <= DateTime.Now)
                reminderDate = reminderDate.AddDays(1);

            _note.ReminderAt = reminderDate;
            _notesManager.UpdateNoteReminder(_note.Id, reminderDate);
            NoteChanged?.Invoke();
            flyout.Hide();
        };
        flyout.ShowAt(MenuButton);
    }

    // Spring scale animation for buttons on hover
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

    public void SetPinnedOnTop(bool pinned)
    {
        _isPinnedOnTop = pinned;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinnedOnTop;
        PinIcon.Glyph = _isPinnedOnTop ? "\uE77A" : "\uE718";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        SetPinnedOnTop(!_isPinnedOnTop);
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (_note.IsLocked)
        {
            SaveCurrentNote();
            this.Close();
            return;
        }

        if (!AppSettings.HasMasterPassword())
        {
            ActionPanel.ShowCreatePasswordFlyout(LockButton, password =>
            {
                var hash = AppSettings.HashPassword(password);
                AppSettings.SaveMasterPasswordHash(hash);
                _notesManager.ToggleLock(_note.Id);
                NoteChanged?.Invoke();
                _ = _notesManager.SyncSettingsToFirebase();
                SaveCurrentNote();
                this.Close();
            });
        }
        else
        {
            _notesManager.ToggleLock(_note.Id);
            NoteChanged?.Invoke();
            SaveCurrentNote();
            this.Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_note.NoteType != "tasklist")
            SaveCurrentNote();
        this.Close();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        SetCompactState(!_isCompact);
    }

    private void CompactAnimTick(object? sender, object e)
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
        var currentWidth = _isCompact ? AppWindow.Size.Width : _preCompactWidth;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(currentWidth, _currentAnimHeight));
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
            if (_note.NoteType != "tasklist")
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
