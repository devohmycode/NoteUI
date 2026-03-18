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

    private Flyout? _slashFlyout;

    private bool _autoResize;
    private const int AutoResizeMin = 150;
    private const int AutoResizeMax = 800;
    private const int NoteWindowWidth = 400;

    // Voice dictation
    private ISpeechRecognizer? _voiceRecognizer;
    private bool _isVoiceRecording;

    public string NoteId => _note.Id;

    public event Action? NoteChanged;
    public event Action? OpenInNotepadRequested;

    public record ParentRect(int X, int Y, int Width, int Height);

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

        ApplyNoteColor(note.Color);
        TitleText.Text = note.Title;
        UpdateMenuIcon();
        ApplyNoteLocalization();

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
            LoadNote();
        }

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
        ToolTipService.SetToolTip(ColorButton, Lang.T("tip_color"));
        TaskNoteEditor.PlaceholderText = Lang.T("notes_placeholder");
    }

    public void ApplyBackdrop(BackdropSettings settings)
    {
        AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
    }

    // ── Color ──────────────────────────────────────────────────

    private void ApplyNoteColor(string colorName)
    {
        if (NoteColors.IsNone(colorName))
        {
            TitleBarGrid.Background = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });
            ColorIndicator.Fill = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });
            ColorIndicator.Stroke = new SolidColorBrush(new Windows.UI.Color { A = 80, R = 128, G = 128, B = 128 });
            ColorIndicator.StrokeThickness = 1.5;
        }
        else
        {
            var darker = NoteColors.GetDarker(colorName, 0.93);
            TitleBarGrid.Background = new SolidColorBrush(darker);
            ColorIndicator.Fill = new SolidColorBrush(NoteColors.Get(colorName));
            ColorIndicator.Stroke = null;
            ColorIndicator.StrokeThickness = 0;
        }

        // Title bar foreground: use theme color when no note color, black otherwise (pastel backgrounds)
        var titleForeground = NoteColors.IsNone(colorName)
            ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Black);
        MenuIcon.Foreground = titleForeground;
        TitleText.Foreground = titleForeground;
        CompactIcon.Foreground = titleForeground;
        PinIcon.Foreground = titleForeground;
        CloseIcon.Foreground = titleForeground;

        // Subtle fade on color change
        AnimationHelper.FadeIn(TitleBarGrid, 200);
    }

    private void UpdateMenuIcon()
    {
        MenuIcon.Glyph = _note.IsFavorite ? "\uE735" : "\uE712";
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
                AppWindow.Resize(new Windows.Graphics.SizeInt32(NoteWindowWidth, totalHeight));
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
                AppWindow.Resize(new Windows.Graphics.SizeInt32(NoteWindowWidth, totalHeight));
        }
    }

    private void TaskNoteEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged) return;
        SaveTaskNoteContent();
        AutoResizeWindow();

        if (_slashFlyout == null && AppSettings.LoadSlashEnabled())
        {
            var slashPos = SlashCommands.DetectSlash(TaskNoteEditor);
            if (slashPos >= 0)
            {
                var actions = SlashCommands.RichEditActions(TaskNoteEditor, slashPos);
                var sp = slashPos;
                actions.Add(new ActionPanel.ActionItem("\uE720", Lang.T("audio"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartVoiceRecording();
                }));
                actions.Add(new ActionPanel.ActionItem("\uE71B", Lang.T("link"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    DispatcherQueue.TryEnqueue(() => ShowTaskNoteLinkFlyout());
                }));
                actions.Add(new ActionPanel.ActionItem("\uE722", Lang.T("capture"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartScreenCapture(TaskNoteEditor);
                }));
                actions.Add(new ActionPanel.ActionItem("\uE8F4", Lang.T("extract_text"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    StartOcrCapture(TaskNoteEditor);
                }));
                actions.Add(new ActionPanel.ActionItem("\uE70F", Lang.T("text_editor"), [], () =>
                {
                    SlashCommands.DeleteSlash(TaskNoteEditor, sp);
                    SaveTaskNoteContent();
                    OpenInNotepadRequested?.Invoke();
                }));
                _slashFlyout = SlashCommands.Show(TaskNoteEditor, actions, () => _slashFlyout = null);
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

        reminderBtn.Click += async (_, _) => await ShowTaskReminderDialog(task, bellIcon, reminderBtn);
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
        if (_suppressTextChanged) return;
        SaveCurrentNote();
        AutoResizeWindow();

        if (_slashFlyout == null && AppSettings.LoadSlashEnabled())
        {
            var slashPos = SlashCommands.DetectSlash(NoteEditor);
            if (slashPos >= 0)
            {
                var actions = SlashCommands.RichEditActions(NoteEditor, slashPos);
                var sp = slashPos;
                actions.Add(new ActionPanel.ActionItem("\uE73A", Lang.T("task"), [], () => ConvertToTaskList(sp)));
                actions.Add(new ActionPanel.ActionItem("\uE720", Lang.T("audio"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartVoiceRecording();
                }));
                actions.Add(new ActionPanel.ActionItem("\uE71B", Lang.T("link"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    DispatcherQueue.TryEnqueue(() => ShowLinkFlyout());
                }));
                actions.Add(new ActionPanel.ActionItem("\uE722", Lang.T("capture"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartScreenCapture(NoteEditor);
                }));
                actions.Add(new ActionPanel.ActionItem("\uE8F4", Lang.T("extract_text"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    StartOcrCapture(NoteEditor);
                }));
                actions.Add(new ActionPanel.ActionItem("\uE70F", Lang.T("text_editor"), [], () =>
                {
                    SlashCommands.DeleteSlash(NoteEditor, sp);
                    SaveCurrentNote();
                    OpenInNotepadRequested?.Invoke();
                }));
                _slashFlyout = SlashCommands.Show(NoteEditor, actions, () => _slashFlyout = null);
            }
        }
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

        try
        {
            _voiceRecognizer = SpeechRecognizerFactory.Create(model);
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
            new("\uE823", reminderLabel, [], async () => await ShowNoteReminderMenu()),
            new("\uE8EC", tagLabel, [], () => ShowTagMenu()),
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

    private async Task ShowTaskReminderDialog(TaskItem task, FontIcon bellIcon, Button reminderBtn)
    {
        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var tertiaryBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        // If reminder already set, offer to change or remove
        if (task.ReminderAt != null)
        {
            var actions = new List<ActionPanel.ActionItem>
            {
                new("\uE70F", $"{Lang.T("edit")} ({task.ReminderAt:dd/MM HH:mm})", [], async () => await EditTaskReminder(task, bellIcon, reminderBtn)),
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

        await EditTaskReminder(task, bellIcon, reminderBtn);
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
            DayFormat = "{day.integer} {month.abbreviated}",
            Margin = new Thickness(0, 0, 0, 12)
        };

        var timeBox = new TextBox
        {
            Text = defaultDate.ToString("HH:mm"),
            PlaceholderText = "HH:mm",
            FontSize = 14,
            MaxLength = 5,
            InputScope = new InputScope(),
        };
        timeBox.InputScope.Names.Add(new InputScopeName(InputScopeNameValue.TimeHour));

        var timeError = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Lang.T("date"), FontSize = 12 });
        panel.Children.Add(datePicker);
        panel.Children.Add(new TextBlock { Text = Lang.T("time"), FontSize = 12 });
        panel.Children.Add(timeBox);
        panel.Children.Add(timeError);

        var dialog = new ContentDialog
        {
            Title = Lang.T("set_reminder"),
            Content = panel,
            PrimaryButtonText = Lang.T("set"),
            CloseButtonText = Lang.T("cancel"),
            XamlRoot = this.Content.XamlRoot
        };

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (!TimeSpan.TryParse(timeBox.Text.Trim(), out var time) || time.TotalHours >= 24)
            {
                timeError.Text = Lang.T("invalid_time_format");
                timeError.Visibility = Visibility.Visible;
                continue;
            }

            var reminderDate = datePicker.Date.DateTime.Date + time;
            if (reminderDate <= DateTime.Now)
                reminderDate = reminderDate.AddDays(1);

            task.ReminderAt = reminderDate;
            bellIcon.Glyph = "\uEA8F";
            bellIcon.Foreground = primaryBrush;
            reminderBtn.Opacity = 1;
            ToolTipService.SetToolTip(reminderBtn, $"{reminderDate:dd/MM HH:mm}");
            SaveTasks();
            break;
        }
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

    // ── Reminder (menu) ──────────────────────────────────────

    private async Task ShowNoteReminderMenu()
    {
        if (_note.ReminderAt != null)
        {
            var actions = new List<ActionPanel.ActionItem>
            {
                new("\uE70F", $"{Lang.T("edit")} ({_note.ReminderAt:dd/MM HH:mm})", [], async () => await EditNoteReminder()),
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

        await EditNoteReminder();
    }

    private async Task EditNoteReminder()
    {
        var now = DateTime.Now;
        var defaultDate = _note.ReminderAt ?? now.AddHours(1);

        var datePicker = new DatePicker
        {
            Date = new DateTimeOffset(defaultDate.Date),
            MinYear = new DateTimeOffset(now.Date),
            MaxYear = new DateTimeOffset(now.Date.AddYears(1)),
            DayFormat = "{day.integer} {month.abbreviated}",
            Margin = new Thickness(0, 0, 0, 12)
        };

        var timeBox = new TextBox
        {
            Text = defaultDate.ToString("HH:mm"),
            PlaceholderText = "HH:mm",
            FontSize = 14,
            MaxLength = 5,
            InputScope = new InputScope(),
        };
        timeBox.InputScope.Names.Add(new InputScopeName(InputScopeNameValue.TimeHour));

        var timeError = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Lang.T("date"), FontSize = 12 });
        panel.Children.Add(datePicker);
        panel.Children.Add(new TextBlock { Text = Lang.T("time"), FontSize = 12 });
        panel.Children.Add(timeBox);
        panel.Children.Add(timeError);

        var dialog = new ContentDialog
        {
            Title = Lang.T("set_reminder"),
            Content = panel,
            PrimaryButtonText = Lang.T("set"),
            CloseButtonText = Lang.T("cancel"),
            XamlRoot = this.Content.XamlRoot
        };

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (!TimeSpan.TryParse(timeBox.Text.Trim(), out var time) || time.TotalHours >= 24)
            {
                timeError.Text = Lang.T("invalid_time_format");
                timeError.Visibility = Visibility.Visible;
                continue;
            }

            var reminderDate = datePicker.Date.DateTime.Date + time;
            if (reminderDate <= DateTime.Now)
                reminderDate = reminderDate.AddDays(1);

            _note.ReminderAt = reminderDate;
            _notesManager.UpdateNoteReminder(_note.Id, reminderDate);
            NoteChanged?.Invoke();
            break;
        }
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

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedOnTop = !_isPinnedOnTop;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinnedOnTop;

        PinIcon.Glyph = _isPinnedOnTop ? "\uE77A" : "\uE718";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_note.NoteType != "tasklist")
            SaveCurrentNote();
        this.Close();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _isCompact = !_isCompact;

        // Rotate chevron with spring animation
        var iconVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CompactIcon);
        var compositor = iconVisual.Compositor;
        iconVisual.CenterPoint = new System.Numerics.Vector3(
            (float)(CompactIcon.ActualWidth / 2),
            (float)(CompactIcon.ActualHeight / 2), 0);
        var rotAnim = compositor.CreateSpringScalarAnimation();
        rotAnim.FinalValue = _isCompact ? 180f : 0f;
        rotAnim.DampingRatio = 0.7f;
        rotAnim.Period = TimeSpan.FromMilliseconds(60);
        iconVisual.StartAnimation("RotationAngleInDegrees", rotAnim);

        if (_isCompact)
        {
            TitleText.Text = _note.Title;
            AnimationHelper.FadeOut(MenuButton, 100, () => MenuButton.Visibility = Visibility.Collapsed);
            if (_note.NoteType == "tasklist")
            {
                AnimationHelper.FadeOut(TaskListScroll, 120, () =>
                {
                    TaskListScroll.Visibility = Visibility.Collapsed;
                });
            }
            else
            {
                AnimationHelper.FadeOut(NoteEditor, 120, () =>
                {
                    NoteEditor.Visibility = Visibility.Collapsed;
                });
            }
            AnimationHelper.FadeOut(StatusBar, 120, () =>
            {
                StatusBar.Visibility = Visibility.Collapsed;
            });
        }
        else
        {
            MenuButton.Visibility = Visibility.Visible;
            AnimationHelper.FadeIn(MenuButton, 200, 60);
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            if (_note.NoteType == "tasklist")
            {
                TaskListScroll.Visibility = Visibility.Visible;
                AnimationHelper.FadeIn(TaskListScroll, 200, 100);
            }
            else
            {
                NoteEditor.Visibility = Visibility.Visible;
                AnimationHelper.FadeIn(NoteEditor, 200, 100);
            }
            StatusBar.Visibility = Visibility.Visible;
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
