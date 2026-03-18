using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace NoteUI;

internal static class OcrService
{
    public static async Task<string> ExtractTextAsync(byte[] pixels, int width, int height)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (engine == null) return "";

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());

        var result = await engine.RecognizeAsync(bitmap);
        return (result.Text ?? "").Trim();
    }
}
