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

        if (note.NoteType == "tasklist")
        {
            NoteEditor.Visibility = Visibility.Collapsed;
            TaskListScroll.Visibility = Visibility.Visible;
            FormatButton.Visibility = Visibility.Collapsed;
            LoadTaskList();
        }
        else
        {
            LoadNote();
        }

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

        var grid = new Grid { Padding = new Thickness(4, 2, 4, 2) };
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
        content.Children.Add(new TextBlock { Text = "Ajouter une t\u00e2che", FontSize = 13, Foreground = tertiaryBrush });
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
        if (_note.Tasks.Count <= 1) return; // keep at least one
        var task = row.Tag as TaskItem;
        var index = TaskListPanel.Children.IndexOf(row);

        _note.Tasks.Remove(task!);
        TaskListPanel.Children.Remove(row);
        SaveTasks();

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
            _notesManager.UpdateNote(_note.Id, _note.Content, _note.Title, _note.Color);
            NoteChanged?.Invoke();
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

    private async Task ShowTaskReminderDialog(TaskItem task, FontIcon bellIcon, Button reminderBtn)
    {
        var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var tertiaryBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        // If reminder already set, offer to change or remove
        if (task.ReminderAt != null)
        {
            var actions = new List<ActionPanel.ActionItem>
            {
                new("\uE70F", $"Modifier ({task.ReminderAt:dd/MM HH:mm})", [], async () => await EditTaskReminder(task, bellIcon, reminderBtn)),
                new("\uE711", "Supprimer le rappel", [], () =>
                {
                    task.ReminderAt = null;
                    bellIcon.Glyph = "\uE823";
                    bellIcon.Foreground = tertiaryBrush;
                    reminderBtn.Opacity = 0;
                    ToolTipService.SetToolTip(reminderBtn, null);
                    SaveTasks();
                }),
            };
            var flyout = ActionPanel.Create("Rappel", actions);
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
        panel.Children.Add(new TextBlock { Text = "Date", FontSize = 12 });
        panel.Children.Add(datePicker);
        panel.Children.Add(new TextBlock { Text = "Heure", FontSize = 12 });
        panel.Children.Add(timeBox);
        panel.Children.Add(timeError);

        var dialog = new ContentDialog
        {
            Title = "D\u00e9finir un rappel",
            Content = panel,
            PrimaryButtonText = "D\u00e9finir",
            CloseButtonText = "Annuler",
            XamlRoot = this.Content.XamlRoot
        };

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (!TimeSpan.TryParse(timeBox.Text.Trim(), out var time) || time.TotalHours >= 24)
            {
                timeError.Text = "Format invalide (ex: 14:30)";
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
        CompactIcon.Glyph = _isCompact ? "\uE70D" : "\uE70E";

        if (_isCompact)
        {
            TitleText.Text = _note.Title;
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
