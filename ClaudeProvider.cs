using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NoteUI;

public class ClaudeProvider : ICloudAiProvider
{
    public string Id => "claude";
    public string Name => "Claude";

    public async Task<List<AiManager.ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        var response = await http.GetAsync("https://api.anthropic.com/v1/models", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var models = new List<AiManager.ModelInfo>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? id : id;
            models.Add(new AiManager.ModelInfo(id, name));
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
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var messages = new List<object>();
        foreach (var msg in history)
            messages.Add(new { role = msg.Role, content = msg.Content });
        messages.Add(new { role = "user", content = userMessage });

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
        };
        if (temperature > 0) payload["temperature"] = temperature;
        if (!string.IsNullOrWhiteSpace(systemPrompt)) payload["system"] = systemPrompt;

        var body = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
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
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";

            if (type == "content_block_delta")
            {
                var delta = doc.RootElement.GetProperty("delta");
                if (delta.TryGetProperty("text", out var text))
                {
                    var s = text.GetString();
                    if (!string.IsNullOrEmpty(s))
                        yield return s;
                }
            }
            else if (type == "message_stop")
                break;
        }
    }
}
