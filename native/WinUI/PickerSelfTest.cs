using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace AutoMusicPlayer;

// Opt-in integration check of our own modal dialogs; never generates input.
internal static class PickerSelfTest
{
    private delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);

    public static async Task Check(WindowId owner)
    {
        await CheckDialog(async () => (await new FileOpenPicker(owner) { FileTypeFilter = { ".json", ".mid", ".midi" } }.PickMultipleFilesAsync()).Count == 0);
        var save = new FileSavePicker(owner) { SuggestedFileName = "picker-self-test", DefaultFileExtension = ".json" };
        save.FileTypeChoices.Add("JSON 乐谱", new List<string> { ".json" });
        save.FileTypeChoices.Add("MIDI 乐谱", new List<string> { ".mid", ".midi" });
        await CheckDialog(async () => await save.PickSaveFileAsync() is null);
        await CheckDialog(async () => await new FolderPicker(owner).PickSingleFolderAsync() is null);
    }

    private static async Task CheckDialog(Func<Task<bool>> show)
    {
        bool observed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        EnumCallback callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId != Environment.ProcessId) return true;
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            observed = true;
            PostMessage(hwnd, 0x0010, 0, 0); // Close only this test process's file dialog.
            return true;
        };
        timer.Tick += (_, _) => EnumWindows(callback, 0);
        timer.Start();
        try
        {
            if (!await show() || !observed) throw new InvalidOperationException("File picker did not show or cancel correctly");
        }
        finally { timer.Stop(); GC.KeepAlive(callback); }
    }
}
