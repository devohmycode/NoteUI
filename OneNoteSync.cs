using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoteUI;

public class OneNoteSync : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string _clientId = "";
    private string? _accessToken;
    private string? _refreshToken;
    private string? _userName;
    private Timer? _refreshTimer;

    private string? _notebookId;
    private string? _sectionId;

    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string NotebookName = "NoteUI";
    private const string SectionName = "Notes";
    private const string MetaPrefix = "<!-- noteui-meta:";
    private const string MetaSuffix = ":noteui-meta -->";

    public bool IsConfigured => !string.IsNullOrEmpty(_clientId);
    public bool IsConnected => _accessToken != null;
    public string? UserName => _userName;

    public void Configure(string clientId)
    {
        _clientId = clientId;
    }

    public string? GetRefreshToken() => _refreshToken;

    // ── OAuth2 Interactive Sign-In (PKCE) ───────────────────────

    public async Task<(bool Success, string? Error)> SignInAsync()
    {
        if (!IsConfigured) return (false, Lang.T("onenote_not_configured"));
        try
        {
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);
            var state = Guid.NewGuid().ToString("N");

            var listener = new HttpListener();
            var port = FindFreePort();
            var redirectUri = $"http://localhost:{port}/";
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            var scopes = Uri.EscapeDataString("Notes.ReadWrite Mail.Read User.Read offline_access");
            var authUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
                + $"?client_id={_clientId}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + "&response_type=code"
                + $"&scope={scopes}"
                + $"&state={state}"
                + $"&code_challenge={codeChallenge}"
                + "&code_challenge_method=S256";

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(2)));

            if (completed != contextTask)
            {
                listener.Stop();
                return (false, Lang.T("timeout"));
            }

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var authError = query["error_description"];

            // Show success or error page depending on result
            var isSuccess = !string.IsNullOrEmpty(code) && returnedState == state;
            var responseHtml = isSuccess
                ? "<html><body style='font-family:Segoe UI;text-align:center;padding:60px'>"
                    + "<h2>" + Lang.T("onenote_auth_success") + "</h2>"
                    + "<p>" + Lang.T("close_window") + "</p></body></html>"
                : "<html><body style='font-family:Segoe UI;text-align:center;padding:60px'>"
                    + "<h2>" + Lang.T("auth_cancelled") + "</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
            listener.Stop();

            if (!isSuccess)
                return (false, authError ?? Lang.T("auth_cancelled"));

            // Exchange code for tokens
            var tokenPayload = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier,
                ["scope"] = "Notes.ReadWrite Mail.Read User.Read offline_access"
            };
            var tokenResp = await _http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(tokenPayload));

            if (!tokenResp.IsSuccessStatusCode)
            {
                var errBody = await tokenResp.Content.ReadAsStringAsync();
                return (false, $"Token error: {tokenResp.StatusCode}");
            }

            var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>();
            ApplyTokenResponse(tokenJson);

            // Fetch user display name
            await FetchUserName();

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Refresh Token Sign-In ───────────────────────────────────

    public async Task<bool> SignInWithRefreshTokenAsync(string refreshToken)
    {
        if (!IsConfigured || string.IsNullOrEmpty(refreshToken)) return false;
        try
        {
            _refreshToken = refreshToken;
            return await RefreshAccessTokenAsync();
        }
        catch { return false; }
    }

    private async Task<bool> RefreshAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken) || string.IsNullOrEmpty(_clientId))
            return false;
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["refresh_token"] = _refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = "Notes.ReadWrite Mail.Read User.Read offline_access"
            };
            var resp = await _http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(payload));

            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            ApplyTokenResponse(json);
            await FetchUserName();
            return true;
        }
        catch { return false; }
    }

    private void ApplyTokenResponse(JsonElement json)
    {
        _accessToken = json.GetProperty("access_token").GetString();
        if (json.TryGetProperty("refresh_token", out var rtProp))
            _refreshToken = rtProp.GetString();

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        var expiresIn = json.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        _refreshTimer?.Dispose();
        _refreshTimer = new Timer(async _ => await RefreshAccessTokenAsync(),
            null, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 120)), Timeout.InfiniteTimeSpan);
    }

    private async Task FetchUserName()
    {
        try
        {
            var resp = await _http.GetAsync($"{GraphBase}/me?$select=displayName,mail");
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                _userName = json.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (string.IsNullOrEmpty(_userName) && json.TryGetProperty("mail", out var m))
                    _userName = m.GetString();
            }
        }
        catch { }
    }

    // ── Notebook / Section management ───────────────────────────

    private async Task<bool> EnsureNotebookAndSection(CancellationToken ct = default)
    {
        if (_notebookId != null && _sectionId != null) return true;
        try
        {
            // Find notebook by listing all and matching client-side (OData $filter unreliable on OneNote API)
            var nbResp = await _http.GetAsync($"{GraphBase}/me/onenote/notebooks?$select=id,displayName&$top=50", ct);
            if (!nbResp.IsSuccessStatusCode) return false;
            var nbJson = await nbResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            string? foundNbId = null;
            foreach (var nb in nbJson.GetProperty("value").EnumerateArray())
            {
                var name = nb.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (string.Equals(name, NotebookName, StringComparison.OrdinalIgnoreCase))
                {
                    foundNbId = nb.GetProperty("id").GetString();
                    break;
                }
            }

            if (foundNbId != null)
            {
                _notebookId = foundNbId;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                var createResp = await _http.PostAsJsonAsync($"{GraphBase}/me/onenote/notebooks",
                    new { displayName = NotebookName }, ct);
                if (!createResp.IsSuccessStatusCode) return false;
                var created = await createResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                _notebookId = created.GetProperty("id").GetString();
            }

            ct.ThrowIfCancellationRequested();

            // Find section by listing all sections in notebook
            var secResp = await _http.GetAsync($"{GraphBase}/me/onenote/notebooks/{_notebookId}/sections?$select=id,displayName&$top=50", ct);
            if (!secResp.IsSuccessStatusCode) return false;
            var secJson = await secResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            string? foundSecId = null;
            foreach (var sec in secJson.GetProperty("value").EnumerateArray())
            {
                var name = sec.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (string.Equals(name, SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    foundSecId = sec.GetProperty("id").GetString();
                    break;
                }
            }

            if (foundSecId != null)
            {
                _sectionId = foundSecId;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                var createSec = await _http.PostAsJsonAsync($"{GraphBase}/me/onenote/notebooks/{_notebookId}/sections",
                    new { displayName = SectionName }, ct);
                if (!createSec.IsSuccessStatusCode) return false;
                var createdSec = await createSec.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                _sectionId = createdSec.GetProperty("id").GetString();
            }

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // ── Pull notes from OneNote ─────────────────────────────────

    public async Task<List<NoteEntry>?> PullNotesAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return null;

        try
        {
            if (!await EnsureNotebookAndSection(ct)) return [];
        }
        catch { return []; }

        try
        {
            if (ct.IsCancellationRequested) return null;
            return await PullOutlookStickyNotesAsync(ct);
        }
        catch { return ct.IsCancellationRequested ? null : []; }
    }

    private async Task<List<JsonElement>> ListPagesInSection(string sectionId, CancellationToken ct = default)
    {
        var pages = new List<JsonElement>();
        try
        {
            var url = $"{GraphBase}/me/onenote/sections/{sectionId}/pages?$select=id,title,lastModifiedDateTime,createdDateTime&$top=100&$orderby=lastModifiedDateTime desc";
            while (!string.IsNullOrEmpty(url))
            {
                ct.ThrowIfCancellationRequested();
                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) break;
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                foreach (var p in json.GetProperty("value").EnumerateArray())
                    pages.Add(p);
                url = json.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return pages;
    }

    private async Task<NoteEntry?> PullPageContent(JsonElement pageMeta, string pageId, CancellationToken ct = default)
    {
        try
        {
            // Retry once on failure (Graph API rate limiting)
            var resp = await _http.GetAsync($"{GraphBase}/me/onenote/pages/{pageId}/content", ct);
            if (!resp.IsSuccessStatusCode)
            {
                await Task.Delay(500, ct);
                resp = await _http.GetAsync($"{GraphBase}/me/onenote/pages/{pageId}/content", ct);
            }
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync();

            // Extract NoteUI metadata if present
            var meta = ExtractMeta(html);
            var title = pageMeta.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

            var createdAt = pageMeta.TryGetProperty("createdDateTime", out var ca)
                ? (DateTime.TryParse(ca.GetString(), out var cdt) ? cdt : DateTime.Now) : DateTime.Now;
            var updatedAt = pageMeta.TryGetProperty("lastModifiedDateTime", out var ua)
                ? (DateTime.TryParse(ua.GetString(), out var udt) ? udt : DateTime.Now) : DateTime.Now;

            // Extract body content (between <body> tags, strip metadata comment)
            var bodyContent = ExtractBodyText(html);

            var note = new NoteEntry
            {
                Id = meta?.Id ?? pageId,
                Title = title,
                Content = bodyContent,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                NoteType = meta?.NoteType ?? "note",
                Color = meta?.Color ?? "Yellow",
                IsPinned = meta?.IsPinned ?? false,
                IsFavorite = meta?.IsFavorite ?? false,
                IsArchived = meta?.IsArchived ?? false,
                Tags = meta?.Tags ?? [],
                Folder = meta?.Folder,
            };

            if (meta?.NoteType == "tasklist" && meta.Tasks != null)
                note.Tasks = meta.Tasks;

            return note;
        }
        catch { return null; }
    }

    // ── Pull Sticky Notes from Outlook ────────────────────────

    private async Task<List<NoteEntry>> PullOutlookStickyNotesAsync(CancellationToken ct = default)
    {
        var notes = new List<NoteEntry>();
        try
        {
            var url = $"{GraphBase}/me/mailFolders/notes/messages?$select=id,subject,body,categories,createdDateTime,lastModifiedDateTime&$top=100";

            while (!string.IsNullOrEmpty(url))
            {
                if (ct.IsCancellationRequested) return notes;
                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) break;
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

                foreach (var msg in json.GetProperty("value").EnumerateArray())
                {
                    var id = msg.GetProperty("id").GetString()!;
                    var subject = msg.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";

                    var content = "";
                    if (msg.TryGetProperty("body", out var body))
                    {
                        var bodyContent = body.TryGetProperty("content", out var bc) ? bc.GetString() ?? "" : "";
                        var bodyType = body.TryGetProperty("contentType", out var bt) ? bt.GetString() : "text";
                        content = bodyType == "html" ? StripHtmlToPlain(bodyContent) : bodyContent;
                    }

                    if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(subject))
                        continue;

                    var createdAt = msg.TryGetProperty("createdDateTime", out var ca)
                        ? (DateTime.TryParse(ca.GetString(), out var cdt) ? cdt : DateTime.Now) : DateTime.Now;
                    var updatedAt = msg.TryGetProperty("lastModifiedDateTime", out var ua)
                        ? (DateTime.TryParse(ua.GetString(), out var udt) ? udt : DateTime.Now) : DateTime.Now;

                    var color = "Yellow";

                    notes.Add(new NoteEntry
                    {
                        Id = $"outlook-sticky-{id}",
                        Title = "",
                        Content = content.Trim(),
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                        NoteType = "note",
                        Color = color,
                    });
                }

                url = json.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }

            // Apply colors from local Sticky Notes database (the only reliable source)
            ApplyColorsFromLocalStickyNotes(notes);

            return notes;
        }
        catch
        {
            return notes;
        }
    }

    private static void ApplyColorsFromLocalStickyNotes(List<NoteEntry> outlookNotes)
    {
        if (!StickyNotesReader.IsAvailable || outlookNotes.Count == 0) return;
        try
        {
            var localNotes = StickyNotesReader.ReadAll();
            if (localNotes.Count == 0) return;

            // Build a map of normalized content → color from local notes
            var colorMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var local in localNotes)
            {
                var key = NormalizeForDedup(local.Content);
                if (!string.IsNullOrWhiteSpace(key) && !colorMap.ContainsKey(key))
                    colorMap[key] = local.Color;
            }

            // Apply colors to Outlook notes by matching content
            foreach (var note in outlookNotes)
            {
                var key = NormalizeForDedup(note.Content);
                if (!string.IsNullOrWhiteSpace(key) && colorMap.TryGetValue(key, out var localColor))
                    note.Color = localColor;
            }
        }
        catch { }
    }

    private static string StripHtmlToPlain(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p>\s*<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(div|p|li|tr|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "", RegexOptions.IgnoreCase);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    // ── Push notes to OneNote ───────────────────────────────────

    public async Task<bool> PushNotesAsync(IReadOnlyList<NoteEntry> notes)
    {
        if (!IsConnected) return false;
        if (!await EnsureNotebookAndSection()) return false;
        try
        {
            // Get existing pages to map NoteUI IDs → OneNote page IDs
            var existingPages = await GetExistingPageMap();

            foreach (var note in notes)
            {
                var html = BuildPageHtml(note);

                if (existingPages.TryGetValue(note.Id, out var pageId))
                {
                    // Update existing page — PATCH replaces body content
                    await UpdatePage(pageId, note, html);
                }
                else
                {
                    // Create new page
                    await CreatePage(html);
                }
            }

            return true;
        }
        catch { return false; }
    }

    private async Task<Dictionary<string, string>> GetExistingPageMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var url = $"{GraphBase}/me/onenote/sections/{_sectionId}/pages?$select=id,title&$top=100";

        while (!string.IsNullOrEmpty(url))
        {
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) break;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

            foreach (var p in json.GetProperty("value").EnumerateArray())
            {
                var pageId = p.GetProperty("id").GetString()!;
                // Need to fetch content to read meta — but that's too slow for mapping
                // Instead, fetch content for each to find the NoteUI ID
                var contentResp = await _http.GetAsync($"{GraphBase}/me/onenote/pages/{pageId}/content");
                if (contentResp.IsSuccessStatusCode)
                {
                    var html = await contentResp.Content.ReadAsStringAsync();
                    var meta = ExtractMeta(html);
                    if (meta?.Id != null)
                        map[meta.Id] = pageId;
                }
            }

            url = json.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return map;
    }

    private async Task<bool> CreatePage(string html)
    {
        var content = new StringContent(html, Encoding.UTF8, "text/html");
        var resp = await _http.PostAsync($"{GraphBase}/me/onenote/sections/{_sectionId}/pages", content);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> UpdatePage(string pageId, NoteEntry note, string fullHtml)
    {
        // OneNote PATCH API uses JSON patch commands targeting element IDs
        // Simpler approach: delete and recreate
        var delResp = await _http.DeleteAsync($"{GraphBase}/me/onenote/pages/{pageId}");
        if (!delResp.IsSuccessStatusCode && delResp.StatusCode != HttpStatusCode.NotFound)
            return false;

        // Small delay to let OneNote process the deletion
        await Task.Delay(200);
        return await CreatePage(fullHtml);
    }

    // ── HTML builders ───────────────────────────────────────────

    private static string BuildPageHtml(NoteEntry note)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append($"<title>{EscapeHtml(note.Title)}</title>");
        sb.Append("<meta name=\"created\" content=\"");
        sb.Append(note.CreatedAt.ToUniversalTime().ToString("O"));
        sb.Append("\" />");
        sb.Append("</head><body>");

        // Embed NoteUI metadata as hidden comment
        sb.Append(BuildMetaComment(note));

        // Content
        if (note.NoteType == "tasklist")
        {
            foreach (var task in note.Tasks)
            {
                var check = task.IsDone ? "&#9745;" : "&#9744;";
                sb.Append($"<p data-tag=\"to-do\">{check} {EscapeHtml(task.Text)}</p>");
            }
        }
        else
        {
            var content = note.Content;
            if (content.StartsWith("{\\rtf", StringComparison.Ordinal))
                content = RtfToSimpleHtml(content);
            else
                content = PlainTextToHtml(content);
            sb.Append(content);
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string BuildMetaComment(NoteEntry note)
    {
        var meta = new OneNoteMetadata
        {
            Id = note.Id,
            NoteType = note.NoteType,
            Color = note.Color,
            IsPinned = note.IsPinned,
            IsFavorite = note.IsFavorite,
            IsArchived = note.IsArchived,
            Tags = note.Tags.Count > 0 ? note.Tags : null,
            Folder = note.Folder,
            Tasks = note.NoteType == "tasklist" ? note.Tasks : null,
        };
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return $"{MetaPrefix}{json}{MetaSuffix}";
    }

    private static OneNoteMetadata? ExtractMeta(string html)
    {
        var start = html.IndexOf(MetaPrefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += MetaPrefix.Length;
        var end = html.IndexOf(MetaSuffix, start, StringComparison.Ordinal);
        if (end < 0) return null;
        var json = html[start..end];
        try
        {
            return JsonSerializer.Deserialize<OneNoteMetadata>(json, JsonOptions);
        }
        catch { return null; }
    }

    // ── RTF → HTML (basic) ──────────────────────────────────────

    private static string RtfToSimpleHtml(string rtf)
    {
        // Simple RTF to HTML: strip RTF control words, keep text
        var sb = new StringBuilder();
        sb.Append("<div>");

        bool inGroup = false;
        int depth = 0;
        int i = 0;
        var textBuf = new StringBuilder();

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
                    // Hex-encoded character
                    if (i + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 1, 2),
                        System.Globalization.NumberStyles.HexNumber, null, out var b))
                    {
                        textBuf.Append((char)b);
                        i += 3;
                    }
                    else i++;
                }
                else if (rtf[i] == '\n' || rtf[i] == '\r') { i++; }
                else
                {
                    // Read control word
                    var wordStart = i;
                    while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                    var word = rtf[wordStart..i];

                    // Skip optional numeric parameter
                    while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                    if (i < rtf.Length && rtf[i] == ' ') i++;

                    if (word is "par" or "line")
                    {
                        if (textBuf.Length > 0)
                        {
                            sb.Append($"<p>{EscapeHtml(textBuf.ToString())}</p>");
                            textBuf.Clear();
                        }
                    }
                    else if (word is "tab")
                    {
                        textBuf.Append('\t');
                    }
                    // Skip other control words (fonttbl, colortbl, etc.)
                }
            }
            else if (c == '\r' || c == '\n')
            {
                i++;
            }
            else
            {
                textBuf.Append(c);
                i++;
            }
        }

        if (textBuf.Length > 0)
            sb.Append($"<p>{EscapeHtml(textBuf.ToString())}</p>");

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string PlainTextToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<p></p>";
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append($"<p>{EscapeHtml(line.TrimEnd('\r'))}</p>");
        return sb.ToString();
    }

    private static string ExtractBodyText(string html)
    {
        // Remove the metadata comment
        var cleaned = html;
        var metaStart = cleaned.IndexOf(MetaPrefix, StringComparison.Ordinal);
        if (metaStart >= 0)
        {
            var metaEnd = cleaned.IndexOf(MetaSuffix, metaStart, StringComparison.Ordinal);
            if (metaEnd >= 0)
                cleaned = cleaned[..metaStart] + cleaned[(metaEnd + MetaSuffix.Length)..];
        }

        // Extract text between body tags
        var bodyStart = cleaned.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            var bodyTagEnd = cleaned.IndexOf('>', bodyStart);
            if (bodyTagEnd >= 0) cleaned = cleaned[(bodyTagEnd + 1)..];
        }
        var bodyEnd = cleaned.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd >= 0) cleaned = cleaned[..bodyEnd];

        // Convert HTML to plain text
        cleaned = Regex.Replace(cleaned, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"</p>\s*<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<p[^>]*>", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"</p>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", "", RegexOptions.IgnoreCase);
        cleaned = WebUtility.HtmlDecode(cleaned);
        return cleaned.Trim();
    }

    private static string EscapeHtml(string text)
    {
        return WebUtility.HtmlEncode(text);
    }

    // ── Merge logic ─────────────────────────────────────────────

    public static List<NoteEntry> MergeWithLocal(
        IReadOnlyList<NoteEntry> local, IReadOnlyList<NoteEntry> remote)
    {
        var merged = new Dictionary<string, NoteEntry>(StringComparer.Ordinal);

        foreach (var n in local)
            merged[n.Id] = n;

        foreach (var n in remote)
        {
            if (!merged.TryGetValue(n.Id, out var existing)
                || n.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                merged[n.Id] = n;
        }

        // Deduplicate notes with same content from different sources
        // (e.g. local Sticky Notes vs Outlook Sticky Notes)
        return DeduplicateByContent(merged.Values.ToList());
    }

    private static List<NoteEntry> DeduplicateByContent(List<NoteEntry> notes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<NoteEntry>();

        foreach (var note in notes.OrderBy(n => n.Id.StartsWith("sticky-") || n.Id.StartsWith("outlook-sticky-") ? 1 : 0))
        {
            var key = NormalizeForDedup(note.Content);
            if (string.IsNullOrWhiteSpace(key) || seen.Add(key))
                result.Add(note);
        }

        return result;
    }

    private static string NormalizeForDedup(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Trim, collapse whitespace, take first 200 chars for comparison
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length > 200 ? normalized[..200] : normalized;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _http.Dispose();
    }

    // ── Metadata model ──────────────────────────────────────────

    private class OneNoteMetadata
    {
        public string? Id { get; set; }
        public string? NoteType { get; set; }
        public string? Color { get; set; }
        public bool IsPinned { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsArchived { get; set; }
        public List<string>? Tags { get; set; }
        public string? Folder { get; set; }
        public List<TaskItem>? Tasks { get; set; }
    }
}
