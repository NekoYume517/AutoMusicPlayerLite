using System.ComponentModel;
using System.Runtime.InteropServices;
namespace AutoMusicPlayer;

// Exercises OS mouse delivery to this application's own window, including WinUI's child HWND.
internal static class WindowDragSelfTest
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    internal static async Task Verify(NonActivatingWindow window)
    {
        if (!Environment.GetCommandLineArgs().Contains("--self-test")) throw new InvalidOperationException("Mouse probe requires isolated self-test mode");
        nint hwnd = window.Hwnd, foreground = NonActivatingSearch.GetForegroundWindow();
        var center = window.DragCenterForTest();
        if (!GetWindowRect(hwnd, out var before)) throw new Win32Exception();
        await Task.Run(() =>
        {
            if ((GetAsyncKeyState(1) & 0x8000) != 0) throw new Exception("Mouse probe cancelled: left button already pressed");
            if (!GetCursorPos(out var saved)) throw new Win32Exception();
            var last = saved;
            bool down = false;
            try
            {
                SetCursorPos(center.X, center.Y); last = new Point { X = center.X, Y = center.Y };
                Thread.Sleep(35);
                if (GetAncestor(WindowFromPoint(last), 2) != hwnd) throw new Exception("Mouse probe target is obscured");
                if (SendInput(1, [new Input { Mouse = new MouseInput { Flags = 2 } }], Marshal.SizeOf<Input>()) != 1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse probe press failed");
                down = true; Thread.Sleep(50);
                foreach (int step in new[] { 1, 2, 3 })
                {
                    if (!GetCursorPos(out var current) || current.X != last.X || current.Y != last.Y || NonActivatingSearch.GetForegroundWindow() != foreground)
                        throw new Exception("Mouse probe interrupted or drag stole foreground");
                    last = new Point { X = center.X + step * 16, Y = center.Y + step * 8 };
                    SetCursorPos(last.X, last.Y); Thread.Sleep(50);
                }
                if (!GetWindowRect(hwnd, out var after) || after.Left - before.Left != 48 || after.Top - before.Top != 24)
                    throw new Exception("Real mouse drag did not move the window by 48 x 24 pixels");
                if (SendInput(1, [new Input { Mouse = new MouseInput { Flags = 4 } }], Marshal.SizeOf<Input>()) != 1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse probe release failed");
                down = false; Thread.Sleep(35);
                last.X += 24; SetCursorPos(last.X, last.Y); Thread.Sleep(35);
                if (!GetWindowRect(hwnd, out var released) || released.Left != after.Left || released.Top != after.Top)
                    throw new Exception("Header drag continued after the mouse button was released");
            }
            finally
            {
                if (down) SendInput(1, [new Input { Mouse = new MouseInput { Flags = 4 } }], Marshal.SizeOf<Input>());
                Thread.Sleep(35);
                SetWindowPos(hwnd, 0, before.Left, before.Top, 0, 0, 0x15);
                if (GetCursorPos(out var current) && current.X == last.X && current.Y == last.Y) SetCursorPos(saved.X, saved.Y);
            }
        });
        if (NonActivatingSearch.GetForegroundWindow() != foreground || !window.DoesNotActivate)
            throw new Exception("Real mouse drag lost foreground protection");
    }
}
