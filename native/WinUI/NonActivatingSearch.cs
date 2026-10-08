using System.Runtime.InteropServices;
using System.Text;
namespace AutoMusicPlayer;
/// <summary>Explicit, temporary search input while the game's HWND remains foreground.</summary>
internal sealed class NonActivatingSearch : IDisposable
{
    private delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint vk, uint scan, byte[] keys, StringBuilder text, int size, uint flags, nint layout);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    private readonly HookProc callback;
    private readonly Action<string> action;
    private readonly HashSet<uint> swallowed = [];
    private readonly HashSet<uint> existing = [];
    private nint hook;
    private nint foreground;
    public bool IsActive => hook != 0;
    public NonActivatingSearch(Action<string> action) { this.action = action; callback = Filter; }
    public void Begin()
    {
        End(); foreground = GetForegroundWindow();
        for (uint k = 8; k < 256; k++) if ((GetAsyncKeyState((int)k) & 0x8000) != 0) existing.Add(k);
        hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法开启小窗搜索输入");
    }
    private nint Filter(int code, nuint message, nint data)
    {
        if (code < 0 || !IsActive) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardData>(data);
        if ((key.Flags & 0x10) != 0) return CallNextHookEx(hook, code, message, data);
        if (GetForegroundWindow() != foreground) { action("end"); return CallNextHookEx(hook, code, message, data); }
        bool up = message is 0x101 or 0x105;
        if (existing.Contains(key.Vk)) { if (up) existing.Remove(key.Vk); return CallNextHookEx(hook, code, message, data); }
        if (up) return swallowed.Remove(key.Vk) ? 1 : CallNextHookEx(hook, code, message, data);
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        string? command = null;
        if (alt || key.Vk is 0x5B or 0x5C) { action("end"); return CallNextHookEx(hook, code, message, data); }
        if (key.Vk is 0x75 or 0x77) { action("end"); return CallNextHookEx(hook, code, message, data); }
        if (key.Vk is 0x1B or 0x0D) command = "end";
        else if (key.Vk == 0x08) command = "backspace";
        else if (key.Vk == 0x2E || ctrl && key.Vk == 0x41) command = "clear";
        else if (ctrl && key.Vk == 0x56) command = "paste";
        else if (key.Vk is 0x26 or 0x28) command = key.Vk == 0x26 ? "previous" : "next";
        else if (!ctrl && key.Vk >= 0x20)
        {
            var state = new byte[256]; GetKeyboardState(state);
            for (int k = 0; k < 256; k++) if ((GetAsyncKeyState(k) & 0x8000) != 0) state[k] |= 0x80;
            var text = new StringBuilder(8);
            uint thread = GetWindowThreadProcessId(foreground, out _);
            int count = ToUnicodeEx(key.Vk, key.Scan, state, text, text.Capacity, 4, GetKeyboardLayout(thread));
            if (count > 0) command = "text:" + text.ToString(0, count);
        }
        if (command is null) return CallNextHookEx(hook, code, message, data);
        swallowed.Add(key.Vk); action(command); return 1;
    }
    public void End() { if (hook != 0) UnhookWindowsHookEx(hook); hook = 0; swallowed.Clear(); existing.Clear(); }
    public void Dispose() => End();
    internal static void VerifyCapture()
    {
        // Exercise the real hook filter without injecting any OS keyboard input.
        var commands = new List<string>();
        using var input = new NonActivatingSearch(commands.Add);
        nint before = GetForegroundWindow();
        input.Begin();
        nint data = Marshal.AllocHGlobal(Marshal.SizeOf<KeyboardData>());
        try
        {
            uint vk = Enumerable.Range(0x41, 26).Select(k => (uint)k).First(k => (GetAsyncKeyState((int)k) & 0x8000) == 0);
            Marshal.StructureToPtr(new KeyboardData { Vk = vk, Scan = vk == 0x41 ? 0x1Eu : 0 }, data, false);
            nint down = input.Filter(0, 0x100, data);
            nint up = input.Filter(0, 0x101, data);
            if (down != 1 || up != 1 || !commands.Any(c => c.StartsWith("text:"))) throw new Exception("search input was not captured and suppressed");
            int count = commands.Count;
            Marshal.StructureToPtr(new KeyboardData { Vk = vk, Flags = 0x10 }, data, false);
            input.Filter(0, 0x100, data);
            if (commands.Count != count) throw new Exception("search captured playback-injected input");
            if (GetForegroundWindow() != before) throw new Exception("search hook changed foreground window");
        }
        finally { Marshal.FreeHGlobal(data); input.End(); }
    }
}
