using System.Text.Json;

namespace NoteUI;

public class SnippetManager
{
    private readonly List<SnippetEntry> _snippets = [];

    private static readonly string SaveDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
    private static readonly string SavePath = Path.Combine(SaveDir, "snippets.json");

    public IReadOnlyList<SnippetEntry> Snippets => _snippets;

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var json = File.ReadAllText(SavePath);
            var entries = JsonSerializer.Deserialize<List<SnippetEntry>>(json);
            if (entries != null)
            {
                _snippets.Clear();
                _snippets.AddRange(entries);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var json = JsonSerializer.Serialize(_snippets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch { }
    }

    public List<string> GetAllCategories()
    {
        return _snippets
            .Where(s => !string.IsNullOrEmpty(s.Category))
            .Select(s => s.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    public SnippetEntry AddSnippet(string noteId, string keyword, string prefix, string content, string category = "")
    {
        // Remove existing snippet for same noteId if any
        _snippets.RemoveAll(s => s.NoteId == noteId);

        var snippet = new SnippetEntry
        {
            Id = Guid.NewGuid().ToString(),
            NoteId = noteId,
            Keyword = keyword,
            Prefix = prefix,
            Content = content,
            Category = category,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        _snippets.Add(snippet);
        Save();
        return snippet;
    }

    public void RemoveSnippet(string noteId)
    {
        _snippets.RemoveAll(s => s.NoteId == noteId);
        Save();
    }

    public void UpdateContent(string noteId, string content)
    {
        var snippet = _snippets.FirstOrDefault(s => s.NoteId == noteId);
        if (snippet == null) return;
        snippet.Content = content;
        snippet.UpdatedAt = DateTime.Now;
        Save();
    }

    public SnippetEntry? FindByNoteId(string noteId)
    {
        return _snippets.FirstOrDefault(s => s.NoteId == noteId);
    }

    public SnippetEntry? FindByTrigger(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return null;
        return _snippets.FirstOrDefault(s =>
        {
            var trigger = string.IsNullOrEmpty(s.Prefix) ? s.Keyword : s.Prefix + s.Keyword;
            return trigger.Equals(typed, StringComparison.Ordinal);
        });
    }

    public bool IsKeywordTaken(string keyword, string prefix, string? excludeNoteId = null)
    {
        if (string.IsNullOrEmpty(keyword)) return false;
        return _snippets.Any(s =>
            s.Keyword.Equals(keyword, StringComparison.Ordinal) &&
            s.Prefix == prefix &&
            s.NoteId != excludeNoteId);
    }
}

public class SnippetEntry
{
    public string Id { get; set; } = "";
    public string NoteId { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string Trigger => string.IsNullOrEmpty(Prefix) ? Keyword : Prefix + Keyword;
}
