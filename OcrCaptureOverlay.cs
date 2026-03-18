using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace NoteUI;

public sealed class OcrRegionCapturedEventArgs : EventArgs
{
    public OcrRegionCapturedEventArgs(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
}

public sealed class OcrCaptureOverlay : Window
{
    public event EventHandler<OcrRegionCapturedEventArgs>? RegionCaptured;
    public event EventHandler? CaptureCanceled;

    private readonly Grid _rootGrid;
    private readonly Image _screenshotImage;
    private readonly Rectangle _selectionRect;
    private readonly Border _hintBadge;
    private readonly TextBlock _hintText;

    private byte[]? _screenPixels;
    private int _screenW;
    private int _screenH;
    private bool _isDragging;
    private int _startX, _startY, _endX, _endY;
    private IntPtr _crossCursorHandle;

    public OcrCaptureOverlay()
    {
        ExtendsContentIntoTitleBar = true;

        _rootGrid = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            IsTabStop = true
        };
        _rootGrid.KeyDown += RootGrid_KeyDown;

        _screenshotImage = new Image { Stretch = Stretch.Fill };

        var dimmer = new Rectangle
        {
            Fill = new SolidColorBrush(ColorHelper.FromArgb(0x77, 0, 0, 0))
        };

        var overlayCanvas = new Canvas();

        _selectionRect = new Rectangle
        {
            Visibility = Visibility.Collapsed,
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 168, 255)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(42, 255, 255, 255))
        };

        _hintText = new TextBlock
        {
            Text = Lang.T("ocr_select_zone"),
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 12
        };

        _hintBadge = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xCC, 16, 16, 16)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            IsHitTestVisible = false,
            Child = _hintText
        };

        overlayCanvas.Children.Add(_selectionRect);
        overlayCanvas.Children.Add(_hintBadge);
        Canvas.SetLeft(_hintBadge, 14);
        Canvas.SetTop(_hintBadge, 14);

        _rootGrid.Children.Add(_screenshotImage);
        _rootGrid.Children.Add(dimmer);
        _rootGrid.Children.Add(overlayCanvas);

        Content = _rootGrid;

        CaptureScreen();
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnActivated;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, _screenW, _screenH));

        var crossCursor = LoadCursor(IntPtr.Zero, IDC_CROSS);
        SetCursor(crossCursor);
        _crossCursorHandle = crossCursor;

        _rootGrid.PointerPressed += RootGrid_PointerPressed;
        _rootGrid.PointerMoved += RootGrid_PointerMoved;
        _rootGrid.PointerReleased += RootGrid_PointerReleased;
        _rootGrid.Focus(FocusState.Programmatic);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelCapture();
            e.Handled = true;
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_rootGrid);
        if (point.Properties.IsRightButtonPressed)
        {
            CancelCapture();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        _startX = Math.Clamp((int)point.Position.X, 0, _screenW - 1);
        _startY = Math.Clamp((int)point.Position.Y, 0, _screenH - 1);
        _endX = _startX;
        _endY = _startY;
        _selectionRect.Visibility = Visibility.Visible;
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_crossCursorHandle != IntPtr.Zero) SetCursor(_crossCursorHandle);
        if (!_isDragging) return;

        var point = e.GetCurrentPoint(_rootGrid);
        _endX = Math.Clamp((int)point.Position.X, 0, _screenW - 1);
        _endY = Math.Clamp((int)point.Position.Y, 0, _screenH - 1);
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        var point = e.GetCurrentPoint(_rootGrid);
        _endX = Math.Clamp((int)point.Position.X, 0, _screenW - 1);
        _endY = Math.Clamp((int)point.Position.Y, 0, _screenH - 1);
        UpdateSelectionVisual();

        if (!TryGetSelectionRect(out var x, out var y, out var width, out var height))
        {
            _selectionRect.Visibility = Visibility.Collapsed;
            _hintText.Text = Lang.T("ocr_too_small");
            return;
        }

        var pixels = ExtractRegionPixels(x, y, width, height);
        RegionCaptured?.Invoke(this, new OcrRegionCapturedEventArgs(pixels, width, height));
        Close();
        e.Handled = true;
    }

    private void UpdateSelectionVisual()
    {
        var x = Math.Min(_startX, _endX);
        var y = Math.Min(_startY, _endY);
        var width = Math.Abs(_endX - _startX);
        var height = Math.Abs(_endY - _startY);

        Canvas.SetLeft(_selectionRect, x);
        Canvas.SetTop(_selectionRect, y);
        _selectionRect.Width = Math.Max(1, width);
        _selectionRect.Height = Math.Max(1, height);

        _hintText.Text = width > 3 && height > 3
            ? $"{width} × {height}"
            : "Sélectionnez une zone";
    }

    private bool TryGetSelectionRect(out int x, out int y, out int width, out int height)
    {
        x = Math.Min(_startX, _endX);
        y = Math.Min(_startY, _endY);
        width = Math.Abs(_endX - _startX);
        height = Math.Abs(_endY - _startY);

        if (width < 4 || height < 4) return false;
        if (x + width > _screenW) width = _screenW - x;
        if (y + height > _screenH) height = _screenH - y;
        return width > 0 && height > 0;
    }

    private byte[] ExtractRegionPixels(int x, int y, int width, int height)
    {
        if (_screenPixels == null || width <= 0 || height <= 0)
            return [];

        var output = new byte[width * height * 4];
        var rowSize = width * 4;

        for (var row = 0; row < height; row++)
        {
            var srcOffset = ((y + row) * _screenW + x) * 4;
            var dstOffset = row * rowSize;
            Buffer.BlockCopy(_screenPixels, srcOffset, output, dstOffset, rowSize);
        }

        return output;
    }

    private void CancelCapture()
    {
        CaptureCanceled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    // ── GDI screen capture ──────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDst, int xd, int yd, int w, int h, IntPtr hdcSrc, int xs, int ys, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const int IDC_CROSS = 32515;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    private void CaptureScreen()
    {
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);

        IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hBitmap = IntPtr.Zero, hOld = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, _screenW, _screenH);
            hOld = SelectObject(hdcMem, hBitmap);
            BitBlt(hdcMem, 0, 0, _screenW, _screenH, hdcScreen, 0, 0, SRCCOPY);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = _screenW, biHeight = -_screenH,
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                }
            };

            _screenPixels = new byte[_screenW * _screenH * 4];
            GetDIBits(hdcMem, hBitmap, 0, (uint)_screenH, _screenPixels, ref bmi, 0);

            var wb = new WriteableBitmap(_screenW, _screenH);
            using var stream = wb.PixelBuffer.AsStream();
            stream.Write(_screenPixels, 0, _screenPixels.Length);
            wb.Invalidate();
            _screenshotImage.Source = wb;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
