using System.Text.Json;

namespace NoteUI;

public class NotesManager
{
    private readonly List<NoteEntry> _notes = [];
    private string _saveDir;
    private string _savePath;

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
    }

    public NoteEntry CreateNote(string color = "Yellow")
    {
        var note = new NoteEntry
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Sans titre",
            Content = "",
            Color = color,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        _notes.Insert(0, note);
        Save();
        return note;
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

    public List<NoteEntry> GetSorted()
    {
        return _notes
            .OrderByDescending(n => n.UpdatedAt)
            .ToList();
    }
}

public class NoteEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Sans titre";
    public string Content { get; set; } = "";
    public string Color { get; set; } = "Yellow";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    private static readonly string[] MonthNames =
        ["", "janv.", "f\u00e9vr.", "mars", "avr.", "mai", "juin", "juil.", "ao\u00fbt", "sept.", "oct.", "nov.", "d\u00e9c."];

    public string DateDisplay
    {
        get
        {
            if (UpdatedAt.Year == DateTime.Now.Year)
                return $"{UpdatedAt.Day} {MonthNames[UpdatedAt.Month]}";
            return UpdatedAt.ToString("dd/MM/yyyy");
        }
    }

    public string Preview
    {
        get
        {
            var text = Content;
            if (text.StartsWith("{\\rtf", StringComparison.Ordinal))
                text = StripRtf(text);
            return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        }
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
                    if (w == "tab") result.Append('\t');
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
}

public static class NoteColors
{
    public static readonly (string Name, string Hex, string Display)[] All =
    [
        ("Yellow", "#FFF9B1", "Jaune"),
        ("Green", "#E2F6D3", "Vert"),
        ("Mint", "#C8F7E1", "Menthe"),
        ("Teal", "#C8E6F0", "Turquoise"),
        ("Blue", "#D4E8FF", "Bleu"),
        ("Lavender", "#DDD4FF", "Lavande"),
        ("Purple", "#E0D4FF", "Violet"),
        ("Pink", "#FFD4E0", "Rose"),
        ("Coral", "#FFD0C0", "Corail"),
        ("Orange", "#FFE4B5", "Orange"),
        ("Peach", "#FFE8D0", "P\u00eache"),
        ("Sand", "#F0E6D3", "Sable"),
        ("Gray", "#F0F0F0", "Gris"),
        ("Charcoal", "#D8D8D8", "Anthracite"),
    ];

    public static Windows.UI.Color Get(string name)
    {
        var hex = All.FirstOrDefault(c => c.Name == name).Hex ?? "#FFF9B1";
        return ColorFromHex(hex);
    }

    public static Windows.UI.Color GetDarker(string name, double factor = 0.92)
    {
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
