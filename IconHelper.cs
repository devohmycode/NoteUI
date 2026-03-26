using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NoteUI;

internal static class IconHelper
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteUI", "icon-cache");

    private static readonly ConcurrentDictionary<string, byte[]?> _memCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task LoadIconAsync(Image target, string exePath)
    {
        try
        {
            var pngBytes = await GetOrExtractAsync(exePath);
            if (pngBytes == null || pngBytes.Length == 0) return;

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(pngBytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            target.Source = bmp;
        }
        catch { }
    }

    private static async Task<byte[]?> GetOrExtractAsync(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        if (_memCache.TryGetValue(exePath, out var cached))
            return cached;

        // Check disk cache
        var key = GetCacheKey(exePath);
        var cachePath = Path.Combine(CacheDir, $"{key}.png");

        byte[]? pngBytes = null;

        if (File.Exists(cachePath))
        {
            pngBytes = await File.ReadAllBytesAsync(cachePath);
        }
        else
        {
            // Extract icon pixels on background thread
            var data = await Task.Run(() => ExtractIconPixels(exePath));
            if (data != null)
            {
                pngBytes = await EncodePngAsync(data.Value.w, data.Value.h, data.Value.pixels);
                if (pngBytes != null)
                {
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        await File.WriteAllBytesAsync(cachePath, pngBytes);
                    }
                    catch { }
                }
            }
        }

        _memCache[exePath] = pngBytes;
        return pngBytes;
    }

    private static string GetCacheKey(string exePath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(exePath.ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12];
    }

    private static (int w, int h, byte[] pixels)? ExtractIconPixels(string exePath)
    {
        if (!File.Exists(exePath)) return null;

        var hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
        if (hIcon == IntPtr.Zero) return null;

        try
        {
            if (!GetIconInfo(hIcon, out var info)) return null;
            try
            {
                var bmp = new BITMAP();
                if (GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), ref bmp) == 0)
                    return null;

                int w = bmp.bmWidth, h = bmp.bmHeight;
                if (w <= 0 || h <= 0) return null;

                var bi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                };

                var pixels = new byte[w * h * 4];
                var hdc = GetDC(IntPtr.Zero);
                int lines = GetDIBits(hdc, info.hbmColor, 0, (uint)h, pixels, ref bi, 0);
                ReleaseDC(IntPtr.Zero, hdc);

                return lines > 0 ? (w, h, pixels) : null;
            }
            finally
            {
                DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static async Task<byte[]?> EncodePngAsync(int w, int h, byte[] bgra)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)w, (uint)h, 96, 96, bgra);
            await encoder.FlushAsync();

            stream.Seek(0);
            var bytes = new byte[stream.Size];
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)bytes.Length);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    // ── P/Invoke ────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }
}
