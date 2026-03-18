using Microsoft.UI.Xaml;

namespace NoteUI;

/// <summary>
/// Reliable theme detection based on the root FrameworkElement's ActualTheme.
/// WinUI 3 framework brushes cannot be resolved from code-behind via
/// Application.Current.Resources when the app theme differs from the system theme.
/// Instead, rely on XAML theme inheritance (no explicit Foreground on elements)
/// and use IsDark() for any conditional styling in code-behind.
/// </summary>
internal static class ThemeHelper
{
    private static FrameworkElement? _root;

    /// <summary>
    /// Call once after the root Content has RequestedTheme set.
    /// </summary>
    internal static void Initialize(FrameworkElement root)
    {
        _root = root;
    }

    /// <summary>
    /// Returns true when the app is in dark mode, based on ActualTheme.
    /// </summary>
    internal static bool IsDark()
    {
        if (_root != null)
            return _root.ActualTheme == ElementTheme.Dark;

        // Fallback before initialization: system detection
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var bg = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R < 128;
        }
        catch { return false; }
    }

    // ── Card background colors ────────────────────────────────
    // Semi-transparent overlays that work on Mica/Acrylic backdrops.

    internal static Windows.UI.Color CardBackground =>
        IsDark()
            ? Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)   // subtle white overlay
            : Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);  // 70 % white

    internal static Windows.UI.Color CardBackgroundWithColor(Windows.UI.Color noteColor) =>
        IsDark()
            ? Windows.UI.Color.FromArgb(30, noteColor.R, noteColor.G, noteColor.B)
            : CardBackground; // light mode: neutral card + color bar only
}
