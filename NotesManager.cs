using System.Text.Json;

namespace NoteUI;

public class NotesManager
{
    private readonly List<NoteEntry> _notes = [];
    private string _saveDir;
    private string _savePath;

    public FirebaseSync? Firebase { get; private set; }
    public WebDavSync? WebDav { get; private set; }
    public bool IsSyncing { get; private set; }

    public IReadOnlyList<NoteEntry> Notes => _notes;
    public string CurrentFolder => _saveDir;

    public NotesManager()
    {
        _saveDir = AppSettings.LoadNotesFolder();
        _savePath = Path.Combine(_saveDir, "notes.json");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var json = File.ReadAllText(_savePath);
            var entries = JsonSerializer.Deserialize<List<NoteEntry>>(json);
            if (entries != null)
            {
                _notes.Clear();
                _notes.AddRange(entries);
            }
        }
        catch { }
    }

    public void ChangeFolder(string newFolder)
    {
        _saveDir = newFolder;
        _savePath = Path.Combine(_saveDir, "notes.json");
        AppSettings.SaveNotesFolder(newFolder);
        _notes.Clear();
        Load();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_saveDir);
            var json = JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_savePath, json);
        }
        catch { }

        // Fire-and-forget push to cloud
        if (Firebase is { IsConnected: true })
            _ = Firebase.PushNotesAsync(_notes);
        if (WebDav is { IsConfigured: true })
            _ = WebDav.PushNotesAsync(_notes);
    }

    // ── Firebase ─────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> SignInFirebase(string url, string apiKey, string email, string password)
    {
        Firebase?.Dispose();
        Firebase = new FirebaseSync();
        Firebase.Configure(url, apiKey);

        var result = await Firebase.SignInAsync(email, password);
        if (result.Success)
        {
            AppSettings.SaveFirebaseSettings(url, apiKey, Firebase.GetRefreshToken() ?? "");
            return result;
        }

        Firebase.Dispose();
        Firebase = null;
        return result;
    }

    public async Task<(bool Success, string? Error)> SignUpFirebase(string url, string apiKey, string email, string password)
    {
        Firebase?.Dispose();
        Firebase = new FirebaseSync();
        Firebase.Configure(url, apiKey);

        var result = await Firebase.SignUpAsync(email, password);
        if (result.Success)
        {
            AppSettings.SaveFirebaseSettings(url, apiKey, Firebase.GetRefreshToken() ?? "");
            return result;
        }

        Firebase.Dispose();
        Firebase = null;
        return result;
    }

    public async Task<(bool Success, string? Error)> SignInFirebaseWithGoogle(string url, string apiKey)
    {
        Firebase?.Dispose();
        Firebase = new FirebaseSync();
        Firebase.Configure(url, apiKey);

        var result = await Firebase.SignInWithGoogleAsync();
        if (result.Success)
        {
            AppSettings.SaveFirebaseSettings(url, apiKey, Firebase.GetRefreshToken() ?? "");
            return result;
        }

        Firebase.Dispose();
        Firebase = null;
        return result;
    }

    public async Task<bool> SyncFromFirebase()
    {
        if (Firebase is not { IsConnected: true }) return false;
        IsSyncing = true;
        try
        {
            var remote = await Firebase.PullNotesAsync();
            if (remote == null) return false;

            var merged = new Dictionary<string, NoteEntry>();
            foreach (var n in _notes)
                merged[n.Id] = n;
            foreach (var n in remote)
            {
                if (!merged.TryGetValue(n.Id, out var local) || n.UpdatedAt > local.UpdatedAt)
                    merged[n.Id] = n;
            }

            _notes.Clear();
            _notes.AddRange(merged.Values);
            Save();
            return true;
        }
        catch { return false; }
        finally { IsSyncing = false; }
    }

    public async Task InitFirebaseFromSettings()
    {
        var (url, apiKey, refreshToken) = AppSettings.LoadFirebaseSettings();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(refreshToken))
            return;

        Firebase = new FirebaseSync();
        Firebase.Configure(url, apiKey);
        if (await Firebase.SignInWithRefreshTokenAsync(refreshToken))
        {
            // Update stored refresh token
            AppSettings.SaveFirebaseSettings(url, apiKey, Firebase.GetRefreshToken() ?? "");
            await SyncSettingsFromFirebase();
            await SyncFromFirebase();
        }
        else
        {
            Firebase.Dispose();
            Firebase = null;
        }
    }

    public void DisconnectFirebase()
    {
        Firebase?.Dispose();
        Firebase = null;
        AppSettings.SaveFirebaseSettings("", "", "");
    }

    public async Task SyncSettingsToFirebase()
    {
        if (Firebase is not { IsConnected: true }) return;
        var settings = new Dictionary<string, object>
        {
            ["language"] = AppSettings.LoadLanguage(),
            ["slashEnabled"] = AppSettings.LoadSlashEnabled(),
            ["theme"] = AppSettings.LoadThemeSetting(),
            ["backdrop"] = AppSettings.LoadSettings().Type,
        };
        var masterPwHash = AppSettings.LoadMasterPasswordHash();
        if (!string.IsNullOrEmpty(masterPwHash))
            settings["masterPasswordHash"] = masterPwHash;
        await Firebase.PushSettingsAsync(settings);
    }

    public async Task SyncSettingsFromFirebase()
    {
        if (Firebase is not { IsConnected: true }) return;
        var remote = await Firebase.PullSettingsAsync();
        if (remote == null) return;

        if (remote.TryGetValue("language", out var langEl))
        {
            var lang = langEl.GetString();
            if (!string.IsNullOrEmpty(lang))
            {
                AppSettings.SaveLanguage(lang);
                Lang.SetLanguage(lang);
            }
        }
        if (remote.TryGetValue("slashEnabled", out var slashEl))
            AppSettings.SaveSlashEnabled(slashEl.GetBoolean());
        if (remote.TryGetValue("theme", out var themeEl))
        {
            var theme = themeEl.GetString();
            if (!string.IsNullOrEmpty(theme))
                AppSettings.SaveThemeSetting(theme);
        }
        if (remote.TryGetValue("masterPasswordHash", out var mpEl))
        {
            var hash = mpEl.GetString();
            if (!string.IsNullOrEmpty(hash))
                AppSettings.SaveMasterPasswordHash(hash);
        }
        if (remote.TryGetValue("backdrop", out var backdropEl))
        {
            var backdrop = backdropEl.GetString();
            if (!string.IsNullOrEmpty(backdrop))
            {
                var current = AppSettings.LoadSettings();
                AppSettings.SaveBackdropSettings(current with { Type = backdrop });
            }
        }
    }

    // ── WebDAV ──────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> ConnectWebDav(string url, string username, string password)
    {
        WebDav?.Dispose();
        WebDav = new WebDavSync();
        WebDav.Configure(url, username, password);

        var result = await WebDav.TestConnectionAsync();
        if (result.Success)
        {
            AppSettings.SaveWebDavSettings(url, username, password);
            return result;
        }

        WebDav.Dispose();
        WebDav = null;
        return result;
    }

    public async Task<bool> SyncFromWebDav()
    {
        if (WebDav is not { IsConfigured: true }) return false;
        IsSyncing = true;
        try
        {
            var remote = await WebDav.PullNotesAsync();
            if (remote == null) return false;

            var merged = new Dictionary<string, NoteEntry>();
            foreach (var n in _notes)
                merged[n.Id] = n;
            foreach (var n in remote)
            {
                if (!merged.TryGetValue(n.Id, out var local) || n.UpdatedAt > local.UpdatedAt)
                    merged[n.Id] = n;
            }

            _notes.Clear();
            _notes.AddRange(merged.Values);
            Save();
            return true;
        }
        catch { return false; }
        finally { IsSyncing = false; }
    }

    public async Task InitWebDavFromSettings()
    {
        var (url, username, password) = AppSettings.LoadWebDavSettings();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(username)) return;

        WebDav = new WebDavSync();
        WebDav.Configure(url, username, password);
        var result = await WebDav.TestConnectionAsync();
        if (result.Success)
            await SyncFromWebDav();
        else
        {
            WebDav.Dispose();
            WebDav = null;
        }
    }

    public void DisconnectWebDav()
    {
        WebDav?.Dispose();
        WebDav = null;
        AppSettings.SaveWebDavSettings("", "", "");
    }

    public NoteEntry CreateNote(string color = "Yellow")
    {
        var note = new NoteEntry
        {
            Id = Guid.NewGuid().ToString(),
            Title = Lang.T("untitled"),
            Content = "",
            Color = color,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        _notes.Insert(0, note);
        Save();
        return note;
    }

    public NoteEntry CreateTaskList(string color = "Yellow")
    {
        var note = new NoteEntry
        {
            Id = Guid.NewGuid().ToString(),
            Title = Lang.T("untitled"),
            Content = "",
            Color = color,
            NoteType = "tasklist",
            Tasks = [new TaskItem()],
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        _notes.Insert(0, note);
        Save();
        return note;
    }

    public void UpdateTasks(string id, List<TaskItem> tasks)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.Tasks = tasks;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public void UpdateNote(string id, string content, string? title = null, string? color = null)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.Content = content;
        if (title != null) note.Title = title;
        if (color != null) note.Color = color;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public NoteEntry? DuplicateNote(string id)
    {
        var source = _notes.FirstOrDefault(n => n.Id == id);
        if (source == null) return null;
        var copy = new NoteEntry
        {
            Id = Guid.NewGuid().ToString(),
            Title = source.Title,
            Content = source.Content,
            Color = source.Color,
            IsLocked = source.IsLocked,
            NoteType = source.NoteType,
            Tasks = source.Tasks.Select(t => new TaskItem { Text = t.Text, IsDone = t.IsDone }).ToList(),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        var index = _notes.IndexOf(source);
        _notes.Insert(index + 1, copy);
        Save();
        return copy;
    }

    public void DeleteNote(string id)
    {
        _notes.RemoveAll(n => n.Id == id);
        Save();
    }

    public void DeleteAllNotes()
    {
        _notes.Clear();
        Save();
    }

    public void TogglePin(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.IsPinned = !note.IsPinned;
        Save();
    }

    public void ToggleFavorite(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.IsFavorite = !note.IsFavorite;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public void ToggleArchive(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.IsArchived = !note.IsArchived;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public void ToggleLock(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.IsLocked = !note.IsLocked;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public void UpdateNoteTags(string id, List<string> tags)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.Tags = tags;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public void UpdateNoteReminder(string id, DateTime? reminderAt)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note == null) return;
        note.ReminderAt = reminderAt;
        note.UpdatedAt = DateTime.Now;
        Save();
    }

    public List<string> GetAllTags()
    {
        return _notes
            .SelectMany(n => n.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();
    }

    public List<NoteEntry> GetSorted()
    {
        return _notes
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
    }
}

public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
    public DateTime? ReminderAt { get; set; }
}

public class NoteEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Color { get; set; } = "Yellow";
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsArchived { get; set; }
    public bool IsLocked { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime? ReminderAt { get; set; }
    public string NoteType { get; set; } = "note"; // "note" or "tasklist"
    public List<TaskItem> Tasks { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    private static readonly string[] MonthNames =
        ["", "janv.", "f\u00e9vr.", "mars", "avr.", "mai", "juin", "juil.", "ao\u00fbt", "sept.", "oct.", "nov.", "d\u00e9c."];

    public string DateDisplay
    {
        get
        {
            return UpdatedAt.ToString("dd/MM/yyyy");
        }
    }

    public string Preview
    {
        get
        {
            if (NoteType == "tasklist")
            {
                if (Tasks.Count == 0) return "";
                var lines = Tasks.Take(4).Select(t => (t.IsDone ? "\u2611 " : "\u2610 ") + t.Text);
                return string.Join("\n", lines);
            }
            var text = Content;
            if (text.StartsWith("{\\rtf", StringComparison.Ordinal))
                text = StripRtf(text);
            return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        }
    }

    public string TaskProgress
    {
        get
        {
            if (NoteType != "tasklist" || Tasks.Count == 0) return "";
            var done = Tasks.Count(t => t.IsDone);
            return $"{done}/{Tasks.Count}";
        }
    }

    public byte[]? ExtractFirstImageBytes()
    {
        var content = Content;
        if (string.IsNullOrEmpty(content)) return null;

        var markerIdx = content.IndexOf("\\pngblip", StringComparison.Ordinal);
        if (markerIdx < 0) markerIdx = content.IndexOf("\\jpegblip", StringComparison.Ordinal);
        if (markerIdx < 0) return null;

        // Skip past \pngblip or \jpegblip
        var i = markerIdx + 1;
        while (i < content.Length && content[i] != ' ' && content[i] != '\\' && content[i] != '}') i++;

        // Skip remaining control words (\picwN, \pichN, etc.) until hex data
        while (i < content.Length && content[i] != '}')
        {
            if (content[i] == '\\')
            {
                i++;
                while (i < content.Length && char.IsLetter(content[i])) i++;
                while (i < content.Length && (content[i] == '-' || char.IsDigit(content[i]))) i++;
                if (i < content.Length && content[i] == ' ') i++;
            }
            else if (content[i] == ' ' || content[i] == '\r' || content[i] == '\n')
            {
                i++;
            }
            else break;
        }

        // Read hex data
        var sb = new System.Text.StringBuilder();
        while (i < content.Length && content[i] != '}')
        {
            var c = content[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                sb.Append(c);
            i++;
        }

        var hex = sb.ToString();
        if (hex.Length < 16) return null;

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (int j = 0; j < bytes.Length; j++)
                bytes[j] = byte.Parse(hex.AsSpan(j * 2, 2), System.Globalization.NumberStyles.HexNumber);
            return bytes;
        }
        catch { return null; }
    }

    private static string StripRtf(string rtf)
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
        // Collapse multiple spaces and clean up bullet list artifacts
        var text = result.ToString();
        var lines = text.Split('\n');
        for (int j = 0; j < lines.Length; j++)
            lines[j] = System.Text.RegularExpressions.Regex.Replace(lines[j].Trim(), @"\s{2,}", " ");
        return string.Join("\n", lines).Trim();
    }
}

public static class NoteColors
{
    public static readonly (string Name, string Hex)[] All =
    [
        ("None", ""),
        ("Yellow", "#FFF9B1"),
        ("Green", "#E2F6D3"),
        ("Mint", "#C8F7E1"),
        ("Teal", "#C8E6F0"),
        ("Blue", "#D4E8FF"),
        ("Lavender", "#DDD4FF"),
        ("Purple", "#E0D4FF"),
        ("Pink", "#FFD4E0"),
        ("Coral", "#FFD0C0"),
        ("Orange", "#FFE4B5"),
        ("Peach", "#FFE8D0"),
        ("Sand", "#F0E6D3"),
        ("Gray", "#F0F0F0"),
        ("Charcoal", "#D8D8D8"),
    ];

    public static string GetDisplayName(string name) =>
        Lang.T("color_" + name.ToLowerInvariant());

    public static bool IsNone(string name) => name == "None";

    public static Windows.UI.Color Get(string name)
    {
        if (IsNone(name))
            return new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
        var hex = All.FirstOrDefault(c => c.Name == name).Hex ?? "#FFF9B1";
        return ColorFromHex(hex);
    }

    public static Windows.UI.Color GetDarker(string name, double factor = 0.92)
    {
        if (IsNone(name))
            return new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
        var c = Get(name);
        return new Windows.UI.Color
        {
            A = 255,
            R = (byte)(c.R * factor),
            G = (byte)(c.G * factor),
            B = (byte)(c.B * factor)
        };
    }

    public static Windows.UI.Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Windows.UI.Color
        {
            A = 255,
            R = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            G = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            B = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber)
        };
    }
}
