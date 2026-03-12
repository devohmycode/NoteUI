using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NoteUI;

internal static class WindowHelper
{
    private const int GWL_STYLE = -16;
    private const long WS_BORDER = 0x00800000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_DLGFRAME = 0x00400000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;

    public static void RemoveWindowBorder(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
    }

    public static void CenterOnScreen(Window window)
    {
        var appWindow = window.AppWindow;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var x = (workArea.Width - appWindow.Size.Width) / 2 + workArea.X;
        var y = (workArea.Height - appWindow.Size.Height) / 2 + workArea.Y;
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public static OverlappedPresenter GetOverlappedPresenter(Window window)
    {
        var appWindow = window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter existing)
            return existing;
        var presenter = OverlappedPresenter.Create();
        appWindow.SetPresenter(presenter);
        return presenter;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
