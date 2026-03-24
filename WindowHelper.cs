using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 6;

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
    /// Subclasses the window to zero out the non-client area via WM_NCCALCSIZE
    /// and handle WM_NCHITTEST for resize edges.
    /// </summary>
    public static void RemoveWindowBorderKeepResize(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);

        // Remove caption styles but keep WS_THICKFRAME for resize
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        // Subclass to zero out non-client area and handle resize hit-testing
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

        if (uMsg == WM_NCHITTEST)
        {
            // Extract cursor position from lParam
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

            GetWindowRect(hWnd, out var rect);

            bool left = x >= rect.Left && x < rect.Left + ResizeBorder;
            bool right = x < rect.Right && x >= rect.Right - ResizeBorder;
            bool top = y >= rect.Top && y < rect.Top + ResizeBorder;
            bool bottom = y < rect.Bottom && y >= rect.Bottom - ResizeBorder;

            if (top && left) return (IntPtr)HTTOPLEFT;
            if (top && right) return (IntPtr)HTTOPRIGHT;
            if (bottom && left) return (IntPtr)HTBOTTOMLEFT;
            if (bottom && right) return (IntPtr)HTBOTTOMRIGHT;
            if (left) return (IntPtr)HTLEFT;
            if (right) return (IntPtr)HTRIGHT;
            if (top) return (IntPtr)HTTOP;
            if (bottom) return (IntPtr)HTBOTTOM;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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

    /// <summary>
    /// Adds invisible XAML resize grips on left, right, bottom edges and corners.
    /// Uses pointer capture (same pattern as DragBar) for native-feeling resize.
    /// Top edge is handled by ExtendsContentIntoTitleBar / WM_NCHITTEST.
    /// </summary>
    public static void AddResizeGrips(Window window)
    {
        if (window.Content is not Grid rootGrid) return;

        const int grip = 6;
        const int cornerGrip = 12;
        int rowSpan = Math.Max(1, rootGrid.RowDefinitions.Count);
        int colSpan = Math.Max(1, rootGrid.ColumnDefinitions.Count);

        // Resize directions: left=-1/0, right=+1/0, top=0/-1, bottom=0/+1
        void AddGrip(HorizontalAlignment hAlign, VerticalAlignment vAlign,
                     double width, double height,
                     int dirX, int dirY, InputSystemCursorShape cursorShape)
        {
            var grip = new ResizeGrip(cursorShape)
            {
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
            };
            if (!double.IsNaN(width)) grip.Width = width;
            if (!double.IsNaN(height)) grip.Height = height;

            Grid.SetRowSpan(grip, rowSpan);
            if (colSpan > 1) Grid.SetColumnSpan(grip, colSpan);

            bool dragging = false;
            POINT startCursor = default;
            Windows.Graphics.PointInt32 startPos = default;
            Windows.Graphics.SizeInt32 startSize = default;
            var appWindow = window.AppWindow;

            grip.PointerPressed += (s, e) =>
            {
                dragging = true;
                GetCursorPos(out startCursor);
                startPos = appWindow.Position;
                startSize = appWindow.Size;
                ((UIElement)s!).CapturePointer(e.Pointer);
                e.Handled = true;
            };

            grip.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                GetCursorPos(out var current);
                int dx = current.X - startCursor.X;
                int dy = current.Y - startCursor.Y;

                int newX = startPos.X;
                int newY = startPos.Y;
                int newW = startSize.Width;
                int newH = startSize.Height;

                if (dirX < 0) { newX = startPos.X + dx; newW = startSize.Width - dx; }
                if (dirX > 0) { newW = startSize.Width + dx; }
                if (dirY < 0) { newY = startPos.Y + dy; newH = startSize.Height - dy; }
                if (dirY > 0) { newH = startSize.Height + dy; }

                const int minW = 200, minH = 100;
                if (newW < minW) { if (dirX < 0) newX = startPos.X + startSize.Width - minW; newW = minW; }
                if (newH < minH) { if (dirY < 0) newY = startPos.Y + startSize.Height - minH; newH = minH; }

                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(newX, newY, newW, newH));
                e.Handled = true;
            };

            grip.PointerReleased += (s, e) =>
            {
                dragging = false;
                ((UIElement)s!).ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            };

            rootGrid.Children.Add(grip);
        }

        // Edge grips (dirX, dirY)
        AddGrip(HorizontalAlignment.Left, VerticalAlignment.Stretch, grip, double.NaN, -1, 0, InputSystemCursorShape.SizeWestEast);
        AddGrip(HorizontalAlignment.Right, VerticalAlignment.Stretch, grip, double.NaN, +1, 0, InputSystemCursorShape.SizeWestEast);
        AddGrip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, grip, 0, +1, InputSystemCursorShape.SizeNorthSouth);

        // Corner grips (larger, added after edges for higher z-order)
        AddGrip(HorizontalAlignment.Left, VerticalAlignment.Bottom, cornerGrip, cornerGrip, -1, +1, InputSystemCursorShape.SizeNortheastSouthwest);
        AddGrip(HorizontalAlignment.Right, VerticalAlignment.Bottom, cornerGrip, cornerGrip, +1, +1, InputSystemCursorShape.SizeNorthwestSoutheast);
    }

    /// <summary>Panel that sets the resize cursor via ProtectedCursor.</summary>
    private sealed class ResizeGrip : Grid
    {
        public ResizeGrip(InputSystemCursorShape cursorShape)
        {
            ProtectedCursor = InputSystemCursor.Create(cursorShape);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

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
