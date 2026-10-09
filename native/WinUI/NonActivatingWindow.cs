using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using WinRT.Interop;
namespace AutoMusicPlayer;

public abstract class NonActivatingWindow : Window
{
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint reference);
    private SubclassProc? subclass;
    private FrameworkElement? dragRegion;
    private Button? closeButton;
    private readonly DispatcherTimer dragTimer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private bool dragging;
    private double grabX, grabY;
    private nint dragForeground;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    public nint Hwnd => WindowNative.GetWindowHandle(this);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out uint value, int size);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    internal bool DoesNotActivate => (GetWindowLongPtr(Hwnd, -20).ToInt64() & 0x08000000L) != 0 && SendMessageW(Hwnd, 0x21, 0, 0) == 3;
    internal bool HasRendered => ((FrameworkElement)Content).ActualHeight > 90;

    protected Grid CreateHeader(string text, double fontSize = 11, string? subtitle = null)
    {
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = text, FontSize = fontSize, Opacity = subtitle is null ? .6 : 1,
            FontWeight = subtitle is null ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold });
        if (subtitle is not null) caption.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = .65 });
        var drag = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        drag.Children.Add(caption); header.Children.Add(drag); dragRegion = drag;
        drag.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint((UIElement)Content);
            if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed) return;
            grabX = point.Position.X; grabY = point.Position.Y;
            dragForeground = NonActivatingSearch.GetForegroundWindow(); dragging = true;
            dragTimer.Start(); e.Handled = true;
        };
        drag.PointerMoved += (_, _) => MoveDrag();
        drag.PointerReleased += (_, e) => { if (!e.GetCurrentPoint(drag).Properties.IsLeftButtonPressed) StopDrag(); };
        // Background windows cannot reliably retain mouse capture outside their bounds.
        // Read the cursor only during a header drag, so fast movements keep following it
        // without activating the window or interfering with the game's keyboard shortcuts.
        dragTimer.Tick += (_, _) => MoveDrag();
        ToolTipService.SetToolTip(drag, "拖动移动窗口");
        closeButton = new Button { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 10 }, Width = 26, Height = 26,
            MinWidth = 26, MinHeight = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            IsTabStop = false, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(closeButton, "关闭" + text);
        ToolTipService.SetToolTip(closeButton, "关闭" + text);
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 1); header.Children.Add(closeButton);
        return header;
    }

    protected void Configure(int width, int height, NonActivatingWindow? owner = null)
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        // Also remove the legacy frame styles retained by the WinUI presenter.
        long frameStyle = GetWindowLongPtr(Hwnd, -16).ToInt64();
        SetWindowLongPtr(Hwnd, -16, (nint)(frameStyle & ~0x00C40000L));
        SetWindowPos(Hwnd, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, without moving, resizing or activating.
        // Windows 11 supports rounded corners without the contrasting one-pixel DWM border.
        // Older Windows versions keep their borderless rectangular frame.
        uint corner = 2, border = 0xFFFFFFFE;
        DwmSetWindowAttribute(Hwnd, 33, ref corner, sizeof(uint));
        DwmSetWindowAttribute(Hwnd, 34, ref border, sizeof(uint));
        double scale = Math.Max(96, GetDpiForWindow(Hwnd)) / 96.0;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.IsShownInSwitchers = false;
        long style = GetWindowLongPtr(Hwnd, -20).ToInt64();
        SetWindowLongPtr(Hwnd, -20, (nint)(style | 0x08000000L | 0x00000080L));
        if (owner is not null) SetWindowLongPtr(Hwnd, -8, owner.Hwnd);
        subclass = (hwnd, msg, wp, lp, id, reference) =>
        {
            if (msg == 0x21) return 3; // MA_NOACTIVATE; WinUI receives header pointer input as client input.
            return DefSubclassProc(hwnd, msg, wp, lp);
        };
        if (!SetWindowSubclass(Hwnd, subclass, 1, 0)) throw new InvalidOperationException("未能设置窗口焦点保护");
        Closed += (_, _) => { StopDrag(); RemoveWindowSubclass(Hwnd, subclass, 1); };
    }

    private void StopDrag() { dragging = false; dragTimer.Stop(); }

    private void MoveDrag()
    {
        if (!dragging) return;
        if ((GetAsyncKeyState(1) & 0x8000) == 0 || NonActivatingSearch.GetForegroundWindow() != dragForeground || !GetCursorPos(out var point))
        {
            StopDrag(); return;
        }
        double scale = Math.Max(96, GetDpiForWindow(Hwnd)) / 96.0;
        int x = point.X - (int)Math.Round(grabX * scale), y = point.Y - (int)Math.Round(grabY * scale);
        if (!GetWindowRect(Hwnd, out var current) || current.Left == x && current.Top == y) return;
        if (!SetWindowPos(Hwnd, 0, x, y, 0, 0, 0x15)) StopDrag(); // NOSIZE | NOZORDER | NOACTIVATE.
    }

    internal void CheckChrome()
    {
        if ((GetWindowLongPtr(Hwnd, -16).ToInt64() & 0x00C00000L) != 0) throw new Exception("mini system caption or border is still present: " + GetWindowLongPtr(Hwnd, -16).ToInt64().ToString("X"));
        if (!GetWindowRect(Hwnd, out var frame) || !GetClientRect(Hwnd, out var client) ||
            frame.Right - frame.Left != client.Right || frame.Bottom - frame.Top != client.Bottom)
            throw new Exception("mini non-client frame still occupies space");
        if (DwmGetWindowAttribute(Hwnd, 34, out uint border, sizeof(uint)) >= 0 && border != 0xFFFFFFFE)
            throw new Exception("mini DWM border was not suppressed");
        if (dragRegion is null || closeButton is null || dragRegion.ActualWidth <= 0 || HitTestCenter(dragRegion) != 1 || HitTestCenter(closeButton) != 1)
            throw new Exception("mini header must deliver client pointer input to WinUI");
        var root = (FrameworkElement)Content;
        var dragOrigin = dragRegion.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        var closeOrigin = closeButton.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        if (dragOrigin.X + dragRegion.ActualWidth > closeOrigin.X) throw new Exception("mini close button overlaps the drag region");
        if (!DoesNotActivate) throw new Exception("borderless window lost focus protection");
    }

    private nint HitTestCenter(FrameworkElement element)
    {
        var root = (FrameworkElement)Content;
        var origin = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        double scale = root.XamlRoot.RasterizationScale;
        var point = new NativePoint { X = (int)((origin.X + element.ActualWidth / 2) * scale), Y = (int)((origin.Y + element.ActualHeight / 2) * scale) };
        if (!ClientToScreen(Hwnd, ref point)) throw new Exception("window coordinates unavailable");
        nint packed = (nint)(unchecked((ushort)point.X) | unchecked((ushort)point.Y) << 16);
        return SendMessageW(Hwnd, 0x84, 0, packed);
    }

    internal Windows.Graphics.PointInt32 DragCenterForTest()
    {
        var root = (FrameworkElement)Content;
        var element = dragRegion ?? throw new Exception("Drag region missing");
        var origin = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        double scale = root.XamlRoot.RasterizationScale;
        var point = new NativePoint { X = (int)((origin.X + element.ActualWidth / 2) * scale), Y = (int)((origin.Y + element.ActualHeight / 2) * scale) };
        if (!ClientToScreen(Hwnd, ref point)) throw new Exception("Drag region coordinates missing");
        return new Windows.Graphics.PointInt32(point.X, point.Y);
    }

    internal void InvokeCloseForTest()
    {
        if (closeButton is null) throw new Exception("custom window close button missing");
        ((IInvokeProvider)new ButtonAutomationPeer(closeButton).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    public void ShowWithoutActivation() => AppWindow.Show(false);
}
