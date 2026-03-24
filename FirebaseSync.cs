using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoteUI;

/// <summary>RTDB tombstone under <c>users/{uid}/noteDeletions</c> (shared with web/Android).</summary>
public sealed class NoteDeletionEntry
{
    public string DeletedAt { get; set; } = "";
}

public class FirebaseSync : IDisposable
{
    private static readonly JsonSerializerOptions NotePullJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = new();
    private string _databaseUrl = "";
    private string _apiKey = "";
    private string? _idToken;
    private string? _localId;
    private string? _refreshToken;
    private Timer? _refreshTimer;

    public bool IsConfigured => !string.IsNullOrEmpty(_databaseUrl) && !string.IsNullOrEmpty(_apiKey);
    public bool IsConnected => _idToken != null;
    public string? Email { get; private set; }
    /// <summary>Firebase Auth local id (<c>users/{LocalUserId}/notes</c>).</summary>
    public string? LocalUserId => _localId;

    public void Configure(string databaseUrl, string apiKey)
    {
        _databaseUrl = databaseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    // ── Email/Password Auth ──────────────────────────────────────

    public async Task<(bool Success, string? Error)> SignUpAsync(string email, string password)
    {
        if (!IsConfigured) return (false, Lang.T("firebase_not_configured"));
        try
        {
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_apiKey}";
            var payload = new { email, password, returnSecureToken = true };
            var response = await _http.PostAsJsonAsync(url, payload);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (!response.IsSuccessStatusCode)
                return (false, ParseFirebaseError(json));

            ApplyAuthResponse(json);
            Email = email;
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Success, string? Error)> SignInAsync(string email, string password)
    {
        if (!IsConfigured) return (false, Lang.T("firebase_not_configured"));
        try
        {
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={_apiKey}";
            var payload = new { email, password, returnSecureToken = true };
            var response = await _http.PostAsJsonAsync(url, payload);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (!response.IsSuccessStatusCode)
                return (false, ParseFirebaseError(json));

            ApplyAuthResponse(json);
            Email = email;
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<bool> SignInWithRefreshTokenAsync(string refreshToken)
    {
        if (!IsConfigured) return false;
        try
        {
            var ok = await RefreshTokenAsync(refreshToken);
            if (!ok) return false;

            // Get user info to retrieve email
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={_apiKey}";
            var payload = new { idToken = _idToken };
            var response = await _http.PostAsJsonAsync(url, payload);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (json.TryGetProperty("users", out var users) && users.GetArrayLength() > 0)
                    Email = users[0].GetProperty("email").GetString();
            }
            return true;
        }
        catch { return false; }
    }

    public string? GetRefreshToken() => _refreshToken;

    private void ApplyAuthResponse(JsonElement json)
    {
        _idToken = json.GetProperty("idToken").GetString();
        _localId = json.GetProperty("localId").GetString();
        _refreshToken = json.GetProperty("refreshToken").GetString();
        var expiresIn = json.GetProperty("expiresIn").GetString();
        ScheduleTokenRefresh(expiresIn);
    }

    private void ScheduleTokenRefresh(string? expiresIn)
    {
        if (int.TryParse(expiresIn, out var seconds) && seconds > 120)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = new Timer(async _ => await RefreshTokenAsync(_refreshToken), null,
                TimeSpan.FromSeconds(seconds - 60), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task<bool> RefreshTokenAsync(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return false;
        try
        {
            var url = $"https://securetoken.googleapis.com/v1/token?key={_apiKey}";
            var payload = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            };
            var response = await _http.PostAsync(url, new FormUrlEncodedContent(payload));
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            _idToken = json.GetProperty("id_token").GetString();
            _localId = json.GetProperty("user_id").GetString();
            _refreshToken = json.GetProperty("refresh_token").GetString();
            var expiresIn = json.GetProperty("expires_in").GetString();
            ScheduleTokenRefresh(expiresIn);
            return true;
        }
        catch { return false; }
    }

    private static string ParseFirebaseError(JsonElement json)
    {
        try
        {
            var msg = json.GetProperty("error").GetProperty("message").GetString() ?? Lang.T("error");
            return msg switch
            {
                "EMAIL_EXISTS" => Lang.T("firebase_email_exists"),
                "EMAIL_NOT_FOUND" => Lang.T("firebase_email_not_found"),
                "INVALID_PASSWORD" => Lang.T("firebase_invalid_password"),
                var s when s.StartsWith("WEAK_PASSWORD") => Lang.T("firebase_weak_password"),
                "INVALID_EMAIL" => Lang.T("firebase_invalid_email"),
                "INVALID_LOGIN_CREDENTIALS" => Lang.T("firebase_invalid_credentials"),
                _ => msg
            };
        }
        catch { return Lang.T("firebase_unknown_error"); }
    }

    // ── Google Sign-In ─────────────────────────────────────────

    // Loaded from environment to avoid hardcoding OAuth credentials in git history.

    public async Task<(bool Success, string? Error)> SignInWithGoogleAsync()
    {
        if (!IsConfigured) return (false, Lang.T("firebase_not_configured"));
        if (!RuntimeSecrets.TryGetGoogleClientId(out var googleClientId))
            return (false, Lang.T("firebase_google_not_configured"));
        RuntimeSecrets.TryGetGoogleClientSecret(out var googleClientSecret);
        try
        {
            // Generate PKCE code verifier + challenge
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);
            var state = Guid.NewGuid().ToString("N");

            // Find a free port
            var listener = new HttpListener();
            var port = FindFreePort();
            var redirectUri = $"http://localhost:{port}/";
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            // Open browser
            var authUrl = "https://accounts.google.com/o/oauth2/v2/auth"
                + $"?client_id={googleClientId}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + "&response_type=code"
                + "&scope=openid%20email%20profile"
                + $"&state={state}"
                + $"&code_challenge={codeChallenge}"
                + "&code_challenge_method=S256"
                + "&access_type=offline";

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            // Wait for callback (timeout 2 min)
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(2)));

            if (completed != contextTask)
            {
                listener.Stop();
                return (false, "D\u00e9lai d\u00e9pass\u00e9");
            }

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];

            // Send success page
            var responseHtml = "<html><body style='font-family:Segoe UI;text-align:center;padding:60px'>"
                + "<h2>Connexion r\u00e9ussie</h2><p>Vous pouvez fermer cette fen\u00eatre.</p></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
            listener.Stop();

            if (string.IsNullOrEmpty(code) || returnedState != state)
                return (false, "Authentification annul\u00e9e");

            // Exchange code for tokens
            var tokenPayload = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = googleClientId,
                ["client_secret"] = googleClientSecret ?? "",
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            };
            var tokenResponse = await _http.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(tokenPayload));

            if (!tokenResponse.IsSuccessStatusCode)
            {
                var detail = await FormatGoogleOAuthTokenErrorAsync(tokenResponse);
                return (false,
                    string.IsNullOrEmpty(detail)
                        ? $"Erreur token Google: {tokenResponse.StatusCode}"
                        : $"Erreur token Google: {tokenResponse.StatusCode} — {detail}");
            }

            var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
            var googleIdToken = tokenJson.GetProperty("id_token").GetString();

            // Sign into Firebase with the Google ID token
            var firebaseUrl = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={_apiKey}";
            var firebasePayload = new
            {
                postBody = $"id_token={googleIdToken}&providerId=google.com",
                requestUri = redirectUri,
                returnSecureToken = true
            };
            var fbResponse = await _http.PostAsJsonAsync(firebaseUrl, firebasePayload);
            var fbJson = await fbResponse.Content.ReadFromJsonAsync<JsonElement>();

            if (!fbResponse.IsSuccessStatusCode)
                return (false, ParseFirebaseError(fbJson));

            ApplyAuthResponse(fbJson);
            if (fbJson.TryGetProperty("email", out var emailProp))
                Email = emailProp.GetString();

            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Google returns JSON: error, error_description (e.g. redirect_uri_mismatch).</summary>
    private static async Task<string> FormatGoogleOAuthTokenErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
                return "";

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (!string.IsNullOrEmpty(desc))
                return string.IsNullOrEmpty(err) ? desc : $"{err}: {desc}";
            if (!string.IsNullOrEmpty(err))
                return err;
            return text.Length > 200 ? text[..200] + "…" : text;
        }
        catch
        {
            return "";
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    // ── Sync ─────────────────────────────────────────────────────

    /// <summary>Matches web <c>mergePullWithLocal</c>: drops locals missing from RTDB if tombstoned or ever seen remotely.</summary>
    public static List<NoteEntry> MergePullWithLocal(
        IReadOnlyList<NoteEntry> localNotes,
        IReadOnlyList<NoteEntry> remoteNotes,
        HashSet<string> idsEverSeenOnRemote,
        HashSet<string>? tombstoneIds = null)
    {
        var tombstones = tombstoneIds ?? new HashSet<string>(StringComparer.Ordinal);
        var remoteMap = remoteNotes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var seenRemote = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<NoteEntry>();

        foreach (var local in localNotes)
        {
            if (remoteMap.TryGetValue(local.Id, out var r))
            {
                seenRemote.Add(local.Id);
                next.Add(MergeRemoteIntoLocal(local, r));
            }
            else
            {
                if (tombstones.Contains(local.Id) || idsEverSeenOnRemote.Contains(local.Id))
                    continue;
                next.Add(local);
            }
        }

        foreach (var r in remoteNotes)
        {
            if (seenRemote.Contains(r.Id))
                continue;
            next.Add(CloneNoteEntry(r));
        }

        foreach (var id in remoteMap.Keys)
            idsEverSeenOnRemote.Add(id);

        return next;
    }

    private static NoteEntry MergeRemoteIntoLocal(NoteEntry local, NoteEntry remote)
    {
        if (remote.UpdatedAt <= local.UpdatedAt)
            return local;
        return new NoteEntry
        {
            Id = local.Id,
            Title = remote.Title,
            Content = remote.Content,
            Color = remote.Color,
            IsPinned = remote.IsPinned,
            IsFavorite = remote.IsFavorite,
            IsArchived = remote.IsArchived,
            IsLocked = remote.IsLocked,
            Tags = [.. remote.Tags],
            ReminderAt = remote.ReminderAt,
            NoteType = remote.NoteType,
            Tasks = remote.Tasks.Select(t => new TaskItem
            {
                Id = t.Id,
                Text = t.Text,
                IsDone = t.IsDone,
                ReminderAt = t.ReminderAt
            }).ToList(),
            CreatedAt = remote.CreatedAt,
            UpdatedAt = remote.UpdatedAt,
            PosX = remote.PosX,
            PosY = remote.PosY,
            Width = remote.Width,
            Height = remote.Height,
            IsCompact = remote.IsCompact,
            AttachTarget = remote.AttachTarget,
            AttachMode = remote.AttachMode,
            AttachOffsetX = remote.AttachOffsetX,
            AttachOffsetY = remote.AttachOffsetY,
        };
    }

    private static NoteEntry CloneNoteEntry(NoteEntry n) => new()
    {
        Id = n.Id,
        Title = n.Title,
        Content = n.Content,
        Color = n.Color,
        IsPinned = n.IsPinned,
        IsFavorite = n.IsFavorite,
        IsArchived = n.IsArchived,
        IsLocked = n.IsLocked,
        Tags = [.. n.Tags],
        ReminderAt = n.ReminderAt,
        NoteType = n.NoteType,
        Tasks = n.Tasks.Select(t => new TaskItem
        {
            Id = t.Id,
            Text = t.Text,
            IsDone = t.IsDone,
            ReminderAt = t.ReminderAt
        }).ToList(),
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
        PosX = n.PosX,
        PosY = n.PosY,
        Width = n.Width,
        Height = n.Height,
        IsCompact = n.IsCompact,
        AttachTarget = n.AttachTarget,
        AttachMode = n.AttachMode,
        AttachOffsetX = n.AttachOffsetX,
        AttachOffsetY = n.AttachOffsetY,
    };

    /// <summary>
    /// Matches web <c>mergeLocalNotesWithRemoteForPush</c>: omits remote keys when the id was ever on RTDB but is absent locally (local delete).
    /// Remote-only ids not in <paramref name="idsEverSeenOnRemote"/> are kept (note created on another client).
    /// </summary>
    private static Dictionary<string, NoteEntry> MergeNotesForPush(
        IReadOnlyList<NoteEntry> local,
        IReadOnlyDictionary<string, NoteEntry> remote,
        HashSet<string> idsEverSeenOnRemote)
    {
        var localById = local.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var merged = new Dictionary<string, NoteEntry>(StringComparer.Ordinal);

        foreach (var (id, l) in localById)
        {
            if (!remote.TryGetValue(id, out var r))
            {
                merged[id] = l;
                continue;
            }
            merged[id] = l.UpdatedAt >= r.UpdatedAt ? l : r;
        }

        foreach (var (id, r) in remote)
        {
            if (localById.ContainsKey(id))
                continue;
            if (idsEverSeenOnRemote.Contains(id))
                continue;
            merged[id] = r;
        }

        return merged;
    }

    /// <summary>Builds the next <c>noteDeletions</c> map (matches web <c>mergeNoteDeletionsForPush</c>).</summary>
    public static Dictionary<string, NoteDeletionEntry> MergeNoteDeletionsForPush(
        Dictionary<string, NoteEntry> mergedNotes,
        IReadOnlyDictionary<string, NoteEntry> previousRemoteNotes,
        IReadOnlyDictionary<string, NoteDeletionEntry>? previousDeletions)
    {
        var prev = previousDeletions != null
            ? previousDeletions.ToDictionary(kvp => kvp.Key, kvp => new NoteDeletionEntry { DeletedAt = kvp.Value.DeletedAt }, StringComparer.Ordinal)
            : new Dictionary<string, NoteDeletionEntry>(StringComparer.Ordinal);
        var now = DateTime.UtcNow.ToString("o");
        foreach (var id in previousRemoteNotes.Keys)
        {
            if (!mergedNotes.ContainsKey(id))
                prev[id] = new NoteDeletionEntry { DeletedAt = now };
        }
        foreach (var id in mergedNotes.Keys)
            prev.Remove(id);
        return prev;
    }

    public async Task<Dictionary<string, NoteEntry>?> PullNotesDictionaryAsync()
    {
        if (!IsConnected || _localId == null) return null;
        try
        {
            var url = $"{_databaseUrl}/users/{_localId}/notes.json?auth={_idToken}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(content) || content == "null")
                return [];

            return JsonSerializer.Deserialize<Dictionary<string, NoteEntry>>(content, NotePullJsonOptions) ?? [];
        }
        catch { return null; }
    }

    public async Task<Dictionary<string, NoteDeletionEntry>?> PullNoteDeletionsAsync()
    {
        if (!IsConnected || _localId == null) return null;
        try
        {
            var url = $"{_databaseUrl}/users/{_localId}/noteDeletions.json?auth={_idToken}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(content) || content == "null")
                return [];

            return JsonSerializer.Deserialize<Dictionary<string, NoteDeletionEntry>>(content, NotePullJsonOptions) ?? [];
        }
        catch { return null; }
    }

    public async Task<List<NoteEntry>?> PullNotesAsync()
    {
        var dict = await PullNotesDictionaryAsync();
        return dict == null ? null : dict.Values.ToList();
    }

    /// <summary>
    /// Reads current cloud notes + deletions, merges with <paramref name="notes"/> (same rules as web), then PATCHes <c>users/{uid}</c> with <c>notes</c> and <c>noteDeletions</c>.
    /// </summary>
    public async Task<bool> PushNotesAsync(IReadOnlyList<NoteEntry> notes)
    {
        if (!IsConnected || _localId == null) return false;
        try
        {
            var url = $"{_databaseUrl}/users/{_localId}/notes.json?auth={_idToken}";
            using var getResponse = await _http.GetAsync(url);
            if (!getResponse.IsSuccessStatusCode)
                return false;

            var remoteBody = await getResponse.Content.ReadAsStringAsync();
            Dictionary<string, NoteEntry> remoteDict;
            if (string.IsNullOrEmpty(remoteBody) || remoteBody == "null")
                remoteDict = [];
            else
                remoteDict = JsonSerializer.Deserialize<Dictionary<string, NoteEntry>>(remoteBody, NotePullJsonOptions) ?? [];

            var everSeen = AppSettings.LoadFirebaseEverSeenNoteIds(_localId);
            var merged = MergeNotesForPush(notes, remoteDict, everSeen);
            foreach (var id in merged.Keys)
                everSeen.Add(id);
            AppSettings.SaveFirebaseEverSeenNoteIds(_localId, everSeen);

            Dictionary<string, NoteDeletionEntry> prevDeletions;
            var delUrl = $"{_databaseUrl}/users/{_localId}/noteDeletions.json?auth={_idToken}";
            using (var delResponse = await _http.GetAsync(delUrl))
            {
                var delBody = await delResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(delBody) || delBody == "null")
                    prevDeletions = [];
                else
                    prevDeletions = JsonSerializer.Deserialize<Dictionary<string, NoteDeletionEntry>>(delBody, NotePullJsonOptions) ?? [];
            }

            var nextDeletions = MergeNoteDeletionsForPush(merged, remoteDict, prevDeletions);

            var userUrl = $"{_databaseUrl}/users/{_localId}.json?auth={_idToken}";
            var patchPayload = new Dictionary<string, object>
            {
                ["notes"] = merged,
                ["noteDeletions"] = nextDeletions
            };
            var patchJson = JsonSerializer.Serialize(patchPayload);
            using var patchResponse = await _http.PatchAsync(userUrl, new StringContent(patchJson, Encoding.UTF8, "application/json"));
            return patchResponse.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Settings Sync ─────────────────────────────────────────

    public async Task<bool> PushSettingsAsync(Dictionary<string, object> settings)
    {
        if (!IsConnected || _localId == null) return false;
        try
        {
            var url = $"{_databaseUrl}/users/{_localId}/settings.json?auth={_idToken}";
            var json = JsonSerializer.Serialize(settings);
            var response = await _http.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<Dictionary<string, JsonElement>?> PullSettingsAsync()
    {
        if (!IsConnected || _localId == null) return null;
        try
        {
            var url = $"{_databaseUrl}/users/{_localId}/settings.json?auth={_idToken}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(content) || content == "null") return null;

            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _http.Dispose();
    }
}
