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

            var dot = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(NoteColors.ColorFromHex(hex)),
                Stroke = new SolidColorBrush(
                    new Windows.UI.Color { A = 60, R = 0, G = 0, B = 0 }),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
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
        Action onConfigureWebDav, Action onDisconnectWebDav, Action onSyncWebDav)
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

    private static Button CreateButton(ActionItem action, Action handler)
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

    private static string ShortenPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            return "~" + path[userProfile.Length..];
        return path.Length > 35 ? "..." + path[^32..] : path;
    }
}
