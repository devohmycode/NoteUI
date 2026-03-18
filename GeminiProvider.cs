using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NoteUI;

public class GeminiProvider : ICloudAiProvider
{
    public string Id => "gemini";
    public string Name => "Google Gemini";

    public async Task<List<AiManager.ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var models = new List<AiManager.ModelInfo>();
        foreach (var item in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var displayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? name : name;
            if (item.TryGetProperty("supportedGenerationMethods", out var methods))
            {
                var supportsChat = false;
                foreach (var m in methods.EnumerateArray())
                    if (m.GetString() is "generateContent" or "streamGenerateContent")
                    { supportsChat = true; break; }
                if (!supportsChat) continue;
            }
            var id = name.StartsWith("models/") ? name["models/".Length..] : name;
            models.Add(new AiManager.ModelInfo(id, displayName));
        }
        return models.OrderBy(m => m.Name).ToList();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string apiKey, string model, string systemPrompt,
        List<AiManager.ChatMessage> history, string userMessage,
        float temperature, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        var contents = new List<object>();
        foreach (var msg in history)
        {
            var role = msg.Role == "assistant" ? "model" : "user";
            contents.Add(new { role, parts = new[] { new { text = msg.Content } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

        var payload = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new { temperature, maxOutputTokens = maxTokens },
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            payload["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };

        var body = JsonSerializer.Serialize(payload);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)) continue;
            if (candidates.GetArrayLength() == 0) continue;
            var candidate = candidates[0];
            if (!candidate.TryGetProperty("content", out var content)) continue;
            if (!content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                {
                    var s = text.GetString();
                    if (!string.IsNullOrEmpty(s))
                        yield return s;
                }
            }
        }
    }
}
