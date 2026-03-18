using Microsoft.UI.Xaml;

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
        ReminderService.Initialize();
        _window = new MainWindow();
        _window.Activate();
    }
}
