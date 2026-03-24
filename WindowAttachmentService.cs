using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace NoteUI;

/// <summary>
/// Monitors foreground windows and open Explorer folders to auto-show/hide
/// notes that are attached to a specific process or folder path.
/// </summary>
public sealed class WindowAttachmentService : IDisposable
{
    private readonly NotesManager _notes;
    private readonly string _selfProcessName;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _explorerTimer;
    private readonly HashSet<string> _visibleNoteIds = [];

    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundDelegate; // prevent GC collection

    // Current foreground process info (updated on each EVENT_SYSTEM_FOREGROUND)
    private string? _currentForegroundProcess;
    private IntPtr _currentForegroundHwnd;

    public event Action<NoteEntry, IntPtr>? ShowRequested;
    public event Action<string>? HideRequested;

    public WindowAttachmentService(NotesManager notes)
    {
        _notes = notes;
        _selfProcessName = NormalizeProcessName(Process.GetCurrentProcess().ProcessName);

        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _foregroundTimer.Tick += ForegroundTimer_Tick;

        _explorerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _explorerTimer.Tick += ExplorerTimer_Tick;
    }

    public void Start()
    {
        // Hook foreground window changes
        _foregroundDelegate = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        _foregroundTimer.Start();
        _explorerTimer.Start();

        // Initial check
        CheckForegroundWindow();
        CheckExplorerFolders();
    }

    public void Stop()
    {
        _foregroundTimer.Stop();
        _explorerTimer.Stop();

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    /// <summary>Call when notes are added/removed/modified to re-evaluate attachments.</summary>
    public void Refresh()
    {
        CheckForegroundWindow();
        CheckExplorerFolders();
    }

    public void Dispose()
    {
        Stop();
        _foregroundTimer.Tick -= ForegroundTimer_Tick;
        _explorerTimer.Tick -= ExplorerTimer_Tick;
    }

    // ── Foreground process monitoring ───────────────────────────────

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        CheckForegroundWindow();
    }

    private void ForegroundTimer_Tick(object? sender, object e)
    {
        CheckForegroundWindow();
    }

    private void CheckForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        string? processName = null;

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
            {
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch { }
            }
        }

        _currentForegroundProcess = processName;
        _currentForegroundHwnd = hwnd;

        EvaluateProcessAttachments(processName, hwnd);
    }

    private void EvaluateProcessAttachments(string? foregroundProcess, IntPtr hwnd)
    {
        var processNotes = _notes.Notes
            .Where(n => n.AttachMode == "process" && !string.IsNullOrEmpty(n.AttachTarget))
            .ToList();

        var isOwnProcessForeground = IsOwnProcessForeground(foregroundProcess);

        foreach (var note in processNotes)
        {
            var matches = IsSameProcessTarget(note.AttachTarget, foregroundProcess);

            if (matches && !IsIconic(hwnd))
            {
                if (_visibleNoteIds.Add(note.Id))
                    ShowRequested?.Invoke(note, hwnd);
            }
            else if (isOwnProcessForeground && _visibleNoteIds.Contains(note.Id))
            {
                // Keep already-attached note visible while user interacts with it
                // (drag/edit focuses NoteUI, not the target process).
                continue;
            }
            else
            {
                if (_visibleNoteIds.Remove(note.Id))
                    HideRequested?.Invoke(note.Id);
            }
        }
    }

    private bool IsOwnProcessForeground(string? foregroundProcess)
    {
        if (string.IsNullOrWhiteSpace(foregroundProcess))
            return false;
        return string.Equals(NormalizeProcessName(foregroundProcess), _selfProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameProcessTarget(string? configuredTarget, string? foregroundProcess)
    {
        if (string.IsNullOrWhiteSpace(configuredTarget) || string.IsNullOrWhiteSpace(foregroundProcess))
            return false;

        var configured = NormalizeProcessName(configuredTarget);
        var foreground = NormalizeProcessName(foregroundProcess);
        return string.Equals(configured, foreground, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return value;

        var withoutPath = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(withoutPath))
            withoutPath = value;

        if (withoutPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            withoutPath = Path.GetFileNameWithoutExtension(withoutPath);

        return withoutPath.Trim();
    }

    // ── Explorer folder monitoring ──────────────────────────────────

    private void ExplorerTimer_Tick(object? sender, object e)
    {
        CheckExplorerFolders();
    }

    private void CheckExplorerFolders()
    {
        var folderNotes = _notes.Notes
            .Where(n => n.AttachMode == "folder" && !string.IsNullOrEmpty(n.AttachTarget))
            .ToList();

        if (folderNotes.Count == 0)
        {
            // Hide any previously visible folder-attached notes
            foreach (var id in _visibleNoteIds.ToList())
            {
                var note = _notes.Notes.FirstOrDefault(n => n.Id == id);
                if (note?.AttachMode == "folder")
                {
                    _visibleNoteIds.Remove(id);
                    HideRequested?.Invoke(id);
                }
            }
            return;
        }

        var openPaths = GetOpenExplorerPaths();

        foreach (var note in folderNotes)
        {
            string normalizedTarget;
            try { normalizedTarget = Path.GetFullPath(note.AttachTarget!); }
            catch { continue; }

            var match = openPaths.FirstOrDefault(p =>
                IsSamePath(p.Path, normalizedTarget));

            if (match.Path != null)
            {
                if (_visibleNoteIds.Add(note.Id))
                    ShowRequested?.Invoke(note, match.Hwnd);
            }
            else
            {
                if (_visibleNoteIds.Remove(note.Id))
                    HideRequested?.Invoke(note.Id);
            }
        }
    }

    public static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrEmpty(root) &&
            !string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar);
        }

        return normalized;
    }

    private static string NormalizeExplorerTitle(string title)
    {
        var normalized = title.Trim();
        string[] knownSuffixes =
        [
            " - File Explorer",
            " - Explorateur de fichiers",
            " - Explorador de archivos",
            " - Datei-Explorer",
            " - Esplora file",
            " - Windows Explorer"
        ];

        foreach (var suffix in knownSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return normalized[..^suffix.Length].Trim();
        }

        return normalized;
    }

    /// <summary>
    /// Returns open Explorer folder paths. When multiple tabs exist in the same
    /// Explorer window, tries to detect only the active tab by matching against
    /// the window title (works best with "Display full path in title bar" enabled).
    /// Falls back to returning all open paths if detection fails.
    /// </summary>
    public static List<(string Path, IntPtr Hwnd)> GetOpenExplorerPaths()
    {
        // Step 1: Get ALL open shell windows
        var allShellWindows = new List<(string Path, IntPtr Hwnd, string LocationName)>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return [];
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    string? fullName = w.FullName;
                    if (fullName == null || !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? url = w.LocationURL;
                    if (string.IsNullOrEmpty(url)) continue;
                    var uri = new Uri(url);
                    if (!uri.IsFile) continue;

                    var hwnd = (IntPtr)(long)w.HWND;
                    string locationName = "";
                    try { locationName = w.LocationName ?? ""; } catch { }

                    var localPath = NormalizePath(uri.LocalPath);
                    if (string.IsNullOrWhiteSpace(localPath)) continue;

                    allShellWindows.Add((localPath, hwnd, locationName));
                }
                catch { }
            }
        }
        catch { }

        if (allShellWindows.Count <= 1)
            return allShellWindows.Select(s => (s.Path, s.Hwnd)).ToList();

        // Step 2: Try to filter to active tabs per Explorer window title.
        // If title matching fails for a window, keep all its tabs to avoid false negatives.
        try
        {
            var explorerTitlesByHwnd = new Dictionary<IntPtr, string>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var csb = new System.Text.StringBuilder(256);
                GetClassName(hwnd, csb, csb.Capacity);
                if (csb.ToString() != "CabinetWClass") return true;
                var tsb = new System.Text.StringBuilder(512);
                GetWindowText(hwnd, tsb, tsb.Capacity);
                var t = tsb.ToString();
                if (!string.IsNullOrEmpty(t))
                    explorerTitlesByHwnd[hwnd] = NormalizeExplorerTitle(t);
                return true;
            }, IntPtr.Zero);

            if (explorerTitlesByHwnd.Count > 0)
            {
                var filtered = new List<(string Path, IntPtr Hwnd)>();

                foreach (var group in allShellWindows.GroupBy(sw => sw.Hwnd))
                {
                    if (!explorerTitlesByHwnd.TryGetValue(group.Key, out var title) ||
                        string.IsNullOrWhiteSpace(title))
                    {
                        filtered.AddRange(group.Select(s => (s.Path, s.Hwnd)));
                        continue;
                    }

                    var matchedInWindow = group.Where(sw =>
                    {
                        var trimmed = sw.Path.TrimEnd(
                            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                        var folderName = System.IO.Path.GetFileName(trimmed);
                        var locationName = sw.LocationName?.Trim();

                        return
                            (!string.IsNullOrEmpty(locationName) &&
                                title.Equals(locationName, StringComparison.OrdinalIgnoreCase)) ||
                            title.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(folderName) &&
                                title.Equals(folderName, StringComparison.OrdinalIgnoreCase));
                    }).Select(s => (s.Path, s.Hwnd)).ToList();

                    if (matchedInWindow.Count > 0)
                        filtered.AddRange(matchedInWindow);
                    else
                        filtered.AddRange(group.Select(s => (s.Path, s.Hwnd)));
                }

                if (filtered.Count > 0)
                    return filtered
                        .DistinctBy(p => (p.Path, p.Hwnd))
                        .ToList();
            }
        }
        catch { }

        // Fallback: return all open paths
        return allShellWindows.Select(s => (s.Path, s.Hwnd)).ToList();
    }

    /// <summary>
    /// Calculates the absolute position for an attached note given the target window handle.
    /// Returns (x, y) or null if the target window is minimized or invalid.
    /// </summary>
    public static (int X, int Y)? GetAttachedPosition(NoteEntry note, IntPtr targetHwnd, int noteWidth = 400, int noteHeight = 450)
    {
        // Shell.Application may return a child HWND; get the top-level window
        var topHwnd = GetAncestor(targetHwnd, GA_ROOT);
        if (topHwnd == IntPtr.Zero) topHwnd = targetHwnd;

        if (topHwnd == IntPtr.Zero || IsIconic(topHwnd))
            return null;

        if (!GetWindowRect(topHwnd, out var rect))
            return null;

        if (note.AttachOffsetX == 0 && note.AttachOffsetY == 0)
        {
            // Default: bottom-right corner of the target window, with margin
            const int margin = 12;
            return (rect.Right - noteWidth - margin, rect.Bottom - noteHeight - margin);
        }

        return (rect.Left + note.AttachOffsetX, rect.Top + note.AttachOffsetY);
    }

    /// <summary>
    /// Saves the relative offset of the note window position to the target window.
    /// </summary>
    public static void SaveAttachOffset(NoteEntry note, IntPtr targetHwnd, int noteX, int noteY)
    {
        var topHwnd = GetAncestor(targetHwnd, GA_ROOT);
        if (topHwnd == IntPtr.Zero) topHwnd = targetHwnd;

        if (topHwnd == IntPtr.Zero || IsIconic(topHwnd))
            return;

        if (!GetWindowRect(topHwnd, out var rect))
            return;

        note.AttachOffsetX = noteX - rect.Left;
        note.AttachOffsetY = noteY - rect.Top;
    }

    /// <summary>Returns the current foreground window handle (for saving offsets on hide).</summary>
    public IntPtr CurrentForegroundHwnd => _currentForegroundHwnd;

    // ── Win32 P/Invoke ──────────────────────────────────────────────

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

}
