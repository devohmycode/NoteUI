using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

public sealed partial class AcrylicSettingsWindow : Window
{
    private readonly Action<BackdropSettings> _onSettingsChanged;
    private bool _suppressChanges = true;
    private string _currentKind = "Base";

    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    public AcrylicSettingsWindow(BackdropSettings settings, Action<BackdropSettings> onSettingsChanged)
    {
        _onSettingsChanged = onSettingsChanged;

        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;

        WindowHelper.RemoveWindowBorder(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(280, 440));
        WindowShadow.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        var theme = AppSettings.LoadThemeSetting();

        // Apply acrylic to this window too (live preview)
        AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);

        LoadSettings(settings);
        ApplyAcrylicLocalization();

        this.Closed += (_, _) =>
        {
            _acrylicController?.Dispose();
            AppSettings.SaveBackdropSettings(BuildSettings());
        };
    }

    private void ApplyAcrylicLocalization()
    {
        AcrylicTitle.Text = Lang.T("acrylic_custom_title");
        ToolTipService.SetToolTip(AcrylicCloseButton, Lang.T("tip_close"));
        TintOpacityLabel.Text = Lang.T("tint_opacity");
        LuminosityLabel.Text = Lang.T("luminosity");
        TintColorLabel.Text = Lang.T("tint_color");
        FallbackColorLabel.Text = Lang.T("fallback_color");
        StyleLabel.Text = Lang.T("style");
    }

    public void SetPosition(int x, int y)
    {
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void LoadSettings(BackdropSettings s)
    {
        _suppressChanges = true;
        TintOpacitySlider.Value = s.TintOpacity;
        LuminositySlider.Value = s.LuminosityOpacity;
        TintColorBox.Text = s.TintColor;
        FallbackColorBox.Text = s.FallbackColor;
        _currentKind = s.Kind;
        UpdateKindButtons();
        UpdateDisplayValues();
        UpdateColorPreviews();
        _suppressChanges = false;
    }

    private BackdropSettings BuildSettings() => new(
        "acrylic_custom",
        TintOpacitySlider.Value,
        LuminositySlider.Value,
        TintColorBox.Text,
        FallbackColorBox.Text,
        _currentKind);

    private void ApplyChanges()
    {
        if (_suppressChanges) return;
        var settings = BuildSettings();
        AppSettings.SaveBackdropSettings(settings);
        AppSettings.ApplyToWindow(this, settings, ref _acrylicController, ref _configSource);
        _onSettingsChanged(settings);
    }

    private void UpdateDisplayValues()
    {
        if (TintOpacityValue == null || LuminosityValue == null) return;
        TintOpacityValue.Text = TintOpacitySlider.Value.ToString("F2");
        LuminosityValue.Text = LuminositySlider.Value.ToString("F2");
    }

    private void UpdateColorPreviews()
    {
        if (TintColorPreview == null || FallbackColorPreview == null) return;
        try
        {
            var tint = AppSettings.ParseColor(TintColorBox.Text);
            TintColorPreview.Background = new SolidColorBrush(tint);
        }
        catch { }
        try
        {
            var fb = AppSettings.ParseColor(FallbackColorBox.Text);
            FallbackColorPreview.Background = new SolidColorBrush(fb);
        }
        catch { }
    }

    private void UpdateKindButtons()
    {
        if (BaseButton == null || ThinButton == null) return;
        var selected = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var normal = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BaseButton.Background = _currentKind == "Base" ? selected : normal;
        ThinButton.Background = _currentKind == "Thin" ? selected : normal;
    }

    // ── Events ──────────────────────────────────────────────────

    private void Slider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateDisplayValues();
        ApplyChanges();
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreviews();
        var text = ((TextBox)sender).Text;
        if (text.StartsWith('#') && text.Length == 7)
            ApplyChanges();
    }

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        _currentKind = ((Button)sender).Tag as string ?? "Base";
        UpdateKindButtons();
        ApplyChanges();
    }

    private void TintColorPreview_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ShowColorPickerFlyout((Border)sender, TintColorBox);
    }

    private void FallbackColorPreview_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ShowColorPickerFlyout((Border)sender, FallbackColorBox);
    }

    private void ShowColorPickerFlyout(Border target, TextBox colorBox)
    {
        Windows.UI.Color initialColor;
        try { initialColor = AppSettings.ParseColor(colorBox.Text); }
        catch { initialColor = Microsoft.UI.Colors.Black; }

        var picker = new ColorPicker
        {
            Color = initialColor,
            IsAlphaEnabled = false,
            IsColorSpectrumVisible = true,
            IsColorSliderVisible = false,
            IsHexInputVisible = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
        };

        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 320));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 400));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));

        var flyout = new Flyout
        {
            Content = picker,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Left,
            FlyoutPresenterStyle = style,
        };

        picker.ColorChanged += (_, args) =>
        {
            var c = args.NewColor;
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            colorBox.Text = hex;
        };

        flyout.ShowAt(target);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

    // ── Drag ────────────────────────────────────────────────────

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
