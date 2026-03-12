using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NoteUI;

internal static class WindowShadow
{
    public static void Apply(IntPtr hwnd)
    {
        var margins = new MARGINS
        {
            leftWidth = 0,
            rightWidth = 0,
            topHeight = 0,
            bottomHeight = 1
        };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    public static void Apply(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        Apply(hwnd);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int leftWidth;
        public int rightWidth;
        public int topHeight;
        public int bottomHeight;
    }
}
