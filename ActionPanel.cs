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
        Action<Flyout>? onShowAi = null)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle(260, 320);

        var panel = new StackPanel { Spacing = 0 };
        var allButtons = new List<Button>();

        // Theme section
        panel.Children.Add(CreateHeader(Lang.T("theme")));
        panel.Children.Add(CreateSeparator());

        var themes = new[] { ("system", Lang.T("theme_system")), ("light", Lang.T("theme_light")), ("dark", Lang.T("theme_dark")) };
        foreach (var (key, label) in themes)
        {
            var k = key;
            var btn = CreateCheckItem(label, key == currentTheme, () =>
            {
                onThemeSelected(k);
                flyout.Hide();
            });
            allButtons.Add(btn);
            panel.Children.Add(btn);
        }

        // Backdrop section
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(CreateHeader(Lang.T("backdrop")));
        panel.Children.Add(CreateSeparator());

        var backdrops = new[]
        {
            ("acrylic", "Acrylic"),
            ("mica", "Mica"),
            ("mica_alt", "MicaAlt"),
            ("acrylic_custom", Lang.T("backdrop_acrylic_custom")),
            ("none", Lang.T("backdrop_none"))
        };
        foreach (var (key, label) in backdrops)
        {
            var k = key;
            var btn = CreateCheckItem(label, key == currentBackdropType, () =>
            {
                onBackdropSelected(k);
                flyout.Hide();
            });
            allButtons.Add(btn);
            panel.Children.Add(btn);
        }

        // Storage section
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(CreateHeader(Lang.T("storage")));
        panel.Children.Add(CreateSeparator());

        var isCustomFolder = !string.Equals(currentNotesFolder, defaultNotesFolder, StringComparison.OrdinalIgnoreCase);

        // Local option
        var localBtn = CreateCheckItem(Lang.T("local"), !isFirebaseConnected && !isCustomFolder, () =>
        {
            if (isCustomFolder) { onResetFolder(); flyout.Hide(); }
            else if (isFirebaseConnected) { onDisconnectFirebase(); flyout.Hide(); }
        });
        localBtn.Tag = Lang.T("local");
        allButtons.Add(localBtn);
        panel.Children.Add(localBtn);

        if (isCustomFolder)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ShortenPath(currentNotesFolder),
                FontSize = 11,
                Opacity = 0.45,
                Margin = new Thickness(12, 0, 12, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        // Dossier option
        var folderBtn = CreateCheckItem(Lang.T("custom_folder"), isCustomFolder && !isFirebaseConnected, () =>
        {
            onChangeFolder();
            flyout.Hide();
        });
        folderBtn.Tag = Lang.T("custom_folder");
        allButtons.Add(folderBtn);
        panel.Children.Add(folderBtn);

        // Cloud (Firebase) option
        if (isFirebaseConnected)
        {
            var cloudBtn = CreateCheckItem(Lang.T("cloud"), true, () =>
            {
                onSyncFirebase();
                flyout.Hide();
            });
            cloudBtn.Tag = Lang.T("cloud");
            allButtons.Add(cloudBtn);
            panel.Children.Add(cloudBtn);

            if (!string.IsNullOrEmpty(firebaseEmail))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = firebaseEmail,
                    FontSize = 11,
                    Opacity = 0.45,
                    Margin = new Thickness(12, 0, 12, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var disconnectBtn = CreateCheckItem(Lang.T("disconnect"), false, () =>
            {
                onDisconnectFirebase();
                flyout.Hide();
            });
            disconnectBtn.Tag = Lang.T("disconnect") + " Cloud";
            allButtons.Add(disconnectBtn);
            panel.Children.Add(disconnectBtn);
        }
        else
        {
            var connectBtn = CreateCheckItem(Lang.T("cloud"), false, () =>
            {
                onConfigureFirebase();
                flyout.Hide();
            });
            connectBtn.Tag = Lang.T("cloud");
            allButtons.Add(connectBtn);
            panel.Children.Add(connectBtn);
        }

        // WebDAV / Nextcloud
        if (isWebDavConnected)
        {
            var webdavBtn = CreateCheckItem("WebDAV", true, () =>
            {
                onSyncWebDav();
                flyout.Hide();
            });
            webdavBtn.Tag = "WebDAV Nextcloud";
            allButtons.Add(webdavBtn);
            panel.Children.Add(webdavBtn);

            if (!string.IsNullOrEmpty(webDavUrl))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = ShortenPath(webDavUrl),
                    FontSize = 11,
                    Opacity = 0.45,
                    Margin = new Thickness(12, 0, 12, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var disconnectWdBtn = CreateCheckItem(Lang.T("disconnect"), false, () =>
            {
                onDisconnectWebDav();
                flyout.Hide();
            });
            disconnectWdBtn.Tag = Lang.T("disconnect") + " WebDAV";
            allButtons.Add(disconnectWdBtn);
            panel.Children.Add(disconnectWdBtn);
        }
        else
        {
            var connectWdBtn = CreateCheckItem("WebDAV", false, () =>
            {
                onConfigureWebDav();
                flyout.Hide();
            });
            connectWdBtn.Tag = "WebDAV Nextcloud";
            allButtons.Add(connectWdBtn);
            panel.Children.Add(connectWdBtn);
        }

        // AI section
        if (onShowAi != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader(Lang.T("ai_section")));
            panel.Children.Add(CreateSeparator());
            var aiBtn = CreateCheckItem(Lang.T("ai_label"), false, () => onShowAi(flyout));
            aiBtn.Tag = Lang.T("ai_label") + " IA AI OpenAI Claude Gemini GGUF";
            allButtons.Add(aiBtn);
            panel.Children.Add(aiBtn);
        }

        // Voice model section
        if (onShowVoiceModels != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader(Lang.T("voice_section")));
            panel.Children.Add(CreateSeparator());
            var voiceBtn = CreateCheckItem(Lang.T("voice_model"), false, () => onShowVoiceModels(flyout));
            voiceBtn.Tag = Lang.T("voice_model") + " TTS STT";
            allButtons.Add(voiceBtn);
            panel.Children.Add(voiceBtn);
        }

        // Shortcuts section
        if (onShowShortcuts != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader(Lang.T("shortcuts_section")));
            panel.Children.Add(CreateSeparator());
            var shortcutsBtn = CreateCheckItem(Lang.T("shortcuts_label"), false, () => onShowShortcuts(flyout));
            shortcutsBtn.Tag = Lang.T("shortcuts_label") + " Raccourcis clavier";
            allButtons.Add(shortcutsBtn);
            panel.Children.Add(shortcutsBtn);
        }

        // Editor section (slash commands toggle)
        if (onSlashToggled != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader(Lang.T("editor_section")));
            panel.Children.Add(CreateSeparator());

            var slashBtn = CreateCheckItem(Lang.T("slash_commands_toggle"), slashEnabled, () =>
            {
                onSlashToggled(!slashEnabled);
                flyout.Hide();
            });
            slashBtn.Tag = Lang.T("slash_commands_toggle");
            allButtons.Add(slashBtn);
            panel.Children.Add(slashBtn);
        }

        // Language section
        if (onLanguageSelected != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader(Lang.T("language_section")));
            panel.Children.Add(CreateSeparator());

            var langs = new[] { ("en", Lang.T("language_en")), ("fr", Lang.T("language_fr")) };
            foreach (var (code, label) in langs)
            {
                var c = code;
                var btn = CreateCheckItem(label, code == (currentLanguage ?? "en"), () =>
                {
                    onLanguageSelected(c);
                    flyout.Hide();
                });
                btn.Tag = label;
                allButtons.Add(btn);
                panel.Children.Add(btn);
            }
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

    public static void ShowVoiceModelsPanel(Flyout flyout, string? currentModelId,
        XamlRoot xamlRoot,
        Action<SttModelInfo> onSelectModel, Action<SttModelInfo> onDeleteModel, Action onBack, Action onRebuild)
    {
        var panel = new StackPanel { Spacing = 0 };

        // Back button + header
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
            Text = Lang.T("voice_model"),
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

        flyout.Content = panel;
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
        var panel = new StackPanel { Spacing = 0 };

        // Back + header
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
            Text = Lang.T("shortcuts_label"),
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

    internal static void ShowAiPanel(Flyout flyout, AiManager aiManager, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action onBack)
    {
        var panel = new StackPanel { Spacing = 0 };

        // Back + header
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
            Text = Lang.T("ai_section"),
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
                        ShowAiPanel(flyout, aiManager, xamlRoot, onBack));
                }
            };

            keyRow.Children.Add(keyBox);
            keyRow.Children.Add(saveKeyBtn);
            providerBlock.Children.Add(keyRow);
            panel.Children.Add(providerBlock);

            // If key already exists, add a button to browse models
            if (hasKey)
            {
                var browseBtn = CreateCheckItem($"{provider.Name} — {Lang.T("ai_select_model")}", false, () =>
                    ShowAiModelsPanel(flyout, aiManager, p, xamlRoot, () =>
                        ShowAiPanel(flyout, aiManager, xamlRoot, onBack)));
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

        // Installed models
        var installed = aiManager.GetInstalledModels();
        if (installed.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("ai_models_installed"),
                FontSize = 11,
                Opacity = 0.45,
                Margin = new Thickness(10, 4, 0, 2)
            });
            foreach (var model in installed)
            {
                var isLoaded = model.FileName == aiManager.LoadedModelFileName;
                var modelBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = isLoaded
                        ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 5, 10, 5),
                    CornerRadius = new CornerRadius(5),
                };
                var modelGrid = new Grid { ColumnSpacing = 8 };
                modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var modelName = new TextBlock { Text = model.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(modelName, 0);
                var modelSize = new TextBlock
                {
                    Text = model.Size,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(modelSize, 1);
                modelGrid.Children.Add(modelName);
                modelGrid.Children.Add(modelSize);
                modelBtn.Content = modelGrid;

                var fn = model.FileName;
                modelBtn.Click += (_, _) =>
                {
                    aiManager.Settings.LastLocalModelFileName = fn;
                    aiManager.SaveSettings();
                    flyout.Hide();
                };
                panel.Children.Add(modelBtn);
            }
        }

        // Available predefined models
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("ai_models_available"),
            FontSize = 11,
            Opacity = 0.45,
            Margin = new Thickness(10, 6, 0, 2)
        });
        foreach (var model in AiManager.PredefinedModels)
        {
            if (model.IsInstalled) continue;
            var m = model;
            var btn = CreateCheckItem($"{model.Name}  ({model.Size})", false, async () =>
            {
                flyout.Hide();
                await DownloadLocalModelWithDialog(aiManager, m, xamlRoot);
            });
            btn.Tag = model.Name + " GGUF local";
            panel.Children.Add(btn);
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
            await DownloadCustomModelWithDialog(aiManager, url, fileName, xamlRoot);
        });
        downloadBtn.Tag = Lang.T("ai_download") + " GGUF custom";
        panel.Children.Add(downloadBtn);

        flyout.Content = panel;
    }

    // ── AI Models sub-panel (cloud provider models) ──

    private static async void ShowAiModelsPanel(Flyout flyout, AiManager aiManager,
        ICloudAiProvider provider, Microsoft.UI.Xaml.XamlRoot xamlRoot, Action onBack)
    {
        var panel = new StackPanel { Spacing = 0 };

        // Back + header
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
            Text = provider.Name,
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
                    aiManager.Settings.LastProviderId = provider.Id;
                    aiManager.Settings.LastModelId = model.Id;
                    aiManager.SaveSettings();
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

    // ── Download helpers ──

    private static async Task DownloadLocalModelWithDialog(AiManager aiManager, AiManager.LocalModel model, Microsoft.UI.Xaml.XamlRoot xamlRoot)
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
            aiManager.Settings.LastLocalModelFileName = model.FileName;
            aiManager.SaveSettings();
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

    private static async Task DownloadCustomModelWithDialog(AiManager aiManager, string url, string fileName, Microsoft.UI.Xaml.XamlRoot xamlRoot)
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
            aiManager.Settings.LastLocalModelFileName = fileName;
            aiManager.SaveSettings();
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
