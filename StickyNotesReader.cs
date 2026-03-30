using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NoteUI;

/// <summary>
/// Reads Microsoft Sticky Notes (Pense-bêtes) from the local SQLite database.
/// Path: %LocalAppData%\Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite
/// </summary>
public static partial class StickyNotesReader
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite");

    /// <summary>Whether the Sticky Notes database exists on this machine.</summary>
    public static bool IsAvailable => File.Exists(DbPath);

    /// <summary>Read all active (non-deleted) Sticky Notes and convert to NoteEntry objects.</summary>
    public static List<NoteEntry> ReadAll()
    {
        var notes = new List<NoteEntry>();
        if (!IsAvailable) return notes;

        try
        {
            // Open read-only to avoid locking issues with the running Sticky Notes app
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Text, Theme, CreatedAt, UpdatedAt
                FROM Note
                WHERE (DeletedAt = 0 OR DeletedAt IS NULL)
                  AND Text IS NOT NULL AND Text != ''
                ORDER BY UpdatedAt DESC
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var rawText = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var theme = reader.IsDBNull(2) ? "Yellow" : reader.GetString(2);
                var createdTicks = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
                var updatedTicks = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);

                var cleanText = CleanStickyText(rawText);
                if (string.IsNullOrWhiteSpace(cleanText)) continue;

                notes.Add(new NoteEntry
                {
                    Id = $"sticky-{id}",
                    Title = "",
                    Content = cleanText,
                    Color = MapTheme(theme),
                    CreatedAt = TicksToDateTime(createdTicks),
                    UpdatedAt = TicksToDateTime(updatedTicks),
                    NoteType = "note",
                });
            }
        }
        catch
        {
            // Database may be locked by Sticky Notes app — return what we have
        }

        return notes;
    }

    /// <summary>Clean Sticky Notes raw text: strip paragraph IDs and RTF formatting codes.</summary>
    private static string CleanStickyText(string text)
    {
        // 1. Strip \id=... prefixes (handles guid, localId_hex, and any other variant)
        var cleaned = ParagraphIdRegex().Replace(text, "");

        // 2. Strip RTF-style inline formatting codes
        cleaned = RtfFormattingRegex().Replace(cleaned, "");

        // 3. Collapse multiple blank lines
        cleaned = MultipleNewlinesRegex().Replace(cleaned, "\n");

        return cleaned.Trim();
    }

    // Matches \id= followed by any non-whitespace characters, then optional trailing space
    [GeneratedRegex(@"\\id=\S+ ?")]
    private static partial Regex ParagraphIdRegex();

    // Matches RTF formatting codes: \b \b0 \i \i0 \ul \ulnone \strike \strike0 \super \sub \nosupersub \par \line \tab etc.
    [GeneratedRegex(@"\\(b0?|i0?|ul(none)?|strike0?|super|sub|nosupersub|par|line|tab|f[0-9]+|fs[0-9]+|cf[0-9]+|highlight[0-9]+|lang[0-9]+|ltrch|rtlch|ltrpar|rtlpar)\b\s?")]
    private static partial Regex RtfFormattingRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();

    /// <summary>Convert Windows Ticks (100ns since 1601-01-01) to DateTime.</summary>
    private static DateTime TicksToDateTime(long windowsTicks)
    {
        if (windowsTicks <= 0) return DateTime.Now;
        try
        {
            return DateTime.FromFileTime(windowsTicks);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>Map Sticky Notes theme names to NoteUI color names.</summary>
    private static string MapTheme(string theme) => theme switch
    {
        "Yellow" => "Yellow",
        "Green" => "Green",
        "Blue" => "Blue",
        "Purple" => "Purple",
        "Pink" => "Pink",
        "Charcoal" or "Black" => "Charcoal",
        "White" or "Gray" => "Gray",
        _ => "Yellow",
    };
}
