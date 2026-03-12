using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Xaml;

namespace NoteUI;

internal sealed class ReminderService : IDisposable
{
    private readonly NotesManager _notes;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _firedReminders = [];

    public event Action? ReminderFired;

    public ReminderService(NotesManager notes)
    {
        _notes = notes;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += CheckReminders;
        _timer.Start();
    }

    private void CheckReminders(object? sender, object e)
    {
        var now = DateTime.Now;
        var changed = false;

        foreach (var note in _notes.Notes)
        {
            if (note.NoteType != "tasklist") continue;

            foreach (var task in note.Tasks)
            {
                if (task.ReminderAt == null) continue;
                if (task.ReminderAt > now) continue;
                if (_firedReminders.Contains(task.Id)) continue;

                _firedReminders.Add(task.Id);
                ShowToast(note.Title, task.Text);
                task.ReminderAt = null;
                changed = true;
            }
        }

        if (changed)
        {
            _notes.Save();
            ReminderFired?.Invoke();
        }
    }

    private static void ShowToast(string noteTitle, string taskText)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(taskText) ? noteTitle : taskText;
            if (title.Length > 100)
                title = title[..100] + "\u2026";

            var builder = new AppNotificationBuilder()
                .AddText($"\ud83d\udd14 {title}")
                .AddText(noteTitle)
                .SetScenario(AppNotificationScenario.Reminder);

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }

    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.Register();
        }
        catch { }
    }

    public static void Shutdown()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
