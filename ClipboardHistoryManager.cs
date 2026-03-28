using System.Text.Json;

namespace NoteUI;

public class ClipboardHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ContentType { get; set; } = "text"; // "text" or "image"
    public string? TextContent { get; set; }
    public string? RtfContent { get; set; }
    public byte[]? ImageData { get; set; }
    public string? SourceExePath { get; set; }
    public string? SourceTitle { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }

    public string Preview => ContentType == "image"
        ? $"[{Lang.T("clipboard_image")}]"
        : string.IsNullOrEmpty(TextContent)
            ? ""
            : TextContent.Length > 150 ? TextContent[..150] : TextContent;
}

public class ClipboardHistoryManager
{
    private const int MaxEntries = 50;
    private readonly List<ClipboardHistoryEntry> _entries = [];
    private readonly string _savePath;

    public IReadOnlyList<ClipboardHistoryEntry> Entries => _entries;

    public ClipboardHistoryManager()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteUI");
        _savePath = Path.Combine(dir, "clipboard_history.json");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var json = File.ReadAllText(_savePath);
            var entries = JsonSerializer.Deserialize<List<ClipboardHistoryEntry>>(json);
            if (entries != null)
            {
                _entries.Clear();
                _entries.AddRange(entries);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_savePath, json);
        }
        catch { }
    }

    public void AddEntry(ClipboardHistoryEntry entry)
    {
        _entries.Insert(0, entry);
        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries.LastOrDefault(e => !e.IsPinned);
            if (oldest != null)
                _entries.Remove(oldest);
            else
                break;
        }
        Save();
    }

    public void RemoveEntry(string id)
    {
        _entries.RemoveAll(e => e.Id == id);
        Save();
    }

    public void TogglePin(string id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry != null)
        {
            entry.IsPinned = !entry.IsPinned;
            Save();
        }
    }

    public List<ClipboardHistoryEntry> GetSorted()
    {
        return _entries
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.CapturedAt)
            .ToList();
    }

    public void Clear()
    {
        _entries.RemoveAll(e => !e.IsPinned);
        Save();
    }

    public bool IsDuplicate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return _entries.Any(e =>
            e.ContentType == "text" &&
            string.Equals(e.TextContent, text, StringComparison.Ordinal));
    }

    public static string GetRelativeTime(DateTime dt)
    {
        var diff = DateTime.Now - dt;

        if (diff.TotalMinutes < 1)
            return Lang.T("clipboard_just_now");
        if (diff.TotalMinutes < 60)
            return Lang.T("clipboard_minutes_ago", (int)diff.TotalMinutes);
        if (diff.TotalHours < 24)
            return Lang.T("clipboard_hours_ago", (int)diff.TotalHours);
        if (diff.TotalDays < 2)
            return Lang.T("clipboard_yesterday");
        return Lang.T("clipboard_days_ago", (int)diff.TotalDays);
    }
}
