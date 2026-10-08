using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AutoMusicPlayer;
public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instanceMutex;
    public App()
    {
        var args = Environment.GetCommandLineArgs();
        int wait = Array.IndexOf(args, "--wait-for-exit");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out int pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); } catch (ArgumentException) { }
        }
        int data = Array.IndexOf(args, "--data-dir");
        string key = data >= 0 && data + 1 < args.Length ? args[data + 1] : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..24];
        instanceMutex = new Mutex(true, "Local\\AutoMusicPlayerLite_" + hash, out bool created);
        if (!created) { Environment.Exit(0); return; }
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoMusicPlayerLite");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ui-errors.log"), $"{DateTimeOffset.Now:O} {e.Message}\n{e.Exception}\n");
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
