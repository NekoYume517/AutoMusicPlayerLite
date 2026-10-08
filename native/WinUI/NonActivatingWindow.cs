using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
namespace AutoMusicPlayer;

public abstract class NonActivatingWindow : Window
{
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint reference);
    private SubclassProc? subclass;
    public nint Hwnd => WindowNative.GetWindowHandle(this);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    internal bool DoesNotActivate => (GetWindowLongPtr(Hwnd, -20).ToInt64() & 0x08000000L) != 0 && SendMessageW(Hwnd, 0x21, 0, 0) == 3;
    internal bool HasRendered => ((FrameworkElement)Content).ActualHeight > 90;
    protected void Configure(int width, int height, NonActivatingWindow? owner = null)
    {
        double scale = Math.Max(96, GetDpiForWindow(Hwnd)) / 96.0;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; }
        long style = GetWindowLongPtr(Hwnd, -20).ToInt64();
        SetWindowLongPtr(Hwnd, -20, (nint)(style | 0x08000000L | 0x00000080L));
        if (owner is not null) SetWindowLongPtr(Hwnd, -8, owner.Hwnd);
        subclass = (hwnd, msg, wp, lp, id, reference) => msg == 0x21 ? 3 : DefSubclassProc(hwnd, msg, wp, lp);
        if (!SetWindowSubclass(Hwnd, subclass, 1, 0)) throw new InvalidOperationException("未能设置窗口焦点保护");
        Closed += (_, _) => RemoveWindowSubclass(Hwnd, subclass, 1);
    }
    public void ShowWithoutActivation() => AppWindow.Show(false);
}
