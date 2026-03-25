using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace NoteUI;

public static class ActionPanel
{
    public record ActionItem(string? Glyph, string Label, string[] Keys, Action Handler, FrameworkElement? Icon = null, bool IsDestructive = false);

    public static Style CreateFlyoutPresenterStyle(double minWidth = 240, double maxWidth = 300)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(3)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, minWidth));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, maxWidth));
        // Background/border: let WinUI resolve the correct themed brushes
        // via RequestedTheme set below – no manual brush resolution needed.
        style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)));

        // Force the flyout to match the app theme so all children inherit
        // the correct themed Foreground / Background automatically.
        var requestedTheme = ThemeHelper.IsDark() ? ElementTheme.Dark : ElementTheme.Light;
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, requestedTheme));

        return style;
    }

    public static Flyout Create(string header, IReadOnlyList<ActionItem> actions)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle();

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(CreateHeader(header));
        panel.Children.Add(CreateSeparator());

        var actionButtons = new List<Button>();
        bool lastWasDestructive = false;
        foreach (var action in actions)
        {
            if (action.IsDestructive && !lastWasDestructive)
            {
                panel.Children.Add(CreateSeparator());
                lastWasDestructive = true;
            }

            var btn = CreateButton(action, () =>
            {
                action.Handler();
                flyout.Hide();
            });
            actionButtons.Add(btn);
            panel.Children.Add(btn);
        }

        panel.Children.Add(CreateSeparator());

        var searchBox = new TextBox
        {
            PlaceholderText = Lang.T("search"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        searchBox.TextChanged += (_, _) =>
        {
            var query = searchBox.Text;
            foreach (var btn in actionButtons)
            {
                var label = btn.Tag as string ?? "";
                btn.Visibility = string.IsNullOrEmpty(query) ||
                    label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };
        panel.Children.Add(searchBox);

        flyout.Content = panel;
        return flyout;
    }

    public static Flyout CreateColorPicker(string header, string currentColor, Action<string> onColorSelected)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle();

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(CreateHeader(header));
        panel.Children.Add(CreateSeparator());

        foreach (var (name, hex) in NoteColors.All)
        {
            var isSelected = name == currentColor;
            var colorName = name;
            var display = NoteColors.GetDisplayName(name);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Tag = display
            };

        var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement dot;
            if (NoteColors.IsNone(name))
            {
                dot = new Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Stroke = new SolidColorBrush(
                        new Windows.UI.Color { A = 100, R = 128, G = 128, B = 128 }),
                    StrokeThickness = 1.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                dot = new Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(NoteColors.ColorFromHex(hex)),
                    Stroke = new SolidColorBrush(
                        new Windows.UI.Color { A = 60, R = 0, G = 0, B = 0 }),
                    StrokeThickness = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            Grid.SetColumn(dot, 0);

            var text = new TextBlock
            {
                Text = display,
                FontSize = 12,
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
                    FontSize = 12,
                    Opacity = 0.9,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(check, 2);
                grid.Children.Add(check);
            }

            btn.Content = grid;
            btn.Click += (_, _) =>
            {
                onColorSelected(colorName);
                flyout.Hide();
            };
            panel.Children.Add(btn);
        }

        flyout.Content = panel;
        return flyout;
    }

    public static Flyout CreateSettings(string currentTheme, string currentBackdropType,
        string currentNotesFolder, string defaultNotesFolder,
        bool isFirebaseConnected, string? firebaseEmail,
        bool isWebDavConnected, string? webDavUrl,
        Action<string> onThemeSelected, Action<string> onBackdropSelected,
        Action onChangeFolder, Action onResetFolder,
        Action onConfigureFirebase, Action onDisconnectFirebase, Action onSyncFirebase,
        Action onConfigureWebDav, Action onDisconnectWebDav, Action onSyncWebDav,
        Action<Flyout>? onShowVoiceModels = null,
        Action<Flyout>? onShowShortcuts = null,
        string? currentLanguage = null, bool slashEnabled = true,
        Action<string>? onLanguageSelected = null, Action<bool>? onSlashToggled = null,
        Action<Flyout>? onShowAi = null, Action<Flyout>? onShowPrompts = null,
        string? currentNoteStyle = null, Action<string>? onNoteStyleSelected = null,
        string? currentFont = null, Action<string>? onFontSelected = null,
        Action? onResetPassword = null, Action? onResetNotes = null)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle(260, 320);

        var panel = new StackPanel { Spacing = 0 };
        var allButtons = new List<Button>();

        static string FindLabel((string key, string label)[] items, string current)
        {
            foreach (var (k, l) in items) if (k == current) return l;
            return "";
        }

        // Theme
        var themes = new[] { ("system", Lang.T("theme_system")), ("light", Lang.T("theme_light")), ("dark", Lang.T("theme_dark")) };
        var themeBtn = CreateCascadeButton(Lang.T("theme"), FindLabel(themes, currentTheme),
            CreateRadioSubMenu("Theme", themes, currentTheme, k => { onThemeSelected(k); flyout.Hide(); }));
        allButtons.Add(themeBtn);
        panel.Children.Add(themeBtn);

        // Backdrop
        var backdrops = new[] { ("acrylic", "Acrylic"), ("mica", "Mica"), ("mica_alt", "MicaAlt"), ("acrylic_custom", Lang.T("backdrop_acrylic_custom")), ("none", Lang.T("backdrop_none")) };
        var backdropBtn = CreateCascadeButton(Lang.T("backdrop"), FindLabel(backdrops, currentBackdropType),
            CreateRadioSubMenu("Backdrop", backdrops, currentBackdropType, k => { onBackdropSelected(k); flyout.Hide(); }));
        allButtons.Add(backdropBtn);
        panel.Children.Add(backdropBtn);

        // Note style
        if (onNoteStyleSelected != null)
        {
            var noteStyles = new[] { ("titlebar", Lang.T("note_style_titlebar")), ("full", Lang.T("note_style_full")) };
            var noteStyle = currentNoteStyle ?? "titlebar";
            var noteBtn = CreateCascadeButton(Lang.T("note_section"), FindLabel(noteStyles, noteStyle),
                CreateRadioSubMenu("NoteStyle", noteStyles, noteStyle, k => { onNoteStyleSelected(k); flyout.Hide(); }));
            noteBtn.Tag = Lang.T("note_section") + " Couleur Note";
            allButtons.Add(noteBtn);
            panel.Children.Add(noteBtn);
        }

        // Font
        if (onFontSelected != null)
        {
            var fonts = new[] { ("segoe", "Segoe UI"), ("geist", "Geist"), ("inter", "Inter"), ("jetbrains", "JetBrains Mono") };
            var font = currentFont ?? "geist";
            var fontBtn = CreateCascadeButton(Lang.T("font_section"), FindLabel(fonts, font),
                CreateRadioSubMenu("Font", fonts, font, k => { onFontSelected(k); flyout.Hide(); }));
            fontBtn.Tag = Lang.T("font_section") + " Police Font Geist Inter Segoe JetBrains";
            allButtons.Add(fontBtn);
            panel.Children.Add(fontBtn);
        }

        panel.Children.Add(CreateSeparator());

        // Storage
        var isCustomFolder = !string.Equals(currentNotesFolder, defaultNotesFolder, StringComparison.OrdinalIgnoreCase);
        var storageSubMenu = CreateStorageSubMenu(
            isCustomFolder, isFirebaseConnected, isWebDavConnected,
            currentNotesFolder, firebaseEmail, webDavUrl,
            onResetFolder, onChangeFolder,
            onConfigureFirebase, onDisconnectFirebase, onSyncFirebase,
            onConfigureWebDav, onDisconnectWebDav, onSyncWebDav,
            () => flyout.Hide());
        var storageValue = isFirebaseConnected ? Lang.T("cloud")
            : isWebDavConnected ? "WebDAV"
            : isCustomFolder ? Lang.T("custom_folder")
            : Lang.T("local");
        var storageBtn = CreateCascadeButton(Lang.T("storage"), storageValue, storageSubMenu);
        storageBtn.Tag = Lang.T("storage") + " Cloud Firebase WebDAV Local";
        allButtons.Add(storageBtn);
        panel.Children.Add(storageBtn);

        panel.Children.Add(CreateSeparator());

        // AI
        if (onShowAi != null)
        {
            var aiBtn = CreateNavigateButton(Lang.T("ai_label"), () => onShowAi(flyout));
            aiBtn.Tag = Lang.T("ai_label") + " IA AI OpenAI Claude Gemini GGUF";
            allButtons.Add(aiBtn);
            panel.Children.Add(aiBtn);

            if (onShowPrompts != null)
            {
                var promptsBtn = CreateNavigateButton(Lang.T("ai_prompts"), () => onShowPrompts(flyout));
                promptsBtn.Tag = Lang.T("ai_prompts") + " prompt IA AI";
                allButtons.Add(promptsBtn);
                panel.Children.Add(promptsBtn);
            }
        }

        // Voice
        if (onShowVoiceModels != null)
        {
            var voiceBtn = CreateNavigateButton(Lang.T("voice_model"), () => onShowVoiceModels(flyout));
            voiceBtn.Tag = Lang.T("voice_model") + " TTS STT";
            allButtons.Add(voiceBtn);
            panel.Children.Add(voiceBtn);
        }

        // Shortcuts
        if (onShowShortcuts != null)
        {
            var shortcutsBtn = CreateNavigateButton(Lang.T("shortcuts_label"), () => onShowShortcuts(flyout));
            shortcutsBtn.Tag = Lang.T("shortcuts_label") + " Raccourcis clavier";
            allButtons.Add(shortcutsBtn);
            panel.Children.Add(shortcutsBtn);
        }

        // Editor (slash toggle)
        if (onSlashToggled != null)
        {
            var slashBtn = CreateCheckItem(Lang.T("slash_commands_toggle"), slashEnabled, () =>
            {
                onSlashToggled(!slashEnabled);
                flyout.Hide();
            });
            slashBtn.Tag = Lang.T("slash_commands_toggle");
            allButtons.Add(slashBtn);
            panel.Children.Add(slashBtn);
        }

        panel.Children.Add(CreateSeparator());

        // Language
        if (onLanguageSelected != null)
        {
            var langs = new[] { ("en", Lang.T("language_en")), ("fr", Lang.T("language_fr")) };
            var langBtn = CreateCascadeButton(Lang.T("language_section"), FindLabel(langs, currentLanguage ?? "en"),
                CreateRadioSubMenu("Language", langs, currentLanguage ?? "en", c => { onLanguageSelected(c); flyout.Hide(); }));
            allButtons.Add(langBtn);
            panel.Children.Add(langBtn);
        }

        // Reset
        if (onResetPassword != null || onResetNotes != null)
        {
            panel.Children.Add(CreateSeparator());
            var resetBtn = CreateNavigateButton(Lang.T("reset"), () =>
                ShowResetSubPanel(flyout, onResetPassword, onResetNotes));
            resetBtn.Tag = Lang.T("reset") + " Réinitialiser Reset";
            // Red text
            if (resetBtn.Content is Grid rg)
                foreach (var child in rg.Children)
                    if (child is TextBlock rtb)
                        rtb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99));
            allButtons.Add(resetBtn);
            panel.Children.Add(resetBtn);
        }

        // Search
        panel.Children.Add(CreateSeparator());
        var searchBox = new TextBox
        {
            PlaceholderText = Lang.T("search"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        searchBox.TextChanged += (_, _) =>
        {
            var query = searchBox.Text;
            foreach (var btn in allButtons)
            {
                var tag = btn.Tag as string ?? "";
                btn.Visibility = string.IsNullOrEmpty(query) ||
                    tag.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };
        panel.Children.Add(searchBox);

        flyout.Content = panel;
        return flyout;
    }

    private static Flyout CreateRadioSubMenu(string groupName,
        (string key, string label)[] options, string currentKey, Action<string> onSelected)
    {
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = CreateFlyoutPresenterStyle(180, 260)
        };

        var panel = new StackPanel { Spacing = 0 };
        foreach (var (key, label) in options)
        {
            var k = key;
            var btn = CreateCheckItem(label, key == currentKey, () => onSelected(k));
            panel.Children.Add(btn);
        }
        flyout.Content = panel;
        return flyout;
    }

    private static Flyout CreateStorageSubMenu(
        bool isCustomFolder, bool isFirebaseConnected, bool isWebDavConnected,
        string currentNotesFolder, string? firebaseEmail, string? webDavUrl,
        Action onResetFolder, Action onChangeFolder,
        Action onConfigureFirebase, Action onDisconnectFirebase, Action onSyncFirebase,
        Action onConfigureWebDav, Action onDisconnectWebDav, Action onSyncWebDav,
        Action onDone)
    {
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = CreateFlyoutPresenterStyle(200, 300)
        };

        var panel = new StackPanel { Spacing = 0 };

        bool isLocal = !isFirebaseConnected && !isWebDavConnected && !isCustomFolder;
        panel.Children.Add(CreateCheckItem(Lang.T("local"), isLocal, () =>
        {
            if (isCustomFolder) onResetFolder(); else if (isFirebaseConnected) onDisconnectFirebase();
            onDone();
        }));

        var folderLabel = isCustomFolder ? $"{Lang.T("custom_folder")} — {ShortenPath(currentNotesFolder)}" : Lang.T("custom_folder");
        panel.Children.Add(CreateCheckItem(folderLabel, isCustomFolder && !isFirebaseConnected, () =>
        {
            onChangeFolder(); onDone();
        }));

        panel.Children.Add(CreateSeparator());

        if (isFirebaseConnected)
        {
            var cloudLabel = !string.IsNullOrEmpty(firebaseEmail) ? $"{Lang.T("cloud")} — {firebaseEmail}" : Lang.T("cloud");
            panel.Children.Add(CreateCheckItem(cloudLabel, true, () => { onSyncFirebase(); onDone(); }));
            panel.Children.Add(CreateActionItem(Lang.T("disconnect"), () => { onDisconnectFirebase(); onDone(); }));
        }
        else
        {
            panel.Children.Add(CreateCheckItem(Lang.T("cloud"), false, () => { onConfigureFirebase(); onDone(); }));
        }

        if (isWebDavConnected)
        {
            var wdLabel = !string.IsNullOrEmpty(webDavUrl) ? $"WebDAV — {ShortenPath(webDavUrl)}" : "WebDAV";
            panel.Children.Add(CreateCheckItem(wdLabel, true, () => { onSyncWebDav(); onDone(); }));
            panel.Children.Add(CreateActionItem(Lang.T("disconnect"), () => { onDisconnectWebDav(); onDone(); }));
        }
        else
        {
            panel.Children.Add(CreateCheckItem("WebDAV", false, () => { onConfigureWebDav(); onDone(); }));
        }

        flyout.Content = panel;
        return flyout;
    }

    private static Button CreateActionItem(string label, Action handler)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0
        };
        btn.Content = new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 };
        btn.Click += (_, _) => handler();
        return btn;
    }

    private static Button CreateCascadeButton(string label, string currentValue, Flyout subMenu)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Flyout = subMenu,
            Tag = label + " " + currentValue
        };

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (!string.IsNullOrEmpty(currentValue))
        {
            var value = new TextBlock { Text = currentValue, FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 9, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        btn.Content = grid;
        return btn;
    }

    private static Button CreateNavigateButton(string label, Action handler)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Tag = label
        };

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 9, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        btn.Content = grid;
        btn.Click += (_, _) => handler();
        return btn;
    }

    private static Button CreateCheckItem(string label, bool isSelected, Action handler)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Tag = label
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (isSelected)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 12,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        btn.Content = grid;
        btn.Click += (_, _) => handler();
        return btn;
    }

    public static TextBlock CreateHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(8, 4, 8, 3),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    public static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Opacity = 0.15,
            Background = new SolidColorBrush(
                ThemeHelper.IsDark() ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black),
            Margin = new Thickness(6, 1, 6, 1)
        };
    }

    internal static Button CreateButton(ActionItem action, Action handler)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Tag = action.Label
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush? foreground = action.IsDestructive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99))
            : null; // inherit themed default

        FrameworkElement iconEl;
        if (action.Icon != null)
        {
            iconEl = action.Icon;
        }
        else
        {
            var fi = new FontIcon { Glyph = action.Glyph ?? "", FontSize = 12 };
            if (foreground != null) fi.Foreground = foreground;
            iconEl = fi;
        }
        Grid.SetColumn(iconEl, 0);

        var text = new TextBlock
        {
            Text = action.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (foreground != null) text.Foreground = foreground;
        Grid.SetColumn(text, 1);

        var keysPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var key in action.Keys)
        {
            keysPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(
                    ThemeHelper.IsDark()
                        ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
                        : Windows.UI.Color.FromArgb(0x09, 0x00, 0x00, 0x00)),
                Child = new TextBlock
                {
                    Text = key,
                    FontSize = 10,
                    Opacity = 0.6
                }
            });
        }
        Grid.SetColumn(keysPanel, 2);

        grid.Children.Add(iconEl);
        grid.Children.Add(text);
        grid.Children.Add(keysPanel);

        btn.Content = grid;
        btn.Click += (_, _) => handler();
        return btn;
    }

    // ── Reset sub-panel ──

    private static void ShowResetSubPanel(Flyout flyout, Action? onResetPassword, Action? onResetNotes)
    {
        var panel = CreateSubPanelWithHeader(Lang.T("reset"), () =>
        {
            // Back: re-show would require recreating settings; just hide
            flyout.Hide();
        });

        if (onResetPassword != null)
        {
            var pwBtn = CreateNavigateButton(Lang.T("reset_password"), () =>
                ShowResetConfirmPanel(flyout, Lang.T("reset_password"), Lang.T("reset_password_warn"), () =>
                {
                    flyout.Hide();
                    onResetPassword();
                }, onResetPassword, onResetNotes, requirePassword: true));
            panel.Children.Add(pwBtn);
        }

        if (onResetNotes != null)
        {
            var notesBtn = CreateNavigateButton(Lang.T("reset_notes"), () =>
                ShowResetConfirmPanel(flyout, Lang.T("reset_notes"), Lang.T("reset_notes_warn"), () =>
                {
                    flyout.Hide();
                    onResetNotes();
                }, onResetPassword, onResetNotes));
            panel.Children.Add(notesBtn);
        }

        flyout.Content = panel;
    }

    private static void ShowResetConfirmPanel(Flyout flyout, string title, string warning, Action onConfirm,
        Action? onResetPassword = null, Action? onResetNotes = null, bool requirePassword = false)
    {
        var panel = CreateSubPanelWithHeader(title, () =>
            ShowResetSubPanel(flyout, onResetPassword, onResetNotes));

        var warningText = new TextBlock
        {
            Text = warning,
            FontSize = 12,
            MaxWidth = 250,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Margin = new Thickness(8, 6, 8, 6),
            Opacity = 0.9,
        };
        panel.Children.Add(warningText);

        PasswordBox? passwordBox = null;
        TextBlock? errorText = null;

        if (requirePassword)
        {
            passwordBox = new PasswordBox
            {
                PlaceholderText = Lang.T("master_password"),
                FontSize = 12,
                Margin = new Thickness(6, 4, 6, 0),
            };
            panel.Children.Add(passwordBox);

            errorText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
                Margin = new Thickness(8, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(errorText);
        }

        panel.Children.Add(CreateSeparator());

        var confirmBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            MinHeight = 0,
            Content = new TextBlock
            {
                Text = Lang.T("reset_confirm"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            }
        };
        confirmBtn.Click += (_, _) =>
        {
            if (requirePassword && passwordBox != null && errorText != null)
            {
                if (string.IsNullOrEmpty(passwordBox.Password))
                {
                    errorText.Text = Lang.T("password_required");
                    errorText.Visibility = Visibility.Visible;
                    return;
                }
                var storedHash = AppSettings.LoadMasterPasswordHash();
                if (storedHash != null && AppSettings.HashPassword(passwordBox.Password) != storedHash)
                {
                    errorText.Text = Lang.T("wrong_password");
                    errorText.Visibility = Visibility.Visible;
                    return;
                }
            }
            onConfirm();
        };
        panel.Children.Add(confirmBtn);

        flyout.Content = panel;
    }

    // ── Lock / Password flyouts ──

    public static void ShowCreatePasswordFlyout(FrameworkElement target, Action<string> onPasswordCreated)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle();

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(CreateHeader(Lang.T("create_password")));
        panel.Children.Add(CreateSeparator());

        var passwordBox = new PasswordBox
        {
            PlaceholderText = Lang.T("master_password"),
            FontSize = 12,
            Margin = new Thickness(6, 4, 6, 0),
        };
        panel.Children.Add(passwordBox);

        var confirmBox = new PasswordBox
        {
            PlaceholderText = Lang.T("confirm_password"),
            FontSize = 12,
            Margin = new Thickness(6, 0, 6, 0),
        };
        panel.Children.Add(confirmBox);

        var errorText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(errorText);

        panel.Children.Add(CreateSeparator());

        var confirmBtn = CreateCheckItem("OK", false, () =>
        {
            if (string.IsNullOrEmpty(passwordBox.Password))
            {
                errorText.Text = Lang.T("password_required");
                errorText.Visibility = Visibility.Visible;
                return;
            }
            if (passwordBox.Password != confirmBox.Password)
            {
                errorText.Text = Lang.T("passwords_dont_match");
                errorText.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            onPasswordCreated(passwordBox.Password);
        });
        panel.Children.Add(confirmBtn);

        flyout.Content = panel;
        flyout.ShowAt(target);
    }

    public static void ShowEnterPasswordFlyout(FrameworkElement target, string storedHash, Action onSuccess)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle();

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(CreateHeader(Lang.T("enter_password")));
        panel.Children.Add(CreateSeparator());

        var passwordBox = new PasswordBox
        {
            PlaceholderText = Lang.T("master_password"),
            FontSize = 12,
            Margin = new Thickness(6, 4, 6, 0),
        };
        panel.Children.Add(passwordBox);

        var errorText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(errorText);

        panel.Children.Add(CreateSeparator());

        var confirmBtn = CreateCheckItem("OK", false, () =>
        {
            if (string.IsNullOrEmpty(passwordBox.Password))
            {
                errorText.Text = Lang.T("password_required");
                errorText.Visibility = Visibility.Visible;
                return;
            }
            var inputHash = AppSettings.HashPassword(passwordBox.Password);
            if (inputHash != storedHash)
            {
                errorText.Text = Lang.T("wrong_password");
                errorText.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            onSuccess();
        });
        panel.Children.Add(confirmBtn);

        flyout.Content = panel;
        flyout.ShowAt(target);
    }

    public static void ShowVoiceModelsPanel(Flyout flyout, string? currentModelId,
        XamlRoot xamlRoot,
        Action<SttModelInfo> onSelectModel, Action<SttModelInfo> onDeleteModel, Action onBack, Action onRebuild,
        AiManager? aiManager = null)
    {
        var panel = CreateSubPanelWithHeader(Lang.T("voice_model"), onBack);

        // French models
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("french"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 0, 4)
        });
        foreach (var model in SttModels.Available.Where(m => m.Languages == "Fran\u00e7ais"))
            panel.Children.Add(CreateModelItem(model, currentModelId, flyout, xamlRoot, onSelectModel, onDeleteModel, onRebuild));

        // English models
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("english"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 0, 4)
        });
        foreach (var model in SttModels.Available.Where(m => m.Languages == "Anglais"))
            panel.Children.Add(CreateModelItem(model, currentModelId, flyout, xamlRoot, onSelectModel, onDeleteModel, onRebuild));

        // Groq Cloud section
        if (aiManager != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("groq_cloud_section"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.6,
                Margin = new Thickness(10, 8, 0, 4)
            });

            var hasKey = aiManager.HasApiKey("groq");

            // API key input
            var keyBlock = new StackPanel { Margin = new Thickness(10, 4, 10, 4), Spacing = 3 };

            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = Lang.T("groq_api_key"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText, 0);

            var statusIcon = new FontIcon
            {
                Glyph = hasKey ? "\uE73E" : "\uE785",
                FontSize = 12,
                Foreground = hasKey
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))
                    : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(statusIcon, 1);
            nameRow.Children.Add(nameText);
            nameRow.Children.Add(statusIcon);
            keyBlock.Children.Add(nameRow);

            var keyRow = new Grid();
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyBox = new PasswordBox
            {
                PlaceholderText = Lang.T("groq_api_key_placeholder"),
                Password = aiManager.GetApiKey("groq"),
                FontSize = 11,
                MaxWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(keyBox, 0);

            var saveKeyBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE73E", FontSize = 12 },
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(saveKeyBtn, 1);

            saveKeyBtn.Click += (_, _) =>
            {
                aiManager.SetApiKey("groq", keyBox.Password);
                var nowHasKey = aiManager.HasApiKey("groq");
                statusIcon.Glyph = nowHasKey ? "\uE73E" : "\uE785";
                statusIcon.Foreground = nowHasKey
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))
                    : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                onRebuild();
            };

            keyRow.Children.Add(keyBox);
            keyRow.Children.Add(saveKeyBtn);
            keyBlock.Children.Add(keyRow);
            panel.Children.Add(keyBlock);

            // Groq cloud models (only show if key exists)
            if (hasKey)
            {
                foreach (var model in SttModels.Available.Where(m => m.Engine == SttEngine.GroqCloud))
                    panel.Children.Add(CreateGroqModelItem(model, currentModelId, onSelectModel, flyout));
            }
        }

        flyout.Content = panel;
    }

    private static UIElement CreateGroqModelItem(SttModelInfo model, string? currentModelId,
        Action<SttModelInfo> onSelect, Flyout parentFlyout)
    {
        var isCurrent = model.Id == currentModelId;

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(5),
            Tag = model.Name
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel { Spacing = 1 };
        textPanel.Children.Add(new TextBlock
        {
            Text = model.Name,
            FontSize = 13,
            FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = isCurrent ? $"Groq — {Lang.T("model_active")}" : $"Groq — {Lang.T("groq_cloud_model")}",
            FontSize = 11,
            Opacity = isCurrent ? 0.85 : 0.45
        });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        var icon = new FontIcon
        {
            Glyph = isCurrent ? "\uE73E" : "\uE753", // checkmark or cloud
            FontSize = isCurrent ? 14 : 12,
            Foreground = isCurrent
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))
                : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        btn.Content = grid;
        btn.Click += (_, _) =>
        {
            onSelect(model);
            parentFlyout.Hide();
        };

        return btn;
    }

    private static UIElement CreateModelItem(SttModelInfo model, string? currentModelId,
        Flyout parentFlyout, XamlRoot xamlRoot, Action<SttModelInfo> onSelect, Action<SttModelInfo> onDelete, Action onRebuild)
    {
        var isCurrent = model.Id == currentModelId;
        var isDownloaded = model.IsDownloaded;
        var engine = model.Engine == SttEngine.Vosk ? "Vosk" : "Whisper";
        var status = isCurrent ? Lang.T("model_active") : isDownloaded ? Lang.T("model_downloaded") : $"{model.SizeMB} MB";

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(5),
            Tag = model.Name
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel { Spacing = 1 };
        textPanel.Children.Add(new TextBlock
        {
            Text = model.Name,
            FontSize = 13,
            FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"{engine} \u2014 {status}",
            FontSize = 11,
            Opacity = isCurrent ? 0.85 : 0.45
        });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        if (isDownloaded)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 14,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }
        else
        {
            var downloadIcon = new FontIcon
            {
                Glyph = "\uE896",
                FontSize = 12,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(downloadIcon, 1);
            grid.Children.Add(downloadIcon);
        }

        btn.Content = grid;

        if (isDownloaded)
        {
            btn.Click += (_, _) =>
            {
                onSelect(model);
                parentFlyout.Hide();
            };

            // Right-click context menu using ContextFlyout (doesn't close parent flyout)
            var menuFlyout = new MenuFlyout();
            var deleteMenuItem = new MenuFlyoutItem
            {
                Text = Lang.T("delete"),
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteMenuItem.Click += (_, _) => onDelete(model);
            menuFlyout.Items.Add(deleteMenuItem);
            btn.ContextFlyout = menuFlyout;
        }
        else
        {
            btn.Click += async (s, _) =>
            {
                parentFlyout.Hide();
                using var cts = new CancellationTokenSource();

                var progressBar = new Microsoft.UI.Xaml.Controls.ProgressBar
                {
                    Minimum = 0, Maximum = 100, Value = 0
                };
                var statusText = new TextBlock
                {
                    Text = Lang.T("downloading", model.Name),
                    FontSize = 12
                };
                var sizeText = new TextBlock
                {
                    Text = $"{model.SizeMB} MB",
                    FontSize = 11,
                    Opacity = 0.45
                };

                var contentPanel = new StackPanel { Spacing = 8 };
                contentPanel.Children.Add(statusText);
                contentPanel.Children.Add(progressBar);
                contentPanel.Children.Add(sizeText);

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = Lang.T("download"),
                    Content = contentPanel,
                    CloseButtonText = Lang.T("cancel")
                };

                var progress = new Progress<double>(p =>
                {
                    dialog.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (p < 0)
                        {
                            progressBar.IsIndeterminate = true;
                            statusText.Text = "Extraction...";
                        }
                        else
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = p * 100;
                            statusText.Text = $"T\u00e9l\u00e9chargement... {p * 100:F0}%";
                        }
                    });
                });

                bool success = false;
                string? errorMsg = null;

                var downloadTask = Task.Run(async () =>
                {
                    try
                    {
                        await ModelDownloader.DownloadAsync(model, progress, cts.Token);
                        success = true;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { errorMsg = ex.Message; }
                    dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide());
                });

                await dialog.ShowAsync();
                if (!success) cts.Cancel();
                try { await downloadTask; } catch { }

                if (success)
                {
                    onSelect(model);
                }
                else if (errorMsg != null)
                {
                    var errDlg = new ContentDialog
                    {
                        XamlRoot = xamlRoot,
                        Title = Lang.T("error"),
                        Content = errorMsg,
                        CloseButtonText = Lang.T("ok")
                    };
                    await errDlg.ShowAsync();
                }
            };
        }

        return btn;
    }

    private static string ShortenPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            return "~" + path[userProfile.Length..];
        return path.Length > 35 ? "..." + path[^32..] : path;
    }

    internal static Flyout ShowSnippetFlyout(FrameworkElement target, string noteId,
        SnippetManager snippetManager, string noteContent)
    {
        var existing = snippetManager.FindByNoteId(noteId);

        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle(280, 340);

        var panel = new StackPanel { Spacing = 0 };

        // Header
        panel.Children.Add(CreateHeader(Lang.T("snippet")));
        panel.Children.Add(CreateSeparator());

        var form = new StackPanel { Spacing = 12, Padding = new Thickness(8) };

        // Keyword
        var keywordBox = new TextBox
        {
            Header = Lang.T("snippet_keyword"),
            PlaceholderText = Lang.T("snippet_keyword_placeholder"),
            FontSize = 13,
            Text = existing?.Keyword ?? ""
        };
        form.Children.Add(keywordBox);

        // Prefix
        var prefixCombo = new ComboBox
        {
            Header = Lang.T("snippet_prefix"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var prefixes = new[] { Lang.T("snippet_none"), ";", "!", "<", "+" };
        foreach (var p in prefixes)
            prefixCombo.Items.Add(p);

        // Select current prefix
        if (existing != null && !string.IsNullOrEmpty(existing.Prefix))
        {
            var idx = Array.IndexOf(prefixes, existing.Prefix);
            prefixCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else
        {
            prefixCombo.SelectedIndex = 0;
        }
        form.Children.Add(prefixCombo);

        // Preview of trigger
        var previewText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.45,
            Margin = new Thickness(0, -4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        form.Children.Add(previewText);

        void UpdatePreview()
        {
            var kw = keywordBox.Text.Trim();
            if (string.IsNullOrEmpty(kw))
            {
                previewText.Text = "";
                return;
            }
            var prefix = prefixCombo.SelectedIndex > 0 ? prefixes[prefixCombo.SelectedIndex] : "";
            previewText.Text = Lang.T("snippet_preview", prefix + kw);
        }

        keywordBox.TextChanged += (_, _) => UpdatePreview();
        prefixCombo.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        // Buttons row
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var saveBtn = new Button
        {
            Content = Lang.T("save"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        saveBtn.Click += (_, _) =>
        {
            var kw = keywordBox.Text.Trim();
            if (string.IsNullOrEmpty(kw)) return;
            var prefix = prefixCombo.SelectedIndex > 0 ? prefixes[prefixCombo.SelectedIndex] : "";

            // Strip RTF if needed to get plain text for expansion
            var plainContent = noteContent;
            if (plainContent.StartsWith("{\\rtf", StringComparison.Ordinal))
                plainContent = StripRtfToPlain(plainContent);

            snippetManager.AddSnippet(noteId, kw, prefix, plainContent);
            flyout.Hide();
        };
        buttonsPanel.Children.Add(saveBtn);

        if (existing != null)
        {
            var removeBtn = new Button { Content = Lang.T("delete") };
            removeBtn.Click += (_, _) =>
            {
                snippetManager.RemoveSnippet(noteId);
                flyout.Hide();
            };
            buttonsPanel.Children.Add(removeBtn);
        }

        form.Children.Add(buttonsPanel);
        panel.Children.Add(form);

        flyout.Content = panel;
        flyout.ShowAt(target);
        return flyout;
    }

    private static string StripRtfToPlain(string rtf)
    {
        var result = new System.Text.StringBuilder();
        int depth = 0;
        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { depth--; i++; continue; }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                if (rtf[i] == '\'')
                {
                    if (i + 2 < rtf.Length &&
                        byte.TryParse(rtf.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    {
                        result.Append((char)b);
                        i += 3;
                    }
                    else i++;
                }
                else if (rtf[i] == '\n' || rtf[i] == '\r') { i++; }
                else
                {
                    var word = new System.Text.StringBuilder();
                    while (i < rtf.Length && char.IsLetter(rtf[i])) { word.Append(rtf[i]); i++; }
                    var w = word.ToString();
                    if (w == "par" || w == "line") result.Append('\n');
                    if (w == "tab") result.Append(' ');
                    if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    {
                        if (rtf[i] == '-') i++;
                        while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                    }
                    if (i < rtf.Length && rtf[i] == ' ') i++;
                }
                continue;
            }
            if (depth <= 1 && (c >= ' ' || c == '\n' || c == '\t'))
                result.Append(c);
            i++;
        }
        return result.ToString().Trim();
    }

    internal static void ShowShortcutsPanel(Flyout flyout, List<HotkeyService.ShortcutEntry> shortcuts,
        Action<List<HotkeyService.ShortcutEntry>> onSave, Action onBack)
    {
        var panel = CreateSubPanelWithHeader(Lang.T("shortcuts_label"), onBack);

        var edited = shortcuts.ToList();

        for (int i = 0; i < edited.Count; i++)
        {
            var idx = i;
            var entry = edited[i];

            var row = new StackPanel { Spacing = 4, Margin = new Thickness(10, 8, 10, 8) };

            var displayLabel = entry.Name switch
            {
                "show" => Lang.T("shortcut_show"),
                "new_note" => Lang.T("shortcut_new_note"),
                "flyout_back" => Lang.T("shortcut_flyout_back"),
                _ => entry.DisplayLabel
            };
            row.Children.Add(new TextBlock
            {
                Text = displayLabel,
                FontSize = 12
            });

            var keyBox = new TextBox
            {
                Text = entry.KeyDisplay,
                IsReadOnly = true,
                FontSize = 12,
                Padding = new Thickness(8, 5, 8, 5),
                PlaceholderText = "Appuyez sur les touches...",
                TextAlignment = TextAlignment.Center,
            };

            keyBox.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var (mods, vk) = HotkeyService.ParseKeyEvent(e.Key);
                if (vk == 0) // modifier-only, show partial
                {
                    keyBox.Text = HotkeyService.FormatShortcut(mods, 0);
                    if (!string.IsNullOrEmpty(keyBox.Text))
                        keyBox.Text += " + ...";
                    return;
                }
                keyBox.Text = HotkeyService.FormatShortcut(mods, vk);
                edited[idx] = entry with { Modifiers = mods, VirtualKey = vk };
                onSave(edited);
            };

            row.Children.Add(keyBox);
            panel.Children.Add(row);

            if (i < edited.Count - 1)
                panel.Children.Add(CreateSeparator());
        }

        flyout.Content = panel;
    }

    // ── AI Panel ──────────────────────────────────────────────

    internal static void ShowAiPanel(Flyout flyout, AiManager aiManager, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action onBack, Action? onAiStateChanged = null)
    {
        var panel = CreateSubPanelWithHeader(Lang.T("ai_section"), onBack);

        // ── Cloud providers section ──
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("ai_providers"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 0, 4)
        });

        foreach (var provider in AiManager.Providers)
        {
            var hasKey = aiManager.HasApiKey(provider.Id);

            var providerBlock = new StackPanel { Margin = new Thickness(10, 4, 10, 4), Spacing = 3 };

            // Provider name + status icon on the same line
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = provider.Name,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText, 0);

            var statusIcon = new FontIcon
            {
                Glyph = hasKey ? "\uE73E" : "\uE785",
                FontSize = 12,
                Foreground = hasKey
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))
                    : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(statusIcon, 1);
            nameRow.Children.Add(nameText);
            nameRow.Children.Add(statusIcon);
            providerBlock.Children.Add(nameRow);

            // Key input + save button
            var keyRow = new Grid();
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyBox = new PasswordBox
            {
                PlaceholderText = Lang.T("ai_api_key_placeholder"),
                Password = aiManager.GetApiKey(provider.Id),
                FontSize = 11,
                MaxWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(keyBox, 0);

            var saveKeyBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE73E", FontSize = 12 },
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(saveKeyBtn, 1);

            var p = provider;
            saveKeyBtn.Click += (_, _) =>
            {
                aiManager.SetApiKey(p.Id, keyBox.Password);
                var nowHasKey = aiManager.HasApiKey(p.Id);
                statusIcon.Glyph = nowHasKey ? "\uE73E" : "\uE785";
                statusIcon.Foreground = nowHasKey
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95))
                    : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

                if (nowHasKey)
                {
                    ShowAiModelsPanel(flyout, aiManager, p, xamlRoot, () =>
                        ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged), onAiStateChanged);
                }
            };

            keyRow.Children.Add(keyBox);
            keyRow.Children.Add(saveKeyBtn);
            providerBlock.Children.Add(keyRow);
            panel.Children.Add(providerBlock);

            // If key already exists, add a button to browse models
            if (hasKey)
            {
                var browseBtn = CreateCheckItem(Lang.T("ai_select_model"), false, () =>
                    ShowAiModelsPanel(flyout, aiManager, p, xamlRoot, () =>
                        ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged), onAiStateChanged));
                browseBtn.Tag = provider.Name + " " + Lang.T("ai_select_model");
                panel.Children.Add(browseBtn);
            }
        }

        panel.Children.Add(CreateSeparator());

        // ── Local models section ──
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("ai_models_local"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 0, 4)
        });

        // All predefined models (installed + available) in one list
        foreach (var model in AiManager.PredefinedModels)
        {
            var m = model;
            var isInstalled = model.IsInstalled;
            var isActive = isInstalled
                && string.IsNullOrEmpty(aiManager.Settings.LastProviderId)
                && aiManager.Settings.LastLocalModelFileName == model.FileName
                && aiManager.Settings.IsEnabled;
            var modelBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(5),
            };
            var modelGrid = new Grid { ColumnSpacing = 8 };
            modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var modelTextPanel = new StackPanel { Spacing = 1 };
            modelTextPanel.Children.Add(new TextBlock
            {
                Text = model.Name,
                FontSize = 12,
                FontWeight = isActive
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            });
            if (isActive)
            {
                modelTextPanel.Children.Add(new TextBlock
                {
                    Text = Lang.T("model_active"),
                    FontSize = 11,
                    Opacity = 0.85,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95)),
                });
            }
            Grid.SetColumn(modelTextPanel, 0);
            var modelSize = new TextBlock
            {
                Text = model.Size,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(modelSize, 1);
            modelGrid.Children.Add(modelTextPanel);
            modelGrid.Children.Add(modelSize);

            if (isInstalled)
            {
                var check = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(check, 2);
                modelGrid.Children.Add(check);
            }
            else
            {
                var downloadIcon = new FontIcon
                {
                    Glyph = "\uE896",
                    FontSize = 12,
                    Opacity = 0.45,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(downloadIcon, 2);
                modelGrid.Children.Add(downloadIcon);
            }

            modelBtn.Content = modelGrid;

            var fn = model.FileName;
            if (isInstalled)
            {
                // Left click: select model
                modelBtn.Click += (_, _) =>
                {
                    aiManager.Settings.IsEnabled = true;
                    aiManager.Settings.LastLocalModelFileName = fn;
                    aiManager.Settings.LastProviderId = "";
                    aiManager.Settings.LastModelId = "";
                    aiManager.SaveSettings();
                    onAiStateChanged?.Invoke();
                    flyout.Hide();
                };

                // Right click: delete option
                var deleteFlyout = new MenuFlyout();
                var deleteItem = new MenuFlyoutItem
                {
                    Text = Lang.T("delete"),
                    Icon = new FontIcon { Glyph = "\uE74D" },
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
                };
                deleteItem.Click += (_, _) =>
                {
                    aiManager.DeleteModel(fn);
                    onAiStateChanged?.Invoke();
                    // Refresh the panel without closing the flyout
                    ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged);
                };
                deleteFlyout.Items.Add(deleteItem);
                modelBtn.ContextFlyout = deleteFlyout;
            }
            else
            {
                // Left click: download model
                modelBtn.Click += async (_, _) =>
                {
                    await DownloadLocalModelWithDialog(aiManager, m, xamlRoot, onAiStateChanged);
                    // Refresh the panel without closing the flyout
                    ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged);
                };

                // Right click: download option
                var downloadFlyout = new MenuFlyout();
                var downloadItem = new MenuFlyoutItem
                {
                    Text = Lang.T("ai_download"),
                    Icon = new FontIcon { Glyph = "\uE896" },
                };
                downloadItem.Click += async (_, _) =>
                {
                    await DownloadLocalModelWithDialog(aiManager, m, xamlRoot, onAiStateChanged);
                    // Refresh the panel without closing the flyout
                    ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged);
                };
                downloadFlyout.Items.Add(downloadItem);
                modelBtn.ContextFlyout = downloadFlyout;
            }

            panel.Children.Add(modelBtn);
        }

        // Custom installed models (non-predefined)
        var customInstalled = aiManager.GetInstalledModels()
            .Where(m => !AiManager.PredefinedModels.Any(p => p.FileName == m.FileName));
        foreach (var model in customInstalled)
        {
            var isActive = string.IsNullOrEmpty(aiManager.Settings.LastProviderId)
                && aiManager.Settings.LastLocalModelFileName == model.FileName
                && aiManager.Settings.IsEnabled;
            var modelBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(5),
            };
            var modelGrid = new Grid { ColumnSpacing = 8 };
            modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var modelTextPanel = new StackPanel { Spacing = 1 };
            modelTextPanel.Children.Add(new TextBlock
            {
                Text = model.Name,
                FontSize = 12,
                FontWeight = isActive
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            });
            if (isActive)
            {
                modelTextPanel.Children.Add(new TextBlock
                {
                    Text = Lang.T("model_active"),
                    FontSize = 11,
                    Opacity = 0.85,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95)),
                });
            }
            Grid.SetColumn(modelTextPanel, 0);
            var modelSize = new TextBlock
            {
                Text = model.Size,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(modelSize, 1);
            modelGrid.Children.Add(modelTextPanel);
            modelGrid.Children.Add(modelSize);
            modelBtn.Content = modelGrid;

            var fn = model.FileName;
            modelBtn.Click += (_, _) =>
            {
                aiManager.Settings.IsEnabled = true;
                aiManager.Settings.LastLocalModelFileName = fn;
                aiManager.Settings.LastProviderId = "";
                aiManager.Settings.LastModelId = "";
                aiManager.SaveSettings();
                onAiStateChanged?.Invoke();
                flyout.Hide();
            };

            // Right click: delete
            var deleteFlyout = new MenuFlyout();
            var deleteItem = new MenuFlyoutItem
            {
                Text = Lang.T("delete"),
                Icon = new FontIcon { Glyph = "\uE74D" },
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            };
            deleteItem.Click += (_, _) =>
            {
                aiManager.DeleteModel(fn);
                onAiStateChanged?.Invoke();
                ShowAiPanel(flyout, aiManager, xamlRoot, onBack, onAiStateChanged);
            };
            deleteFlyout.Items.Add(deleteItem);
            modelBtn.ContextFlyout = deleteFlyout;

            panel.Children.Add(modelBtn);
        }

        panel.Children.Add(CreateSeparator());

        // ── Custom GGUF URL ──
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("ai_custom_gguf"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 0, 4)
        });

        var urlBox = new TextBox
        {
            PlaceholderText = Lang.T("ai_custom_url"),
            FontSize = 12,
            Margin = new Thickness(10, 0, 10, 4),
            MaxWidth = 230,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(urlBox);

        var downloadBtn = CreateCheckItem(Lang.T("ai_download"), false, async () =>
        {
            var url = urlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url)) return;

            Uri uri;
            try { uri = new Uri(url); } catch { return; }

            var fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                return;

            flyout.Hide();
            await DownloadCustomModelWithDialog(aiManager, url, fileName, xamlRoot, onAiStateChanged);
        });
        downloadBtn.Tag = Lang.T("ai_download") + " GGUF custom";
        panel.Children.Add(downloadBtn);

        panel.Children.Add(CreateSeparator());

        var disableBtn = CreateButton(
            new ActionItem("\uE74D", Lang.T("ai_disable_all"), [], () => { }, IsDestructive: true),
            () =>
            {
                aiManager.DisableAll();
                onAiStateChanged?.Invoke();
                flyout.Hide();
            });
        disableBtn.Tag = Lang.T("ai_disable_all");
        panel.Children.Add(disableBtn);

        flyout.Content = panel;
    }

    // ── AI Models sub-panel (cloud provider models) ──

    private static async void ShowAiModelsPanel(Flyout flyout, AiManager aiManager,
        ICloudAiProvider provider, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action onBack, Action? onAiStateChanged = null)
    {
        var panel = CreateSubPanelWithHeader(provider.Name, onBack);

        // Loading indicator
        var loadingText = new TextBlock
        {
            Text = Lang.T("ai_loading_models"),
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(10, 8, 10, 8)
        };
        panel.Children.Add(loadingText);
        flyout.Content = panel;

        // Fetch models
        try
        {
            var apiKey = aiManager.GetApiKey(provider.Id);
            var models = await provider.ListModelsAsync(apiKey);

            panel.Children.Remove(loadingText);

            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("ai_select_model"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.6,
                Margin = new Thickness(10, 4, 0, 4)
            });

            foreach (var model in models)
            {
                var isSelected = model.Id == aiManager.Settings.LastModelId
                    && provider.Id == aiManager.Settings.LastProviderId;
                var modelBtn = CreateCheckItem(model.Name, isSelected, () =>
                {
                    aiManager.Settings.IsEnabled = true;
                    aiManager.Settings.LastProviderId = provider.Id;
                    aiManager.Settings.LastModelId = model.Id;
                    aiManager.Settings.LastLocalModelFileName = "";
                    aiManager.SaveSettings();
                    onAiStateChanged?.Invoke();
                    flyout.Hide();
                });
                modelBtn.Tag = model.Name;
                panel.Children.Add(modelBtn);
            }
        }
        catch
        {
            loadingText.Text = Lang.T("ai_error_loading");
        }
    }

    // ── Keyboard back helper ──

    private static void AddBackKeyNavigation(StackPanel panel, Action onBack)
    {
        var shortcut = HotkeyService.LoadFlyoutBack();
        var mods = Windows.System.VirtualKeyModifiers.None;
        if (shortcut.Modifiers.HasFlag(HotkeyService.Modifiers.Ctrl))
            mods |= Windows.System.VirtualKeyModifiers.Control;
        if (shortcut.Modifiers.HasFlag(HotkeyService.Modifiers.Alt))
            mods |= Windows.System.VirtualKeyModifiers.Menu;
        if (shortcut.Modifiers.HasFlag(HotkeyService.Modifiers.Shift))
            mods |= Windows.System.VirtualKeyModifiers.Shift;

        var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)shortcut.VirtualKey,
            Modifiers = mods,
        };
        acc.Invoked += (_, args) => { args.Handled = true; onBack(); };
        panel.KeyboardAccelerators.Add(acc);
    }

    private static StackPanel CreateSubPanelWithHeader(string title, Action onBack)
    {
        var panel = new StackPanel { Spacing = 0 };
        AddBackKeyNavigation(panel, onBack);

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var backBtn = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE72B", FontSize = 12 }
        };
        backBtn.Click += (_, _) => onBack();
        Grid.SetColumn(backBtn, 0);

        var header = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(header, 1);

        headerRow.Children.Add(backBtn);
        headerRow.Children.Add(header);
        panel.Children.Add(headerRow);
        panel.Children.Add(CreateSeparator());

        return panel;
    }

    // ── Prompts sub-panel ──

    internal static void ShowPromptsPanel(Flyout flyout, AiManager aiManager, Action onBack)
    {
        var panel = CreateSubPanelWithHeader(Lang.T("ai_prompts"), onBack);

        // Built-in prompts
        foreach (var (key, _) in AiManager.PromptDefinitions)
        {
            var k = key;
            var btn = CreateCheckItem(Lang.T(key), false, () =>
                ShowPromptEditPanel(flyout, aiManager, k, () =>
                    ShowPromptsPanel(flyout, aiManager, onBack)));
            btn.Tag = Lang.T(key);
            panel.Children.Add(btn);
        }

        // Custom prompts
        foreach (var cp in aiManager.Settings.CustomPrompts)
        {
            var prompt = cp;
            var btn = CreateCheckItem(prompt.Title, false, () =>
                ShowPromptEditPanel(flyout, aiManager, prompt.Id, () =>
                    ShowPromptsPanel(flyout, aiManager, onBack)));
            btn.Tag = prompt.Title;

            // Right-click: Edit / Delete
            var ctxFlyout = new MenuFlyout();
            var editItem = new MenuFlyoutItem
            {
                Text = Lang.T("ai_prompt_edit"),
                Icon = new FontIcon { Glyph = "\uE70F" },
            };
            editItem.Click += (_, _) =>
                ShowCustomPromptFormPanel(flyout, aiManager, prompt, () =>
                    ShowPromptsPanel(flyout, aiManager, onBack));
            ctxFlyout.Items.Add(editItem);

            var deleteItem = new MenuFlyoutItem
            {
                Text = Lang.T("delete"),
                Icon = new FontIcon { Glyph = "\uE74D" },
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99)),
            };
            deleteItem.Click += (_, _) =>
            {
                aiManager.Settings.CustomPrompts.Remove(prompt);
                aiManager.SaveSettings();
                ShowPromptsPanel(flyout, aiManager, onBack);
            };
            ctxFlyout.Items.Add(deleteItem);
            btn.ContextFlyout = ctxFlyout;

            panel.Children.Add(btn);
        }

        panel.Children.Add(CreateSeparator());

        // Add button
        var addBtn = CreateButton(
            new ActionItem("\uE710", Lang.T("ai_prompt_add"), [], () => { }),
            () => ShowCustomPromptFormPanel(flyout, aiManager, null, () =>
                ShowPromptsPanel(flyout, aiManager, onBack)));
        addBtn.Tag = Lang.T("ai_prompt_add");
        panel.Children.Add(addBtn);

        flyout.Content = panel;
    }

    private static void ShowPromptEditPanel(Flyout flyout, AiManager aiManager, string promptKey, Action onBack)
    {
        // Check if it's a custom prompt
        var custom = aiManager.Settings.CustomPrompts.FirstOrDefault(p => p.Id == promptKey);
        if (custom != null)
        {
            ShowCustomPromptFormPanel(flyout, aiManager, custom, onBack);
            return;
        }

        var panel = CreateSubPanelWithHeader(Lang.T(promptKey), onBack);

        var currentValue = aiManager.GetPrompt(promptKey);

        var textBox = new TextBox
        {
            Text = currentValue,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 100,
            MaxHeight = 160,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(10, 8, 10, 8),
        };
        panel.Children.Add(textBox);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 10, 8)
        };

        var validateBtn = new Button
        {
            Content = Lang.T("ai_prompt_validate"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        validateBtn.Click += (_, _) =>
        {
            var newValue = textBox.Text?.Trim() ?? "";
            var defaultVal = AiManager.GetDefaultPrompt(promptKey);
            if (string.IsNullOrWhiteSpace(newValue) || newValue == defaultVal)
                aiManager.Settings.Prompts.Remove(promptKey);
            else
                aiManager.Settings.Prompts[promptKey] = newValue;
            aiManager.SaveSettings();
            onBack();
        };

        var cancelBtn = new Button { Content = Lang.T("cancel") };
        cancelBtn.Click += (_, _) => onBack();

        buttonsPanel.Children.Add(validateBtn);
        buttonsPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonsPanel);

        flyout.Content = panel;
    }

    private static void ShowCustomPromptFormPanel(Flyout flyout, AiManager aiManager, AiManager.CustomPrompt? existing, Action onBack)
    {
        var isEdit = existing != null;
        var panel = CreateSubPanelWithHeader(
            isEdit ? existing!.Title : Lang.T("ai_prompt_add"), onBack);

        var form = new StackPanel { Spacing = 8, Margin = new Thickness(10, 8, 10, 8) };

        var titleBox = new TextBox
        {
            Header = Lang.T("ai_prompt_title"),
            Text = existing?.Title ?? "",
            FontSize = 12,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        form.Children.Add(titleBox);

        var contentBox = new TextBox
        {
            Header = Lang.T("ai_prompt_content"),
            Text = existing?.Instruction ?? "",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 80,
            MaxHeight = 140,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        form.Children.Add(contentBox);

        var saveBtn = new Button
        {
            Content = Lang.T("save"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        saveBtn.Click += (_, _) =>
        {
            var title = titleBox.Text?.Trim() ?? "";
            var instruction = contentBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(instruction)) return;

            if (isEdit)
            {
                existing!.Title = title;
                existing.Instruction = instruction;
            }
            else
            {
                aiManager.Settings.CustomPrompts.Add(new AiManager.CustomPrompt
                {
                    Title = title,
                    Instruction = instruction,
                });
            }
            aiManager.SaveSettings();
            onBack();
        };
        form.Children.Add(saveBtn);

        panel.Children.Add(form);
        flyout.Content = panel;
    }

    // ── Download helpers ──

    private static async Task DownloadLocalModelWithDialog(AiManager aiManager, AiManager.LocalModel model, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action? onAiStateChanged = null)
    {
        using var cts = new CancellationTokenSource();
        var progressBar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var statusText = new TextBlock { Text = Lang.T("ai_downloading") + $" {model.Name}\u2026", FontSize = 12 };
        var sizeText = new TextBlock { Text = model.Size, FontSize = 11, Opacity = 0.45 };

        var contentPanel = new StackPanel { Spacing = 8 };
        contentPanel.Children.Add(statusText);
        contentPanel.Children.Add(progressBar);
        contentPanel.Children.Add(sizeText);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Lang.T("ai_download"),
            Content = contentPanel,
            CloseButtonText = Lang.T("cancel")
        };

        var progress = new Progress<(long downloaded, long? total)>(p =>
        {
            dialog.DispatcherQueue.TryEnqueue(() =>
            {
                if (p.total.HasValue && p.total.Value > 0)
                {
                    var pct = (double)p.downloaded / p.total.Value * 100;
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = pct;
                    var dlMb = p.downloaded / (1024.0 * 1024.0);
                    var totalMb = p.total.Value / (1024.0 * 1024.0);
                    statusText.Text = $"{dlMb:F0}/{totalMb:F0} MB ({pct:F0}%)";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                }
            });
        });

        bool success = false;
        string? errorMsg = null;
        var downloadTask = Task.Run(async () =>
        {
            try
            {
                await aiManager.DownloadModelAsync(model, progress, cts.Token);
                success = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errorMsg = ex.Message; }
            dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide());
        });

        await dialog.ShowAsync();
        if (!success) cts.Cancel();
        try { await downloadTask; } catch { }

        if (success)
        {
            aiManager.Settings.IsEnabled = true;
            aiManager.Settings.LastLocalModelFileName = model.FileName;
            aiManager.Settings.LastProviderId = "";
            aiManager.Settings.LastModelId = "";
            aiManager.SaveSettings();
            onAiStateChanged?.Invoke();
        }
        else if (errorMsg != null)
        {
            var errDlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Lang.T("error"),
                Content = errorMsg,
                CloseButtonText = Lang.T("ok")
            };
            await errDlg.ShowAsync();
        }
    }

    private static async Task DownloadCustomModelWithDialog(AiManager aiManager, string url, string fileName, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action? onAiStateChanged = null)
    {
        using var cts = new CancellationTokenSource();
        var progressBar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var statusText = new TextBlock { Text = Lang.T("ai_downloading") + $" {fileName}\u2026", FontSize = 12 };

        var contentPanel = new StackPanel { Spacing = 8 };
        contentPanel.Children.Add(statusText);
        contentPanel.Children.Add(progressBar);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Lang.T("ai_download"),
            Content = contentPanel,
            CloseButtonText = Lang.T("cancel")
        };

        var progress = new Progress<(long downloaded, long? total)>(p =>
        {
            dialog.DispatcherQueue.TryEnqueue(() =>
            {
                if (p.total.HasValue && p.total.Value > 0)
                {
                    var pct = (double)p.downloaded / p.total.Value * 100;
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = pct;
                    var dlMb = p.downloaded / (1024.0 * 1024.0);
                    var totalMb = p.total.Value / (1024.0 * 1024.0);
                    statusText.Text = $"{dlMb:F0}/{totalMb:F0} MB ({pct:F0}%)";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                }
            });
        });

        bool success = false;
        string? errorMsg = null;
        var downloadTask = Task.Run(async () =>
        {
            try
            {
                await aiManager.DownloadFromUrlAsync(url, fileName, progress, cts.Token);
                success = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errorMsg = ex.Message; }
            dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide());
        });

        await dialog.ShowAsync();
        if (!success) cts.Cancel();
        try { await downloadTask; } catch { }

        if (success)
        {
            aiManager.Settings.IsEnabled = true;
            aiManager.Settings.LastLocalModelFileName = fileName;
            aiManager.Settings.LastProviderId = "";
            aiManager.Settings.LastModelId = "";
            aiManager.SaveSettings();
            onAiStateChanged?.Invoke();
        }
        else if (errorMsg != null)
        {
            var errDlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Lang.T("error"),
                Content = errorMsg,
                CloseButtonText = Lang.T("ok")
            };
            await errDlg.ShowAsync();
        }
    }
}
