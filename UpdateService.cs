using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NoteUI;

public static class UpdateService
{
    public const string CurrentVersion = "0.5.1";
    private const string GitHubOwner = "devohmycode";
    private const string GitHubRepo = "NoteUI";

    private static readonly HttpClient Http = CreateHttpClient();

    public record UpdateInfo(string Version, string DownloadUrl, string ReleaseUrl, string Body);

    /// <summary>Detects whether this installation includes CUDA libraries.</summary>
    public static bool IsCudaBuild()
    {
        var cudaDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12");
        return Directory.Exists(cudaDir);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoteUI", CurrentVersion));
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>Checks GitHub for a newer release. Returns null if up to date or on error.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var remoteVersion = tagName.TrimStart('v', 'V');

            if (!IsNewer(remoteVersion, CurrentVersion))
                return null;

            // Pick the right installer asset based on current build variant
            var expectedAsset = IsCudaBuild() ? "NoteUI-Setup-CUDA.exe" : "NoteUI-Setup-CPU.exe";
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            return new UpdateInfo(remoteVersion, downloadUrl ?? "", releaseUrl, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the installer to a temp file and returns the path.</summary>
    public static async Task<string?> DownloadInstallerAsync(string downloadUrl, IProgress<double>? progress = null)
    {
        try
        {
            using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempPath = Path.Combine(Path.GetTempPath(), $"NoteUI-Setup-{Guid.NewGuid():N}.exe");

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((double)totalRead / totalBytes);
            }

            progress?.Report(1.0);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Launches the downloaded installer with /SILENT and exits the app.</summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT",
            UseShellExecute = true
        });

        Environment.Exit(0);
    }

    /// <summary>Returns true if remote version is strictly newer than local.</summary>
    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(remote, out var r) && Version.TryParse(local, out var l))
            return r > l;
        return false;
    }
}
