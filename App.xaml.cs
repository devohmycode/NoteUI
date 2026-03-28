using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Lang.SetLanguage(AppSettings.LoadLanguage());
        ApplyFontResource(AppSettings.LoadFontSetting());
        ReminderService.Initialize();
        _window = new MainWindow();
        _window.Activate();

        // Only minimize at startup when launched via Windows auto-start
        if (AppSettings.LoadStartMinimized() && AppSettings.LoadStartWithWindows())
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (uptime.TotalMinutes < 5)
            {
                var appWindow = _window.AppWindow;
                appWindow.Hide();
            }
        }
    }

    public static void ApplyFontResource(string font)
    {
        var fontFamily = AppSettings.GetFontFamily(font);
        Application.Current.Resources["ContentControlThemeFontFamily"] = fontFamily;
    }
}
