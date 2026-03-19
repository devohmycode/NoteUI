using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

internal static class SlashCommands
{
    /// <summary>Returns position of "/" if just typed at start-of-line or after whitespace, else -1.</summary>
    public static int DetectSlash(RichEditBox editor)
    {
        var sel = editor.Document.Selection;
        if (sel.StartPosition != sel.EndPosition) return -1;
        var pos = sel.StartPosition;
        if (pos < 1) return -1;

        var range = editor.Document.GetRange(pos - 1, pos);
        range.GetText(TextGetOptions.None, out var ch);
        if (ch != "/") return -1;

        return pos - 1;
    }

    /// <summary>Returns position of "/" if just typed at start or after whitespace in a TextBox, else -1.</summary>
    public static int DetectSlash(TextBox textBox)
    {
        var pos = textBox.SelectionStart;
        if (pos < 1 || pos > textBox.Text.Length) return -1;
        if (textBox.Text[pos - 1] != '/') return -1;

        return pos - 1;
    }

    public static void DeleteSlash(RichEditBox editor, int slashPos)
    {
        if (slashPos < 0) return;
        var range = editor.Document.GetRange(slashPos, slashPos + 1);
        range.Delete(TextRangeUnit.Character, 1);
    }

    public static void DeleteSlash(TextBox textBox, int slashPos)
    {
        if (slashPos < 0 || slashPos >= textBox.Text.Length) return;
        textBox.Text = textBox.Text.Remove(slashPos, 1);
        textBox.SelectionStart = slashPos;
    }

    /// <summary>Show slash-command flyout positioned at cursor in a RichEditBox.</summary>
    public static Flyout Show(RichEditBox editor, IReadOnlyList<ActionPanel.ActionItem> actions, Action? onClosed = null)
    {
        editor.Document.Selection.GetRect(PointOptions.ClientCoordinates, out var rect, out _);
        var pos = new Windows.Foundation.Point(
            Math.Max(0, rect.X),
            Math.Max(0, rect.Y + rect.Height) + 4);
        return Show((FrameworkElement)editor, pos, actions, onClosed);
    }

    /// <summary>Show slash-command flyout at a given position relative to target.</summary>
    public static Flyout Show(FrameworkElement target, Windows.Foundation.Point position,
        IReadOnlyList<ActionPanel.ActionItem> actions, Action? onClosed = null)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle();

        var panel = BuildPanel(Lang.T("commands"), actions, flyout);

        flyout.Opened += (_, _) => FocusSearchBox(panel);
        if (onClosed != null) flyout.Closed += (_, _) => onClosed();

        flyout.Content = panel;
        flyout.ShowAt(target, new FlyoutShowOptions { Position = position });
        return flyout;
    }

    /// <summary>Open a new flyout for a submenu (Format / IA). Esc or light-dismiss calls onEscBack to reopen parent.</summary>
    internal static Flyout ShowSubFlyout(FrameworkElement target, string header,
        IReadOnlyList<ActionPanel.ActionItem> actions, Action? onClosed = null, Action? onEscBack = null)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle();

        bool actionExecuted = false;

        // Wrap actions to track if user clicked an action vs dismissed with Esc
        var wrapped = new List<ActionPanel.ActionItem>(actions.Count);
        foreach (var a in actions)
        {
            var orig = a;
            wrapped.Add(new(orig.Glyph, orig.Label, orig.Keys,
                () => { actionExecuted = true; orig.Handler(); },
                orig.Icon, orig.IsDestructive));
        }

        var panel = BuildPanel(header, wrapped, flyout);

        flyout.Opened += (_, _) => FocusSearchBox(panel);
        flyout.Closed += (_, _) =>
        {
            if (!actionExecuted && onEscBack != null)
                onEscBack();
            else
                onClosed?.Invoke();
        };

        flyout.Content = panel;

        if (target is RichEditBox editor)
        {
            editor.Document.Selection.GetRect(PointOptions.ClientCoordinates, out var rect, out _);
            var pos = new Windows.Foundation.Point(
                Math.Max(0, rect.X),
                Math.Max(0, rect.Y + rect.Height) + 4);
            flyout.ShowAt(target, new FlyoutShowOptions { Position = pos });
        }
        else
        {
            flyout.ShowAt(target);
        }

        return flyout;
    }

    /// <summary>Builds a panel with header, search box, and action buttons.</summary>
    private static StackPanel BuildPanel(string header, IReadOnlyList<ActionPanel.ActionItem> actions, Flyout flyout)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(ActionPanel.CreateHeader(header));

        var searchBox = new TextBox
        {
            PlaceholderText = Lang.T("filter"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        panel.Children.Add(searchBox);
        panel.Children.Add(ActionPanel.CreateSeparator());

        var buttons = new List<Button>();
        foreach (var action in actions)
        {
            var a = action;
            var btn = ActionPanel.CreateButton(a, () => { a.Handler(); flyout.Hide(); });
            buttons.Add(btn);
            panel.Children.Add(btn);
        }

        searchBox.TextChanged += (_, _) =>
        {
            var query = searchBox.Text;
            foreach (var btn in buttons)
            {
                var label = btn.Tag as string ?? "";
                btn.Visibility = string.IsNullOrEmpty(query) ||
                    label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };

        return panel;
    }

    private static void FocusSearchBox(StackPanel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is TextBox tb)
            {
                tb.Focus(FocusState.Programmatic);
                break;
            }
        }
    }

    /// <summary>Common formatting slash-commands for a RichEditBox.</summary>
    public static List<ActionPanel.ActionItem> RichEditActions(RichEditBox editor, int slashPos, Action? onAfterCommand = null)
    {
        var primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        void Run(Action action)
        {
            DeleteSlash(editor, slashPos);
            action();
            editor.Focus(FocusState.Programmatic);
            onAfterCommand?.Invoke();
        }

        return
        [
            new(null, Lang.T("heading1"), [], () => Run(() => ApplyHeading(editor, 24f)),
                Icon: Icon("H1", primary, bold: true)),
            new(null, Lang.T("heading2"), [], () => Run(() => ApplyHeading(editor, 20f)),
                Icon: Icon("H2", primary, bold: true)),
            new(null, Lang.T("heading3"), [], () => Run(() => ApplyHeading(editor, 16f)),
                Icon: Icon("H3", primary, bold: true)),
            new(null, Lang.T("bold"), ["Ctrl", "B"], () => Run(() => Toggle(editor, f => f.Bold, (f, v) => f.Bold = v)),
                Icon: Icon("B", primary, bold: true)),
            new(null, Lang.T("italic"), ["Ctrl", "I"], () => Run(() => Toggle(editor, f => f.Italic, (f, v) => f.Italic = v)),
                Icon: Icon("I", primary, italic: true)),
            new(null, Lang.T("underline"), ["Ctrl", "U"], () => Run(() =>
            {
                var s = editor.Document.Selection;
                s.CharacterFormat.Underline = s.CharacterFormat.Underline != UnderlineType.None
                    ? UnderlineType.None : UnderlineType.Single;
            }), Icon: Icon("S", primary, underline: true)),
            new(null, Lang.T("strikethrough"), [], () => Run(() => Toggle(editor, f => f.Strikethrough, (f, v) => f.Strikethrough = v)),
                Icon: Icon("ab", primary, strikethrough: true)),
            new("\uE8FD", Lang.T("bullet_list"), [], () => Run(() =>
            {
                var s = editor.Document.Selection;
                s.ParagraphFormat.ListType = s.ParagraphFormat.ListType == MarkerType.Bullet
                    ? MarkerType.None : MarkerType.Bullet;
            })),
            new("\uE787", Lang.T("datetime"), ["F5"], () => Run(() =>
                editor.Document.Selection.TypeText(DateTime.Now.ToString("HH:mm dd/MM/yyyy")))),
        ];
    }

    /// <summary>Basic slash-commands for a plain TextBox.</summary>
    public static List<ActionPanel.ActionItem> TextBoxActions(TextBox textBox, int slashPos)
    {
        void Run(Action action)
        {
            DeleteSlash(textBox, slashPos);
            action();
            textBox.Focus(FocusState.Programmatic);
        }

        return
        [
            new("\uE787", Lang.T("datetime"), [], () => Run(() =>
            {
                var p = textBox.SelectionStart;
                var dt = DateTime.Now.ToString("HH:mm dd/MM/yyyy");
                textBox.Text = textBox.Text.Insert(p, dt);
                textBox.SelectionStart = p + dt.Length;
            })),
        ];
    }

    private static void ApplyHeading(RichEditBox editor, float fontSize)
    {
        var charFmt = editor.Document.Selection.CharacterFormat;
        charFmt.Size = fontSize;
        charFmt.Bold = fontSize > 14f ? FormatEffect.On : FormatEffect.Off;
    }

    private static void Toggle(RichEditBox editor,
        Func<ITextCharacterFormat, FormatEffect> getter,
        Action<ITextCharacterFormat, FormatEffect> setter)
    {
        var fmt = editor.Document.Selection.CharacterFormat;
        setter(fmt, getter(fmt) == FormatEffect.On ? FormatEffect.Off : FormatEffect.On);
    }

    internal static FrameworkElement Icon(string text, Brush fg,
        bool bold = false, bool italic = false, bool underline = false, bool strikethrough = false)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (bold) tb.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        if (italic) tb.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (underline) tb.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        if (strikethrough) tb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        return tb;
    }
}
