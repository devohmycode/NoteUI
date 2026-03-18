using System.Runtime.InteropServices;
using System.Text;

namespace NoteUI;

public class TextExpansionService
{
    private readonly SnippetManager _snippetManager;
    private readonly StringBuilder _buffer = new(64);
    private IntPtr _hookId;
    private LowLevelKeyboardProc? _hookProc;
    private bool _isSendingInput;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint INPUT_KEYBOARD = 1;
    private const uint LLKHF_INJECTED = 0x00000010;

    public TextExpansionService(SnippetManager snippetManager)
    {
        _snippetManager = snippetManager;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookProc = HookCallback;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(module.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && !_isSendingInput)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip injected keystrokes (our own SendInput)
            if ((kb.flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var vk = kb.vkCode;

            // Trigger keys: Space, Tab, Enter
            if (vk == 0x20 || vk == 0x09 || vk == 0x0D)
            {
                var keyword = _buffer.ToString();
                _buffer.Clear();

                if (!string.IsNullOrEmpty(keyword))
                {
                    var snippet = _snippetManager.FindByTrigger(keyword);
                    if (snippet != null)
                    {
                        ExpandSnippet(keyword, snippet.Content);
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Backspace: remove last char from buffer
            if (vk == 0x08)
            {
                if (_buffer.Length > 0)
                    _buffer.Remove(_buffer.Length - 1, 1);
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Escape, arrows, modifiers, etc. -> clear buffer
            if (vk < 0x20 || vk >= 0xA0)
            {
                // Allow shift (0x10) through without clearing
                if (vk != 0x10 && vk != 0x11 && vk != 0x12)
                    _buffer.Clear();
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Convert VK to character using current keyboard layout
            var ch = VkToChar(vk);
            if (ch != '\0')
            {
                if (_buffer.Length >= 64)
                    _buffer.Remove(0, 1);
                _buffer.Append(ch);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ExpandSnippet(string keyword, string content)
    {
        _isSendingInput = true;
        try
        {
            // Send Backspace x keyword length to erase the keyword
            var backspaces = keyword.Length;
            var bsInputs = new INPUT[backspaces * 2];
            for (int i = 0; i < backspaces; i++)
            {
                bsInputs[i * 2].type = INPUT_KEYBOARD;
                bsInputs[i * 2].ki.wVk = 0x08; // VK_BACK
                bsInputs[i * 2 + 1].type = INPUT_KEYBOARD;
                bsInputs[i * 2 + 1].ki.wVk = 0x08;
                bsInputs[i * 2 + 1].ki.dwFlags = KEYEVENTF_KEYUP;
            }
            SendInput((uint)bsInputs.Length, bsInputs, Marshal.SizeOf<INPUT>());

            // Send content as Unicode characters
            var chars = content.ToCharArray();
            var charInputs = new INPUT[chars.Length * 2];
            for (int i = 0; i < chars.Length; i++)
            {
                charInputs[i * 2].type = INPUT_KEYBOARD;
                charInputs[i * 2].ki.wScan = chars[i];
                charInputs[i * 2].ki.dwFlags = KEYEVENTF_UNICODE;

                charInputs[i * 2 + 1].type = INPUT_KEYBOARD;
                charInputs[i * 2 + 1].ki.wScan = chars[i];
                charInputs[i * 2 + 1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            }
            SendInput((uint)charInputs.Length, charInputs, Marshal.SizeOf<INPUT>());
        }
        finally
        {
            _isSendingInput = false;
        }
    }

    private static char VkToChar(uint vk)
    {
        var keyboardState = new byte[256];
        GetKeyboardState(keyboardState);

        var scanCode = MapVirtualKey(vk, 0);
        var sb = new StringBuilder(4);
        var result = ToUnicode(vk, scanCode, keyboardState, sb, sb.Capacity, 0);
        return result == 1 ? sb[0] : '\0';
    }

    // -- P/Invoke --

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags);
}
