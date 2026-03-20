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

    private const int WM_NCCALCSIZE = 0x0083;

    public static void RemoveWindowBorder(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
    }

    /// <summary>
    /// Removes the visible non-client frame while keeping WS_THICKFRAME for resize.
    /// Subclasses the window to zero out the non-client area via WM_NCCALCSIZE.
    /// </summary>
    public static void RemoveWindowBorderKeepResize(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);

        // Remove caption styles but keep WS_THICKFRAME for resize
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        // Subclass to zero out non-client area (removes visible top frame)
        var subclassProc = new SubclassProc(BorderlessResizeProc);
        _pinnedDelegates.Add(subclassProc); // prevent GC
        SetWindowSubclass(hwnd, subclassProc, IntPtr.Zero, IntPtr.Zero);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
    }

    private static IntPtr BorderlessResizeProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            return IntPtr.Zero; // non-client area = 0 → no visible frame
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // prevent delegate from being garbage-collected
    private static readonly List<SubclassProc> _pinnedDelegates = [];

    public static void CenterOnScreen(Window window)
    {
        var appWindow = window.AppWindow;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var x = (workArea.Width - appWindow.Size.Width) / 2 + workArea.X;
        var y = (workArea.Height - appWindow.Size.Height) / 2 + workArea.Y;
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public static void MoveToBottomRight(Window window, int margin = 12)
    {
        var appWindow = window.AppWindow;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;

        var x = workArea.X + Math.Max(0, workArea.Width - appWindow.Size.Width - margin);
        var y = workArea.Y + Math.Max(0, workArea.Height - appWindow.Size.Height - margin);
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public static void MoveToVisibleArea(Window window, int x, int y)
    {
        var appWindow = window.AppWindow;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;

        var minX = workArea.X;
        var minY = workArea.Y;
        var maxX = workArea.X + Math.Max(0, workArea.Width - appWindow.Size.Width);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - appWindow.Size.Height);

        var clampedX = Math.Clamp(x, minX, maxX);
        var clampedY = Math.Clamp(y, minY, maxY);
        appWindow.Move(new Windows.Graphics.PointInt32(clampedX, clampedY));
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

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
