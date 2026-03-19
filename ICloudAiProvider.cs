namespace NoteUI;

public interface ICloudAiProvider
{
    string Id { get; }
    string Name { get; }
    Task<List<AiManager.ModelInfo>> ListModelsAsync(string apiKey, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamChatAsync(
        string apiKey, string model, string systemPrompt,
        List<AiManager.ChatMessage> history, string userMessage,
        float temperature, int maxTokens,
        CancellationToken ct = default);
}
