using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace NoteUI;

public class AiManager
{
    // ── Data models ──

    public record ModelInfo(string Id, string Name);
    public record ChatMessage(string Role, string Content, DateTime Timestamp);

    public record LocalModel(string Name, string Repo, string FileName, string Size, bool IsPredefined = true)
    {
        public string DownloadUrl => $"https://huggingface.co/{Repo}/resolve/main/{FileName}";
        public string LocalPath => Path.Combine(ModelsDir, FileName);
        public bool IsInstalled => File.Exists(LocalPath);
    }

    public class AiSettings
    {
        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 2048;
        public int ContextSize { get; set; } = 2048;
        public int GpuLayers { get; set; } = 20;
        public string SystemPrompt { get; set; } = "Tu es un assistant utile et concis.";
        public string LastProviderId { get; set; } = "";
        public string LastModelId { get; set; } = "";
        public string LastLocalModelFileName { get; set; } = "";
    }

    // ── Paths ──

    private static readonly string BaseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
    public static readonly string ModelsDir = Path.Combine(BaseDir, "ai_models");
    private static readonly string SettingsPath = Path.Combine(BaseDir, "ai_settings.json");
    private static readonly string KeysPath = Path.Combine(BaseDir, "ai_keys.dat");

    // ── State ──

    public AiSettings Settings { get; private set; } = new();
    private Dictionary<string, string> _apiKeys = new();

    // Local model state
    private LLamaWeights? _model;
    private ModelParams? _modelParams;
    private string? _loadedModelPath;
    private CancellationTokenSource? _inferenceCts;

    // ── Providers ──

    public static readonly ICloudAiProvider[] Providers =
    [
        new OpenAiProvider(),
        new ClaudeProvider(),
        new GeminiProvider(),
    ];

    // ── Predefined local models ──

    public static readonly LocalModel[] PredefinedModels =
    [
        new("Gemma 2 2B",      "bartowski/gemma-2-2b-it-GGUF",                     "gemma-2-2b-it-Q4_K_M.gguf",                 "~1.6 GB"),
        new("Phi-3 Mini 3.8B", "microsoft/Phi-3-mini-4k-instruct-gguf",            "Phi-3-mini-4k-instruct-q4.gguf",            "~2.4 GB"),
        new("Llama 3.2 3B",    "bartowski/Llama-3.2-3B-Instruct-GGUF",             "Llama-3.2-3B-Instruct-Q4_K_M.gguf",         "~2 GB"),
        new("Mistral 7B",      "TheBloke/Mistral-7B-Instruct-v0.2-GGUF",           "mistral-7b-instruct-v0.2.Q4_K_M.gguf",      "~4.4 GB"),
        new("Llama 3.1 8B",    "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF",        "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",    "~4.9 GB"),
    ];

    // ── Init ──

    public void Load()
    {
        Directory.CreateDirectory(ModelsDir);
        LoadSettings();
        LoadApiKeys();
    }

    // ── Settings persistence ──

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonSerializer.Deserialize<AiSettings>(json) ?? new();
            }
        }
        catch { Settings = new(); }
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    // ── API Keys (DPAPI) ──

    public string GetApiKey(string providerId) =>
        _apiKeys.TryGetValue(providerId, out var key) ? key : "";

    public void SetApiKey(string providerId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            _apiKeys.Remove(providerId);
        else
            _apiKeys[providerId] = key;
        SaveApiKeys();
    }

    public bool HasApiKey(string providerId) =>
        _apiKeys.TryGetValue(providerId, out var k) && !string.IsNullOrWhiteSpace(k);

    private void LoadApiKeys()
    {
        try
        {
            if (!File.Exists(KeysPath)) return;
            var encrypted = File.ReadAllBytes(KeysPath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            _apiKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { _apiKeys = new(); }
    }

    private void SaveApiKeys()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var json = JsonSerializer.Serialize(_apiKeys);
            var bytes = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeysPath, encrypted);
        }
        catch { }
    }

    // ── Provider helpers ──

    public ICloudAiProvider? GetProvider(string id) =>
        Providers.FirstOrDefault(p => p.Id == id);

    // ── Local model management ──

    public List<LocalModel> GetInstalledModels()
    {
        if (!Directory.Exists(ModelsDir)) return [];
        var files = Directory.GetFiles(ModelsDir, "*.gguf");
        var installed = new List<LocalModel>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var predefined = PredefinedModels.FirstOrDefault(m => m.FileName == fileName);
            if (predefined != null)
                installed.Add(predefined);
            else
            {
                var sizeMb = new FileInfo(file).Length / (1024.0 * 1024.0);
                var sizeStr = sizeMb > 1024 ? $"~{sizeMb / 1024:F1} GB" : $"~{sizeMb:F0} MB";
                installed.Add(new LocalModel(fileName, "", fileName, sizeStr, false));
            }
        }
        return installed;
    }

    public Task DownloadModelAsync(LocalModel model, IProgress<(long downloaded, long? total)> progress, CancellationToken ct)
        => DownloadFileAsync(model.DownloadUrl, model.LocalPath, progress, ct);

    public Task DownloadFromUrlAsync(string url, string fileName, IProgress<(long downloaded, long? total)> progress, CancellationToken ct)
        => DownloadFileAsync(url, Path.Combine(ModelsDir, fileName), progress, ct);

    private static async Task DownloadFileAsync(string url, string destPath, IProgress<(long downloaded, long? total)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tempPath = destPath + ".tmp";

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromHours(2);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTime.UtcNow;
        while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 50)
            {
                lastReport = now;
                progress.Report((totalRead, totalBytes));
            }
        }

        // Final report to ensure 100%
        progress.Report((totalRead, totalBytes));

        fileStream.Close();
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);
    }

    public void DeleteModel(string fileName)
    {
        var path = Path.Combine(ModelsDir, fileName);
        if (_loadedModelPath == path) UnloadModel();
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Local inference ──

    public async Task LoadModelAsync(string fileName)
    {
        var path = Path.Combine(ModelsDir, fileName);
        if (_loadedModelPath == path && _model != null) return;

        UnloadModel();

        await Task.Run(() =>
        {
            var gpuLayers = Settings.GpuLayers;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var parameters = new ModelParams(path)
                    {
                        ContextSize = (uint)Settings.ContextSize,
                        GpuLayerCount = attempt == 0 ? gpuLayers : 0,
                    };
                    _model = LLamaWeights.LoadFromFile(parameters);
                    _modelParams = parameters;
                    _loadedModelPath = path;
                    return;
                }
                catch when (attempt == 0 && gpuLayers > 0)
                {
                    _model?.Dispose();
                    _model = null;
                }
            }
        });
    }

    public async IAsyncEnumerable<string> ChatLocalAsync(string userMessage, List<ChatMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_model == null || _modelParams == null) yield break;

        var template = new LLamaTemplate(_model);
        template.Add("system", Settings.SystemPrompt);
        foreach (var msg in history)
            template.Add(msg.Role, msg.Content);
        template.Add("user", userMessage);

        var prompt = Encoding.UTF8.GetString(template.Apply().ToArray());

        var inferenceParams = new InferenceParams
        {
            MaxTokens = Settings.MaxTokens,
            AntiPrompts = [
                "<|eot_id|>", "<|start_header_id|>",
                "<|end|>", "<|assistant|>", "<|user|>", "<|system|>",
                "[/INST]", "</s>",
                "<|im_end|>", "<|endoftext|>",
                "<end_of_turn>",
            ],
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = Settings.Temperature },
        };

        _inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var context = _model.CreateContext(_modelParams);
        var executor = new StatelessExecutor(_model, _modelParams);

        await foreach (var token in executor.InferAsync(prompt, inferenceParams, _inferenceCts.Token))
        {
            yield return token;
        }
    }

    public void StopInference() => _inferenceCts?.Cancel();

    public void UnloadModel()
    {
        _model?.Dispose();
        _model = null;
        _modelParams = null;
        _loadedModelPath = null;
    }

    public bool IsModelLoaded => _model != null;
    public string? LoadedModelFileName => _loadedModelPath != null ? Path.GetFileName(_loadedModelPath) : null;
}
