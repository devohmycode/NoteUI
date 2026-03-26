using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NoteUI;

internal sealed class ClipboardMonitor : IDisposable
{
    private readonly IntPtr _ownHwnd;

    public string? SourceExePath { get; private set; }
    public string? SourceTitle { get; private set; }

    public ClipboardMonitor(Window window)
    {
        _ownHwnd = WindowNative.GetWindowHandle(window);
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnContentChanged;
    }

    private void OnContentChanged(object? sender, object e)
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
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnContentChanged;
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
