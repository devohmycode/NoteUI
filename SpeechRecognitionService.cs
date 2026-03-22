using System.Text.Json;
using NAudio.Wave;
using Vosk;
using Whisper.net;

namespace NoteUI;

// ── Model definitions ────────────────────────────────────────────

public enum SttEngine { Vosk, Whisper, GroqCloud }

public class SttModelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SttEngine Engine { get; init; }
    public required string DownloadUrl { get; init; }
    public required long SizeMB { get; init; }
    public required string Languages { get; init; }
    public string? WhisperLanguage { get; init; }

    public string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteUI", "models", Id);

    public bool IsDownloaded => Engine switch
    {
        SttEngine.Vosk => Directory.Exists(ModelDir) && Directory.GetFiles(ModelDir).Length > 0,
        SttEngine.GroqCloud => true, // cloud model, no download needed
        _ => File.Exists(Path.Combine(ModelDir, $"{Id}.bin")),
    };
}

public static class SttModels
{
    public static readonly SttModelInfo[] Available =
    [
        // English
        new()
        {
            Id = "vosk-model-small-en-us-0.15",
            Name = "Vosk English (small)",
            Engine = SttEngine.Vosk,
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
            SizeMB = 40,
            Languages = "Anglais"
        },
        new()
        {
            Id = "ggml-tiny.en",
            Name = "Whisper tiny (EN)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
            SizeMB = 75,
            Languages = "Anglais",
            WhisperLanguage = "en"
        },
        new()
        {
            Id = "ggml-base.en",
            Name = "Whisper base (EN)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
            SizeMB = 140,
            Languages = "Anglais",
            WhisperLanguage = "en"
        },
        new()
        {
            Id = "ggml-small.en",
            Name = "Whisper small (EN)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
            SizeMB = 460,
            Languages = "Anglais",
            WhisperLanguage = "en"
        },
        // Français
        new()
        {
            Id = "vosk-model-small-fr-0.22",
            Name = "Vosk Fran\u00e7ais (small)",
            Engine = SttEngine.Vosk,
            DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-fr-0.22.zip",
            SizeMB = 40,
            Languages = "Fran\u00e7ais"
        },
        new()
        {
            Id = "ggml-tiny",
            Name = "Whisper tiny (FR)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
            SizeMB = 75,
            Languages = "Fran\u00e7ais",
            WhisperLanguage = "fr"
        },
        new()
        {
            Id = "ggml-base",
            Name = "Whisper base (FR)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            SizeMB = 140,
            Languages = "Fran\u00e7ais",
            WhisperLanguage = "fr"
        },
        new()
        {
            Id = "ggml-small",
            Name = "Whisper small (FR)",
            Engine = SttEngine.Whisper,
            DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            SizeMB = 460,
            Languages = "Fran\u00e7ais",
            WhisperLanguage = "fr"
        },
        // Groq Cloud
        new()
        {
            Id = "whisper-large-v3",
            Name = "Whisper Large v3 (Groq)",
            Engine = SttEngine.GroqCloud,
            DownloadUrl = "",
            SizeMB = 0,
            Languages = "Multi",
            WhisperLanguage = "auto"
        },
        new()
        {
            Id = "whisper-large-v3-turbo",
            Name = "Whisper Large v3 Turbo (Groq)",
            Engine = SttEngine.GroqCloud,
            DownloadUrl = "",
            SizeMB = 0,
            Languages = "Multi",
            WhisperLanguage = "auto"
        },
    ];
}

// ── Model downloader ─────────────────────────────────────────────

public static class ModelDownloader
{
    public static async Task DownloadAsync(SttModelInfo model, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(model.ModelDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var response = await http.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? model.SizeMB * 1024 * 1024;
        var lastProgressReport = DateTime.UtcNow;

        void ReportProgress(long downloaded)
        {
            var now = DateTime.UtcNow;
            if ((now - lastProgressReport).TotalMilliseconds < 50) return;
            lastProgressReport = now;
            progress.Report((double)downloaded / totalBytes);
        }

        if (model.Engine == SttEngine.Vosk)
        {
            var zipPath = Path.Combine(model.ModelDir, "model.zip");
            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    ReportProgress(downloaded);
                }
            }

            progress.Report(-1);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, model.ModelDir, overwriteFiles: true);

            var subdirs = Directory.GetDirectories(model.ModelDir);
            if (subdirs.Length == 1)
            {
                foreach (var f in Directory.GetFiles(subdirs[0]))
                    File.Move(f, Path.Combine(model.ModelDir, Path.GetFileName(f)), overwrite: true);
                foreach (var d in Directory.GetDirectories(subdirs[0]))
                {
                    var dest = Path.Combine(model.ModelDir, Path.GetFileName(d));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(d, dest);
                }
                Directory.Delete(subdirs[0], true);
            }

            File.Delete(zipPath);
        }
        else
        {
            var binPath = Path.Combine(model.ModelDir, $"{model.Id}.bin");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(binPath);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                ReportProgress(downloaded);
            }
        }

        progress.Report(1.0);
    }
}

// ── Speech recognition interface ─────────────────────────────────

public interface ISpeechRecognizer : IDisposable
{
    event Action<string>? OnPartialResult;
    event Action<string>? OnFinalResult;
    event Action<float>? OnAudioLevel;
    void Start();
    void Stop();
}

// ── Vosk implementation ──────────────────────────────────────────

public class VoskRecognizer : ISpeechRecognizer
{
    public event Action<string>? OnPartialResult;
    public event Action<string>? OnFinalResult;
    public event Action<float>? OnAudioLevel;

    private readonly WaveInEvent _waveIn;
    private readonly Vosk.VoskRecognizer _recognizer;
    private readonly Model _model;
    private volatile bool _disposed;

    public VoskRecognizer(string modelPath)
    {
        _model = new Model(modelPath);
        _recognizer = new Vosk.VoskRecognizer(_model, 16000.0f);
        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(true);

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public void Start() => _waveIn.StartRecording();
    public void Stop() => _waveIn.StopRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;

        float sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = sample / 32768f;
            sum += normalized * normalized;
        }
        var rms = MathF.Sqrt(sum / (e.BytesRecorded / 2f));
        OnAudioLevel?.Invoke(rms);

        if (_disposed) return;

        if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
        {
            var result = _recognizer.Result();
            var text = ExtractText(result);
            if (!string.IsNullOrWhiteSpace(text))
                OnFinalResult?.Invoke(text);
        }
        else
        {
            var partial = _recognizer.PartialResult();
            var text = ExtractPartialText(partial);
            if (!string.IsNullOrWhiteSpace(text))
                OnPartialResult?.Invoke(text);
        }
    }

    private static string ExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("text").GetString() ?? "";
        }
        catch { return ""; }
    }

    private static string ExtractPartialText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("partial").GetString() ?? "";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _recognizer.Dispose();
        _model.Dispose();
    }
}

// ── Whisper implementation ───────────────────────────────────────

public class WhisperRecognizer : ISpeechRecognizer
{
    public event Action<string>? OnPartialResult;
    public event Action<string>? OnFinalResult;
    public event Action<float>? OnAudioLevel;

    private readonly WaveInEvent _waveIn;
    private readonly WhisperProcessor _processor;
    private readonly List<byte> _audioBuffer = [];
    private readonly object _bufferLock = new();
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private volatile bool _disposed;
    private const int ChunkSizeBytes = 16000 * 2 * 3; // 3 seconds

    public WhisperRecognizer(string modelPath, string language = "auto")
    {
        var factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder();
        if (language != "auto")
            builder.WithLanguage(language);
        _processor = builder.Build();

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _waveIn.StartRecording();
        _processingTask = ProcessLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _disposed = true;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _cts?.Cancel();
        // Don't block UI thread — let processing task finish asynchronously
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;

        float sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = sample / 32768f;
            sum += normalized * normalized;
        }
        var rms = MathF.Sqrt(sum / (e.BytesRecorded / 2f));
        OnAudioLevel?.Invoke(rms);

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded));
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            byte[] chunk;
            lock (_bufferLock)
            {
                if (_audioBuffer.Count < ChunkSizeBytes) continue;
                chunk = _audioBuffer.ToArray();
                _audioBuffer.Clear();
            }

            OnPartialResult?.Invoke("...");

            var samples = ConvertToFloat(chunk);
            try
            {
                var sb = new System.Text.StringBuilder();
                await foreach (var segment in _processor.ProcessAsync(samples, ct))
                {
                    sb.Append(segment.Text);
                }
                var text = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    OnFinalResult?.Invoke(text);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }

        byte[] remaining;
        lock (_bufferLock)
        {
            remaining = _audioBuffer.ToArray();
            _audioBuffer.Clear();
        }

        if (remaining.Length > 1600 && !_disposed)
        {
            var samples = ConvertToFloat(remaining);
            try
            {
                var sb = new System.Text.StringBuilder();
                await foreach (var segment in _processor.ProcessAsync(samples))
                {
                    if (_disposed) break;
                    sb.Append(segment.Text);
                }
                var text = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text) && !_disposed)
                    OnFinalResult?.Invoke(text);
            }
            catch { }
        }
    }

    private static float[] ConvertToFloat(byte[] pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
        }
        return samples;
    }

    public void Dispose()
    {
        _disposed = true;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _cts?.Cancel();
        try { _processingTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _waveIn.Dispose();
        _processor.Dispose();
        _cts?.Dispose();
    }
}

// ── Groq Cloud implementation ────────────────────────────────────

public class GroqWhisperRecognizer : ISpeechRecognizer
{
    public event Action<string>? OnPartialResult;
    public event Action<string>? OnFinalResult;
    public event Action<float>? OnAudioLevel;

    private readonly WaveInEvent _waveIn;
    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly string _language;
    private readonly List<byte> _audioBuffer = [];
    private readonly object _bufferLock = new();
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private volatile bool _disposed;
    private const int ChunkSizeBytes = 16000 * 2 * 5; // 5 seconds

    public GroqWhisperRecognizer(string apiKey, string modelId, string language = "auto")
    {
        _apiKey = apiKey;
        _modelId = modelId;
        _language = language;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _waveIn.StartRecording();
        _processingTask = ProcessLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _disposed = true;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _cts?.Cancel();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;

        float sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = sample / 32768f;
            sum += normalized * normalized;
        }
        var rms = MathF.Sqrt(sum / (e.BytesRecorded / 2f));
        OnAudioLevel?.Invoke(rms);

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded));
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            byte[] chunk;
            lock (_bufferLock)
            {
                if (_audioBuffer.Count < ChunkSizeBytes) continue;
                chunk = _audioBuffer.ToArray();
                _audioBuffer.Clear();
            }

            OnPartialResult?.Invoke("...");

            var text = await SendToGroqAsync(http, chunk, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                OnFinalResult?.Invoke(text);
        }

        // Process remaining audio
        byte[] remaining;
        lock (_bufferLock)
        {
            remaining = _audioBuffer.ToArray();
            _audioBuffer.Clear();
        }

        if (remaining.Length > 3200 && !_disposed)
        {
            using var http2 = new HttpClient();
            http2.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            try
            {
                var text = await SendToGroqAsync(http2, remaining, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text) && !_disposed)
                    OnFinalResult?.Invoke(text);
            }
            catch { }
        }
    }

    private async Task<string> SendToGroqAsync(HttpClient http, byte[] pcm16, CancellationToken ct)
    {
        // Convert PCM16 to WAV in memory
        using var wavStream = new MemoryStream();
        using (var writer = new BinaryWriter(wavStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // WAV header
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm16.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16); // chunk size
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(16000); // sample rate
            writer.Write(16000 * 2); // byte rate
            writer.Write((short)2); // block align
            writer.Write((short)16); // bits per sample
            writer.Write("data"u8);
            writer.Write(pcm16.Length);
            writer.Write(pcm16);
        }
        wavStream.Position = 0;

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(wavStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(_modelId), "model");
        content.Add(new StringContent("json"), "response_format");
        if (_language != "auto")
            content.Add(new StringContent(_language), "language");

        var response = await http.PostAsync(
            "https://api.groq.com/openai/v1/audio/transcriptions", content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("text").GetString() ?? "";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _cts?.Cancel();
        try { _processingTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _waveIn.Dispose();
        _cts?.Dispose();
    }
}

// ── Factory ──────────────────────────────────────────────────────

public static class SpeechRecognizerFactory
{
    public static ISpeechRecognizer Create(SttModelInfo model, string? groqApiKey = null)
    {
        if (model.Engine == SttEngine.Vosk)
            return new VoskRecognizer(model.ModelDir);

        if (model.Engine == SttEngine.GroqCloud)
        {
            if (string.IsNullOrWhiteSpace(groqApiKey))
                throw new InvalidOperationException("Groq API key is required");
            return new GroqWhisperRecognizer(groqApiKey, model.Id, model.WhisperLanguage ?? "auto");
        }

        var binPath = Path.Combine(model.ModelDir, $"{model.Id}.bin");
        return new WhisperRecognizer(binPath, model.WhisperLanguage ?? "auto");
    }
}
