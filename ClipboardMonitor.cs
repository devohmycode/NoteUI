using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NoteUI;

internal sealed class ClipboardMonitor : IDisposable
{
    private readonly IntPtr _ownHwnd;

    public string? SourceExePath { get; private set; }
    public string? SourceTitle { get; private set; }

    public event Action<ClipboardHistoryEntry>? EntryAdded;

    public ClipboardMonitor(Window window)
    {
        _ownHwnd = WindowNative.GetWindowHandle(window);
        Clipboard.ContentChanged += OnContentChanged;
    }

    private async void OnContentChanged(object? sender, object e)
    {
        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero || fgHwnd == _ownHwnd) return;

            GetWindowThreadProcessId(fgHwnd, out var pid);
            if (pid == 0) return;

            SourceExePath = GetProcessPath(pid);

            var sb = new StringBuilder(512);
            GetWindowText(fgHwnd, sb, sb.Capacity);
            SourceTitle = sb.ToString();

            await Task.Delay(100);

            var content = Clipboard.GetContent();
            if (content == null) return;

            if (content.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await content.GetStorageItemsAsync();
                    if (items != null && items.Count > 0)
                    {
                        string[] imageExts = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
                        var imageFiles = items
                            .OfType<StorageFile>()
                            .Where(f => imageExts.Contains(Path.GetExtension(f.Name).ToLowerInvariant()))
                            .ToList();

                        if (imageFiles.Count > 0)
                        {
                            foreach (var file in imageFiles)
                            {
                                var buffer = await FileIO.ReadBufferAsync(file);
                                var bytes = new byte[buffer.Length];
                                using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
                                reader.ReadBytes(bytes);

                                var imgEntry = new ClipboardHistoryEntry
                                {
                                    ContentType = "image",
                                    ImageData = bytes,
                                    SourceExePath = SourceExePath,
                                    SourceTitle = file.Name,
                                    CapturedAt = DateTime.Now
                                };
                                EntryAdded?.Invoke(imgEntry);
                            }
                            return;
                        }

                        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                        if (paths.Count > 0)
                        {
                            var names = items.Select(i => i.Name).ToList();
                            var fileEntry = new ClipboardHistoryEntry
                            {
                                ContentType = "text",
                                TextContent = string.Join("\n", paths),
                                SourceExePath = SourceExePath,
                                SourceTitle = string.Join(", ", names),
                                CapturedAt = DateTime.Now
                            };
                            EntryAdded?.Invoke(fileEntry);
                            return;
                        }
                    }
                }
                catch { }
            }

            string? text = null;
            string? rtf = null;
            byte[]? imageData = null;

            if (content.Contains(StandardDataFormats.Text))
                text = await content.GetTextAsync();
            if (content.Contains(StandardDataFormats.Rtf))
            {
                try { rtf = await content.GetRtfAsync(); } catch { }
            }
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                try
                {
                    var reference = await content.GetBitmapAsync();
                    using var stream = await reference.OpenReadAsync();
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    using var outStream = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
                    var pixelData = await decoder.GetPixelDataAsync();
                    encoder.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode,
                        decoder.PixelWidth, decoder.PixelHeight,
                        decoder.DpiX, decoder.DpiY,
                        pixelData.DetachPixelData());
                    await encoder.FlushAsync();
                    outStream.Seek(0);
                    imageData = new byte[outStream.Size];
                    var reader = new DataReader(outStream);
                    await reader.LoadAsync((uint)imageData.Length);
                    reader.ReadBytes(imageData);
                }
                catch { }
            }

            if (string.IsNullOrEmpty(text) && imageData == null) return;

            var entry = new ClipboardHistoryEntry
            {
                ContentType = imageData != null && string.IsNullOrEmpty(text) ? "image" : "text",
                TextContent = text,
                RtfContent = rtf,
                ImageData = imageData,
                SourceExePath = SourceExePath,
                SourceTitle = CleanTitle(SourceTitle, SourceExePath),
                CapturedAt = DateTime.Now
            };

            EntryAdded?.Invoke(entry);
        }
        catch { }
    }

    private static string? GetProcessPath(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string CleanTitle(string? title, string? exePath)
    {
        if (string.IsNullOrEmpty(title)) return "";

        var processName = string.IsNullOrEmpty(exePath)
            ? ""
            : Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

        if (processName is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "arc")
        {
            string[] suffixes =
            [
                " - Google Chrome", " \u2014 Mozilla Firefox", " - Mozilla Firefox",
                " - Microsoft\u200B Edge", " - Microsoft Edge",
                " - Brave", " - Opera", " - Vivaldi", " - Arc"
            ];
            foreach (var suffix in suffixes)
            {
                if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return title[..^suffix.Length];
            }
        }

        return title;
    }

    public void Dispose()
    {
        Clipboard.ContentChanged -= OnContentChanged;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
