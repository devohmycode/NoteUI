using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NoteUI;

internal static class ScreenCaptureService
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private const byte VK_LWIN = 0x5B, VK_SHIFT = 0x10, VK_S = 0x53;
    private const int KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Triggers the Windows snipping overlay (Win+Shift+S) and inserts the captured
    /// image into the specified RichEditBox.
    /// Polls GetClipboardSequenceNumber (Win32, works without focus) every 500 ms to
    /// detect clipboard changes, then reads the bitmap via WinRT Clipboard API.
    /// </summary>
    public static void CaptureAndInsert(Window window, RichEditBox editor, Action? onInserted = null)
    {
        var seqBefore = GetClipboardSequenceNumber();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var ticks = 0;
        var busy = false;

        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            ticks++;
            if (ticks > 120) // 60 s timeout
            {
                timer.Stop();
                return;
            }

            // No clipboard change yet
            if (GetClipboardSequenceNumber() == seqBefore) return;

            busy = true;
            timer.Stop();

            try
            {
                // Small delay so the clipboard content is fully written
                await Task.Delay(200);

                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Bitmap)) return;

                var streamRef = await content.GetBitmapAsync();
                using var origStream = await streamRef.OpenReadAsync();

                // Decode and re-encode as PNG into a persistent stream
                // (the clipboard DIB format is not rendered by RichEditBox)
                var decoder = await BitmapDecoder.CreateAsync(origStream);
                var w = (int)decoder.PixelWidth;
                var h = (int)decoder.PixelHeight;

                var softBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                var pngStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, pngStream);
                encoder.SetSoftwareBitmap(softBitmap);
                await encoder.FlushAsync();

                const int maxWidth = 340;
                if (w > maxWidth)
                {
                    h = (int)(h * ((double)maxWidth / w));
                    w = maxWidth;
                }

                pngStream.Seek(0);
                editor.Document.Selection.InsertImage(
                    w, h, 0,
                    VerticalCharacterAlignment.Baseline,
                    "capture",
                    pngStream);
                // pngStream intentionally NOT disposed — RichEditBox holds a reference for rendering

                editor.Focus(FocusState.Programmatic);
                onInserted?.Invoke();
            }
            catch
            {
                // Clipboard read failed (e.g. locked) — retry
                busy = false;
                timer.Start();
            }
        };

        timer.Start();

        // Simulate Win+Shift+S to open the system snipping overlay
        keybd_event(VK_LWIN, 0, 0, 0);
        keybd_event(VK_SHIFT, 0, 0, 0);
        keybd_event(VK_S, 0, 0, 0);
        keybd_event(VK_S, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
    }
}
