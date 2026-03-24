using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NoteUI;

/// <summary>
/// Helper to enumerate visible windows for the "Attach to program" picker.
/// </summary>
public static class WindowAttachmentHelper
{
    public record WindowInfo(string ProcessName, string Title);

    public static List<WindowInfo> GetVisibleWindows()
    {
        var results = new List<WindowInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == currentPid) return true;

            try
            {
                var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName;

                // Skip system processes
                if (name is "explorer" or "SearchHost" or "ShellExperienceHost"
                    or "StartMenuExperienceHost" or "TextInputHost" or "SystemSettings")
                    return true;

                // Deduplicate by process name (keep first = most relevant window)
                if (seen.Add(name))
                    results.Add(new WindowInfo(name, title.Length > 60 ? title[..57] + "..." : title));
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        return results.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
