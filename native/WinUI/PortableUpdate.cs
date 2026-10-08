using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AutoMusicPlayer;

internal static class PortableUpdate
{
    private static string? Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static void ValidateTarget(string target)
    {
        target = Path.GetFullPath(target);
        string source = Path.GetFullPath(DistributionMode.Executable);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target) ||
            !string.Equals(FileVersionInfo.GetVersionInfo(source).ProductName, FileVersionInfo.GetVersionInfo(target).ProductName, StringComparison.Ordinal))
            throw new InvalidDataException("目标文件不是同一版本类型的播放器 EXE。");
        if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly)) throw new IOException("当前绿色版文件为只读，请先取消只读属性或手动下载新版。");
        string probe = Path.Combine(Path.GetDirectoryName(target)!, ".amp-write-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    internal static ProcessStartInfo StartInfo(string downloaded, string current, string data, int processId)
    {
        ValidateTarget(current);
        var start = new ProcessStartInfo(Path.GetFullPath(downloaded)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(downloaded))! };
        start.ArgumentList.Add("--portable-update-target"); start.ArgumentList.Add(Path.GetFullPath(current));
        start.ArgumentList.Add("--portable-update-wait-pid"); start.ArgumentList.Add(processId.ToString());
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(data));
        return start;
    }

    internal static void ReportReady(string[] args, string data)
    {
        string? token = Argument(args, "--portable-ready-token");
        if (token is not null) ElevationHandoff.Report(data, token, "ready");
    }

    // Runs before acquiring the library's instance lease. The downloaded EXE is the updater.
    internal static int? TryRun(string[] args, string data)
    {
        string? rawTarget = Argument(args, "--portable-update-target");
        if (rawTarget is null) return null;
        string target = Path.GetFullPath(rawTarget), source = Path.GetFullPath(DistributionMode.Executable);
        bool testing = args.Contains("--test-driver") && args.Contains("--portable-update-test");
        string? report = testing ? Argument(args, "--portable-update-test-report") : null;
        string backup = Path.Combine(Path.GetDirectoryName(target)!, ".amp-previous-" + Guid.NewGuid().ToString("N") + ".tmp");
        string stage = Path.Combine(Path.GetDirectoryName(target)!, ".amp-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        string token = Guid.NewGuid().ToString("N");
        Process? child = null;
        bool replaced = false, succeeded = false, restored = false;
        string message = "";
        try
        {
            if (!DistributionMode.IsPortable || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("绿色版更新需要独立的已下载 EXE。");
            ValidateTarget(target);
            if (!int.TryParse(Argument(args, "--portable-update-wait-pid"), out int parent) || parent <= 0 || parent == Environment.ProcessId)
                throw new InvalidDataException("更新进程参数无效。");
            try
            {
                using var old = Process.GetProcessById(parent);
                if (!old.WaitForExit(45000)) throw new IOException("旧播放器仍在运行，未替换程序文件。");
            }
            catch (ArgumentException) { } // Parent already exited.
            File.Copy(source, stage, false);
            var unlock = Stopwatch.StartNew();
            while (true)
            {
                try { File.Move(target, backup, false); break; }
                catch (IOException) when (unlock.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(120); }
            }
            try { File.Move(stage, target, false); replaced = true; }
            catch { File.Move(backup, target, false); throw; }
            var start = PlayerStart(target, data, testing);
            start.ArgumentList.Add("--portable-ready-token"); start.ArgumentList.Add(token);
            if (testing && args.Contains("--portable-update-test-fail-ready")) start.ArgumentList.Add("--portable-update-test-fail-ready");
            child = Process.Start(start) ?? throw new IOException("系统未启动新版播放器。");
            var ready = Stopwatch.StartNew();
            while (ready.Elapsed < TimeSpan.FromSeconds(45))
            {
                var status = ElevationHandoff.Read(data, token);
                if (status?.State == "ready" && status.ProcessId == child.Id) { succeeded = true; break; }
                if (child.HasExited) throw new IOException("新版播放器在准备就绪前退出。");
                Thread.Sleep(100);
            }
            if (!succeeded) throw new IOException("新版播放器启动超时。");
        }
        catch (Exception error)
        {
            message = error.Message;
            if (replaced && File.Exists(backup))
            {
                if (child is not null && !child.HasExited) { child.Kill(true); child.WaitForExit(10000); }
                try
                {
                    // Windows may retain the executable image briefly after process exit.
                    var unlock = Stopwatch.StartNew();
                    while (true)
                    {
                        try { File.Move(target, stage, false); break; }
                        catch (Exception lockError) when ((lockError is IOException or UnauthorizedAccessException) && unlock.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(120); }
                    }
                    try { File.Move(backup, target, false); restored = true; }
                    catch { File.Move(stage, target, false); throw; }
                    using var rollback = Process.Start(PlayerStart(target, data, testing));
                    if (testing) rollback?.WaitForExit(15000);
                }
                catch (Exception rollbackError) { message += "\n恢复原程序时遇到问题：" + rollbackError.Message; }
            }
            if (!testing) MessageBox(0, "绿色版更新未能完成：" + message + (restored ? "\n已恢复原来的程序文件。" : replaced ? "\n原文件保存在：" + backup + "\n请关闭播放器后用该文件恢复。" : "\n原程序文件未更改。"), "自动演奏器 Lite", 0x10);
        }
        finally
        {
            try { if (File.Exists(stage)) File.Delete(stage); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            try { if (succeeded && File.Exists(backup)) File.Delete(backup); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            try { File.Delete(ElevationHandoff.TicketPath(data, token)); } catch (IOException) { }
            child?.Dispose();
            if (report is not null) File.WriteAllText(report, JsonSerializer.Serialize(new { passed = succeeded, restored, message, target, source }));
        }
        return succeeded ? 0 : 1;
    }

    private static ProcessStartInfo PlayerStart(string target, string data, bool testing)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data);
        if (testing) { start.ArgumentList.Add("--test-driver"); start.ArgumentList.Add("--portable-ready-exit"); }
        return start;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string title, uint type);
}
