using Microsoft.UI.Xaml;
namespace AutoMusicPlayer;
public partial class App : Application
{
    private MainWindow? window;
    private DispatcherTimer? restartCancellation;
    public string DataDirectory { get; }
    public InstanceLease Instance { get; }
    public App()
    {
        var args = Environment.GetCommandLineArgs();
        DataDirectory = Path.GetFullPath(ReadArg(args, "--data-dir") ?? (args.Contains("--self-test")
            ? Path.Combine(Path.GetTempPath(), "AutoMusicPlayerLite-test-" + Guid.NewGuid().ToString("N"))
            : BackendClient.DefaultDataDirectory));
        Directory.CreateDirectory(DataDirectory);
        int? portableUpdate = PortableUpdate.TryRun(args, DataDirectory);
        if (portableUpdate is int exitCode) Environment.Exit(exitCode);
        Instance = new InstanceLease(DataDirectory);
        if (!Instance.Owned) { Environment.Exit(0); return; }
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(DataDirectory, "ui-errors.log"), $"{DateTimeOffset.Now:O} {e.Message}\n{e.Exception}\n");
            string? token = ReadArg(args, "--restart-token");
            if (token is not null)
            {
                Instance.Release();
                try { ElevationHandoff.Report(DataDirectory, token, "failed", e.Message); } catch (Exception) { }
            }
        };
    }
    private static string? ReadArg(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Closed += (_, _) => { restartCancellation?.Stop(); Instance.Dispose(); Exit(); };
        string? token = ReadArg(Environment.GetCommandLineArgs(), "--restart-token");
        if (token is not null)
        {
            restartCancellation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            restartCancellation.Tick += (_, _) =>
            {
                if (ElevationHandoff.Read(DataDirectory, token)?.State != "cancelled") return;
                restartCancellation.Stop(); Instance.Release(); window.Close();
            };
            restartCancellation.Start();
        }
        window.Activate();
    }
}
