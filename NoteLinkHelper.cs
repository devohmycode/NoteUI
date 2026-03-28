using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace NoteUI;

/// <summary>
/// Repairs note link formatting that is lost during RTF round-trips
/// and provides shared link detection logic.
/// </summary>
internal static class NoteLinkHelper
{
    internal static readonly Windows.UI.Color LinkColor = Windows.UI.Color.FromArgb(255, 140, 200, 120);

    /// <summary>
    /// Scans the editor for note link IDs (GUID between zero-width spaces)
    /// and re-applies Hidden formatting, link color, and underline.
    /// Uses NotesManager to look up the title by GUID for robust title detection.
    /// </summary>
    internal static void RepairHiddenLinks(RichEditBox editor, NotesManager? notesManager = null)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        var len = text.TrimEnd('\r', '\n').Length;
        if (len == 0) return;

        int pos = 0;
        while (pos < len)
        {
            int start = text.IndexOf('\u200B', pos);
            if (start < 0) break;

            int end = text.IndexOf('\u200B', start + 1);
            if (end < 0) break;

            var between = text[(start + 1)..end];

            if (between.Length == 36 && Guid.TryParse(between, out _))
            {
                // Restore Hidden + link color on the GUID range
                var idRange = editor.Document.GetRange(start, end + 1);
                idRange.CharacterFormat.Hidden = FormatEffect.On;
                idRange.CharacterFormat.ForegroundColor = LinkColor;

                // Find the link title before the GUID
                int titleEnd = start;
                int titleStart = titleEnd;

                // Strategy 1: look up note title by GUID
                string? expectedTitle = null;
                if (notesManager != null)
                {
                    var note = notesManager.Notes.FirstOrDefault(n => n.Id == between);
                    if (note != null) expectedTitle = note.Title;
                }

                if (expectedTitle != null && titleEnd >= expectedTitle.Length)
                {
                    // Check if the text before the GUID matches the expected title
                    var candidate = text.Substring(titleEnd - expectedTitle.Length, expectedTitle.Length);
                    if (candidate == expectedTitle)
                        titleStart = titleEnd - expectedTitle.Length;
                }

                // Strategy 2: fallback — walk backwards while underlined
                if (titleStart == titleEnd)
                {
                    while (titleStart > 0)
                    {
                        var ch = editor.Document.GetRange(titleStart - 1, titleStart);
                        ch.GetText(TextGetOptions.None, out var c);
                        if (c == "\r" || c == "\n" || c == "\u200B") break;
                        if (ch.CharacterFormat.Underline == UnderlineType.None) break;
                        titleStart--;
                    }
                }

                if (titleStart < titleEnd)
                {
                    var titleRange = editor.Document.GetRange(titleStart, titleEnd);
                    titleRange.CharacterFormat.ForegroundColor = LinkColor;
                    titleRange.CharacterFormat.Underline = UnderlineType.Single;
                }
            }

            pos = end + 1;
        }
    }

    /// <summary>
    /// Detects if the cursor is on a note link and returns the target note ID.
    /// </summary>
    internal static string? DetectLinkAtCursor(RichEditBox editor, NotesManager notesManager)
    {
        var sel = editor.Document.Selection;
        if (sel == null) return null;

        var pos = sel.StartPosition;

        var fg = editor.Document.GetRange(pos, pos + 1).CharacterFormat.ForegroundColor;
        if (!IsLinkColor(fg) && pos > 0)
            fg = editor.Document.GetRange(pos - 1, pos).CharacterFormat.ForegroundColor;
        if (!IsLinkColor(fg)) return null;

        int end = pos;
        editor.Document.GetText(TextGetOptions.None, out var plainText);
        int textLen = plainText.TrimEnd('\r', '\n').Length;
        while (end < textLen)
        {
            var next = editor.Document.GetRange(end, end + 1);
            if (!IsLinkColor(next.CharacterFormat.ForegroundColor)) break;
            end++;
        }

        int start = pos > 0 ? pos - 1 : 0;
        while (start > 0)
        {
            var prev = editor.Document.GetRange(start - 1, start);
            prev.GetText(TextGetOptions.None, out var ch);
            if (ch == "\r" || ch == "\n" || !IsLinkColor(prev.CharacterFormat.ForegroundColor)) break;
            start--;
        }

        var linkRange = editor.Document.GetRange(start, end);
        linkRange.GetText(TextGetOptions.IncludeNumbering, out var visibleText);
        linkRange.GetText(TextGetOptions.None, out var fullText);
        visibleText = visibleText?.TrimEnd('\r', '\n') ?? "";
        fullText = fullText?.TrimEnd('\r', '\n') ?? "";

        string? noteId = null;
        if (fullText.Length > visibleText.Length && fullText.StartsWith(visibleText, StringComparison.Ordinal))
            noteId = fullText[visibleText.Length..].Trim('\u200B');

        if (string.IsNullOrEmpty(noteId))
        {
            var target = notesManager.GetByTitle(visibleText);
            if (target != null) noteId = target.Id;
        }

        return noteId;
    }

    internal static bool IsLinkColor(Windows.UI.Color c)
        => c.R == LinkColor.R && c.G == LinkColor.G && c.B == LinkColor.B;
}
