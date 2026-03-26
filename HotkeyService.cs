using System.Runtime.InteropServices;
using System.Text.Json;

namespace NoteUI;

internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    // Hotkey IDs
    public const int HOTKEY_SHOW = 1;
    public const int HOTKEY_NEW_NOTE = 2;
    public const int HOTKEY_PASTE_NOTE = 3;

    [Flags]
    public enum Modifiers : uint
    {
        None = 0,
        Alt = 1,
        Ctrl = 2,
        Shift = 4,
        Win = 8,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = [];
    private readonly NativeMessageHook _hook;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hook = new NativeMessageHook(hwnd, WM_HOTKEY, (wParam, _) =>
        {
            if (_handlers.TryGetValue((int)wParam, out var handler))
                handler();
        });
    }

    public void Register(int id, Modifiers modifiers, uint vk, Action handler)
    {
        UnregisterHotKey(_hwnd, id);
        _handlers[id] = handler;
        RegisterHotKey(_hwnd, id, (uint)modifiers, vk);
    }

    public void Unregister(int id)
    {
        UnregisterHotKey(_hwnd, id);
        _handlers.Remove(id);
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
        _hook.Dispose();
    }

    // ── Shortcut persistence ────────────────────────────────────

    public record ShortcutEntry(string Name, string DisplayLabel, Modifiers Modifiers, uint VirtualKey)
    {
        public string KeyDisplay => FormatShortcut(Modifiers, VirtualKey);
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteUI", "shortcuts.json");

    public static List<ShortcutEntry> GetDefaults() =>
    [
        new("show", Lang.T("shortcut_show"), Modifiers.Ctrl | Modifiers.Alt, 0x4E), // Ctrl+Alt+N
        new("new_note", Lang.T("shortcut_new_note"), Modifiers.Ctrl, 0x4E), // Ctrl+N
        new("paste_note", Lang.T("shortcut_paste_note"), Modifiers.Ctrl | Modifiers.Alt, 0x56), // Ctrl+Alt+V
        new("flyout_back", Lang.T("shortcut_flyout_back"), Modifiers.Ctrl, 0x50), // Ctrl+P
    ];

    public static List<ShortcutEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var entries = JsonSerializer.Deserialize<List<ShortcutEntryDto>>(json);
                if (entries != null)
                {
                    var loaded = entries.Select(e => new ShortcutEntry(e.Name, e.DisplayLabel,
                        (Modifiers)e.Modifiers, e.VirtualKey)).ToList();
                    // Merge with defaults: add any new entries not present in saved file
                    foreach (var def in GetDefaults())
                    {
                        if (!loaded.Any(e => e.Name == def.Name))
                            loaded.Add(def);
                    }
                    return loaded;
                }
            }
        }
        catch { }
        return GetDefaults();
    }

    public static void Save(List<ShortcutEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var dtos = entries.Select(e => new ShortcutEntryDto
            {
                Name = e.Name, DisplayLabel = e.DisplayLabel,
                Modifiers = (uint)e.Modifiers, VirtualKey = e.VirtualKey
            }).ToList();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private class ShortcutEntryDto
    {
        public string Name { get; set; } = "";
        public string DisplayLabel { get; set; } = "";
        public uint Modifiers { get; set; }
        public uint VirtualKey { get; set; }
    }

    public static ShortcutEntry LoadFlyoutBack()
    {
        var all = Load();
        return all.FirstOrDefault(e => e.Name == "flyout_back")
            ?? new("flyout_back", Lang.T("shortcut_flyout_back"), Modifiers.Ctrl, 0x50);
    }

    public static string FormatShortcut(Modifiers mods, uint vk)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Modifiers.Win)) parts.Add("Win");

        if (vk >= 0x30 && vk <= 0x39) parts.Add(((char)vk).ToString());
        else if (vk >= 0x41 && vk <= 0x5A) parts.Add(((char)vk).ToString());
        else if (vk >= 0x70 && vk <= 0x7B) parts.Add($"F{vk - 0x6F}");
        else if (vk == 0x20) parts.Add("Space");
        else if (vk == 0x0D) parts.Add("Enter");
        else if (vk == 0x1B) parts.Add("Esc");
        else if (vk == 0x09) parts.Add("Tab");
        else if (vk != 0) parts.Add($"0x{vk:X2}");

        return string.Join(" + ", parts);
    }

    public static (Modifiers mods, uint vk) ParseKeyEvent(Windows.System.VirtualKey key)
    {
        var mods = Modifiers.None;
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;

        if (state(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            mods |= Modifiers.Ctrl;
        if (state(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            mods |= Modifiers.Alt;
        if (state(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            mods |= Modifiers.Shift;
        if (state(Windows.System.VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            state(Windows.System.VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            mods |= Modifiers.Win;

        uint vk = (uint)key;

        // Ignore modifier-only keys
        if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
            or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows or Windows.System.VirtualKey.LeftControl
            or Windows.System.VirtualKey.RightControl or Windows.System.VirtualKey.LeftMenu
            or Windows.System.VirtualKey.RightMenu or Windows.System.VirtualKey.LeftShift
            or Windows.System.VirtualKey.RightShift)
        {
            return (mods, 0);
        }

        return (mods, vk);
    }
}

/// <summary>Hooks a native window message via subclassing.</summary>
internal sealed class NativeMessageHook : IDisposable
{
    private delegate IntPtr WNDPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    private readonly IntPtr _hwnd;
    private readonly uint _targetMsg;
    private readonly Action<IntPtr, IntPtr> _handler;
    private readonly SubclassProc _subclassDelegate;

    public NativeMessageHook(IntPtr hwnd, uint targetMsg, Action<IntPtr, IntPtr> handler)
    {
        _hwnd = hwnd;
        _targetMsg = targetMsg;
        _handler = handler;
        _subclassDelegate = SubclassCallback;
        SetWindowSubclass(hwnd, _subclassDelegate, 1, 0);
    }

    private IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == _targetMsg)
            _handler(wParam, lParam);
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        RemoveWindowSubclass(_hwnd, _subclassDelegate, 1);
    }
}
