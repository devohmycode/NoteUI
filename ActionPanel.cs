using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace NoteUI;

public static class ActionPanel
{
    public record ActionItem(string? Glyph, string Label, string[] Keys, Action Handler, FrameworkElement? Icon = null, bool IsDestructive = false);

    public static Style CreateFlyoutPresenterStyle(double minWidth = 260, double maxWidth = 320)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(4)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, minWidth));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, maxWidth));
        style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty,
            (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"]));
        style.Setters.Add(new Setter(FlyoutPresenter.BorderBrushProperty,
            (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]));
        style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)));
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
            PlaceholderText = "Rechercher...",
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

        foreach (var (name, hex, display) in NoteColors.All)
        {
            var isSelected = name == currentColor;
            var colorName = name;

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(5),
                Tag = display
            };

            var grid = new Grid { ColumnSpacing = 10 };
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
        Action<Flyout>? onShowShortcuts = null)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = CreateFlyoutPresenterStyle(260, 320);

        var panel = new StackPanel { Spacing = 0 };
        var allButtons = new List<Button>();

        // Theme section
        panel.Children.Add(CreateHeader("Th\u00e8me"));
        panel.Children.Add(CreateSeparator());

        var themes = new[] { ("system", "Syst\u00e8me"), ("light", "Clair"), ("dark", "Sombre") };
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
        panel.Children.Add(CreateHeader("Fond"));
        panel.Children.Add(CreateSeparator());

        var backdrops = new[]
        {
            ("acrylic", "Acrylic"),
            ("mica", "Mica"),
            ("mica_alt", "MicaAlt"),
            ("acrylic_custom", "Acrylic personnalis\u00e9"),
            ("none", "Aucun")
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
        panel.Children.Add(CreateHeader("Stockage"));
        panel.Children.Add(CreateSeparator());

        var isCustomFolder = !string.Equals(currentNotesFolder, defaultNotesFolder, StringComparison.OrdinalIgnoreCase);

        // Local option
        var localBtn = CreateCheckItem("Local", !isFirebaseConnected && !isCustomFolder, () =>
        {
            if (isCustomFolder) { onResetFolder(); flyout.Hide(); }
            else if (isFirebaseConnected) { onDisconnectFirebase(); flyout.Hide(); }
        });
        localBtn.Tag = "Local";
        allButtons.Add(localBtn);
        panel.Children.Add(localBtn);

        if (isCustomFolder)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ShortenPath(currentNotesFolder),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Margin = new Thickness(12, 0, 12, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        // Dossier option
        var folderBtn = CreateCheckItem("Dossier personnalis\u00e9", isCustomFolder && !isFirebaseConnected, () =>
        {
            onChangeFolder();
            flyout.Hide();
        });
        folderBtn.Tag = "Dossier personnalis\u00e9";
        allButtons.Add(folderBtn);
        panel.Children.Add(folderBtn);

        // Cloud (Firebase) option
        if (isFirebaseConnected)
        {
            var cloudBtn = CreateCheckItem("Cloud", true, () =>
            {
                onSyncFirebase();
                flyout.Hide();
            });
            cloudBtn.Tag = "Cloud";
            allButtons.Add(cloudBtn);
            panel.Children.Add(cloudBtn);

            if (!string.IsNullOrEmpty(firebaseEmail))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = firebaseEmail,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Margin = new Thickness(12, 0, 12, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var disconnectBtn = CreateCheckItem("D\u00e9connecter", false, () =>
            {
                onDisconnectFirebase();
                flyout.Hide();
            });
            disconnectBtn.Tag = "D\u00e9connecter Cloud";
            allButtons.Add(disconnectBtn);
            panel.Children.Add(disconnectBtn);
        }
        else
        {
            var connectBtn = CreateCheckItem("Cloud", false, () =>
            {
                onConfigureFirebase();
                flyout.Hide();
            });
            connectBtn.Tag = "Cloud";
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
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Margin = new Thickness(12, 0, 12, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var disconnectWdBtn = CreateCheckItem("D\u00e9connecter", false, () =>
            {
                onDisconnectWebDav();
                flyout.Hide();
            });
            disconnectWdBtn.Tag = "D\u00e9connecter WebDAV";
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

        // Voice model section
        if (onShowVoiceModels != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader("Vocal"));
            panel.Children.Add(CreateSeparator());
            var voiceBtn = CreateCheckItem("Mod\u00e8le vocal", false, () => onShowVoiceModels(flyout));
            voiceBtn.Tag = "Mod\u00e8le vocal TTS STT";
            allButtons.Add(voiceBtn);
            panel.Children.Add(voiceBtn);
        }

        // Shortcuts section
        if (onShowShortcuts != null)
        {
            panel.Children.Add(CreateSeparator());
            panel.Children.Add(CreateHeader("Raccourcis"));
            panel.Children.Add(CreateSeparator());
            var shortcutsBtn = CreateCheckItem("Shortcuts", false, () => onShowShortcuts(flyout));
            shortcutsBtn.Tag = "Shortcuts Raccourcis clavier";
            allButtons.Add(shortcutsBtn);
            panel.Children.Add(shortcutsBtn);
        }

        // Search
        panel.Children.Add(CreateSeparator());
        var searchBox = new TextBox
        {
            PlaceholderText = "Rechercher...",
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
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(5),
            Tag = label
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
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
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(10, 6, 10, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    public static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(8, 2, 8, 2)
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
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(5),
            Tag = action.Label
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var foreground = action.IsDestructive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 99, 99))
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        FrameworkElement iconEl;
        if (action.Icon != null)
        {
            iconEl = action.Icon;
        }
        else
        {
            iconEl = new FontIcon { Glyph = action.Glyph ?? "", FontSize = 14, Foreground = foreground };
        }
        Grid.SetColumn(iconEl, 0);

        var text = new TextBlock
        {
            Text = action.Label,
            FontSize = 13,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);

        var keysPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var key in action.Keys)
        {
            keysPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                Child = new TextBlock
                {
                    Text = key,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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
            Text = "Mod\u00e8le vocal",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
            Text = "Fran\u00e7ais",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(10, 8, 0, 4)
        });
        foreach (var model in SttModels.Available.Where(m => m.Languages == "Fran\u00e7ais"))
            panel.Children.Add(CreateModelItem(model, currentModelId, flyout, xamlRoot, onSelectModel, onDeleteModel, onRebuild));

        // English models
        panel.Children.Add(new TextBlock
        {
            Text = "English",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
        var status = isCurrent ? "\u25cf Actif" : isDownloaded ? "T\u00e9l\u00e9charg\u00e9" : $"{model.SizeMB} MB";

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
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"{engine} \u2014 {status}",
            FontSize = 11,
            Foreground = isCurrent
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        if (isDownloaded)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
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
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
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
                Text = "Supprimer",
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
                    Text = $"T\u00e9l\u00e9chargement de {model.Name}...",
                    FontSize = 12
                };
                var sizeText = new TextBlock
                {
                    Text = $"{model.SizeMB} MB",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                };

                var contentPanel = new StackPanel { Spacing = 8 };
                contentPanel.Children.Add(statusText);
                contentPanel.Children.Add(progressBar);
                contentPanel.Children.Add(sizeText);

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "T\u00e9l\u00e9chargement",
                    Content = contentPanel,
                    CloseButtonText = "Annuler"
                };

                var progress = new Progress<double>(p =>
                {
                    progressBar.Value = p * 100;
                    statusText.Text = $"T\u00e9l\u00e9chargement... {p * 100:F0}%";
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
                        Title = "Erreur",
                        Content = errorMsg,
                        CloseButtonText = "OK"
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
            Text = "Raccourcis clavier",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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

            row.Children.Add(new TextBlock
            {
                Text = entry.DisplayLabel,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
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
}
