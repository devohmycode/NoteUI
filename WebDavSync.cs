using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoteUI;

public class WebDavSync : IDisposable
{
    private HttpClient? _http;
    private string _baseUrl = "";

    public bool IsConfigured => _http != null && !string.IsNullOrEmpty(_baseUrl);
    public string? ServerUrl { get; private set; }

    public void Configure(string url, string username, string password)
    {
        _baseUrl = url.TrimEnd('/');
        ServerUrl = _baseUrl;

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password)
        };
        _http?.Dispose();
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        if (_http == null) return (false, "Non configur\u00e9");
        try
        {
            // PROPFIND to check if folder exists
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl + "/");
            request.Headers.Add("Depth", "0");
            var response = await _http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "Identifiants incorrects");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, "Dossier introuvable");
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
                return (true, null);

            return (false, $"Erreur {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<NoteEntry>?> PullNotesAsync()
    {
        if (_http == null) return null;
        try
        {
            var url = _baseUrl + "/notes.json";
            var response = await _http.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return [];

            return JsonSerializer.Deserialize<List<NoteEntry>>(json) ?? [];
        }
        catch { return null; }
    }

    public async Task<bool> PushNotesAsync(IReadOnlyList<NoteEntry> notes)
    {
        if (_http == null) return false;
        try
        {
            var url = _baseUrl + "/notes.json";
            var json = JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PutAsync(url, content);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
