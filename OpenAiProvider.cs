using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NoteUI;

public class OpenAiProvider : ICloudAiProvider
{
    public string Id => "openai";
    public string Name => "OpenAI";

    public async Task<List<AiManager.ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        var response = await http.GetAsync("https://api.openai.com/v1/models", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var models = new List<AiManager.ModelInfo>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            if (id.StartsWith("gpt-") || id.StartsWith("o") || id.StartsWith("chatgpt-"))
                models.Add(new AiManager.ModelInfo(id, id));
        }
        return models.OrderBy(m => m.Id).ToList();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string apiKey, string model, string systemPrompt,
        List<AiManager.ChatMessage> history, string userMessage,
        float temperature, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        foreach (var msg in history)
            messages.Add(new { role = msg.Role, content = msg.Content });
        messages.Add(new { role = "user", content = userMessage });

        var body = JsonSerializer.Serialize(new
        {
            model,
            messages,
            temperature,
            max_tokens = maxTokens,
            stream = true,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
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
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;
            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var content))
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
    }
}
