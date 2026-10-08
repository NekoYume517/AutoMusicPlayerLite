using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace AutoMusicPlayer;
public sealed record RestartStatus(string Token, string State, int ProcessId, bool Elevated, string Error = "");
public sealed record RestartResult(bool Started, bool Cancelled, string Message);
public static class ElevationHandoff
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static bool ShouldAutoElevate(bool elevated, bool preferAdmin, bool testMode) => !elevated && preferAdmin && !testMode;
    public static bool ShouldRemind(bool elevated, bool suppressReminder, bool testMode) => !elevated && !suppressReminder && !testMode;
    public static string TicketPath(string data, string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new InvalidDataException("Invalid restart token");
        return Path.Combine(Path.GetFullPath(data), "restarts", token + ".json");
    }
    public static RestartStatus? Read(string data, string token)
    {
        try { return JsonSerializer.Deserialize<RestartStatus>(File.ReadAllText(TicketPath(data, token))); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public static void Report(string data, string token, string state, string error = "")
    {
        string path = TicketPath(data, token); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RestartStatus(token, state, Environment.ProcessId, IsAdministrator(), error)));
        File.Move(temporary, path, true);
    }
    public static ProcessStartInfo StartInfo(string executable, string directory, string token, bool setDefault, bool recordingDriver, bool probe = false)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = !probe, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        if (!probe) start.Verb = "runas";
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(directory));
        start.ArgumentList.Add("--restart-token"); start.ArgumentList.Add(token);
        if (setDefault) start.ArgumentList.Add("--set-default-admin");
        if (recordingDriver) start.ArgumentList.Add("--test-driver");
        if (probe) start.ArgumentList.Add("--restart-probe");
        return start;
    }
    public static async Task<RestartResult> Start(ProcessStartInfo start, string directory, string token, Action release, Func<bool> reacquire,
        Func<ProcessStartInfo, Process?>? launcher = null, TimeSpan? timeout = null, bool requireAdmin = true)
    {
        Process? child = null; bool success = false;
        Report(directory, token, "requested");
        release();
        try
        {
            child = await Task.Run(() => (launcher ?? Process.Start)(start));
            if (child is null) return new(false, false, "系统未创建新的播放器窗口，当前窗口已保留。");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(45)))
            {
                var status = Read(directory, token);
                if (status?.Token == token && status.ProcessId == child.Id)
                {
                    if (status.State == "ready" && (!requireAdmin || status.Elevated))
                    { success = true; return new(true, false, "管理员窗口已就绪。"); }
                    if (status.State == "failed") return new(false, false, "新窗口启动失败，当前窗口已保留：" + status.Error);
                }
                if (child.HasExited) return new(false, false, "新窗口在准备就绪前退出，当前窗口已保留。");
                await Task.Delay(120);
            }
            Report(directory, token, "cancelled");
            return new(false, false, "新窗口启动超时，当前窗口已保留。");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return new(false, true, "已取消管理员授权，继续以普通身份运行。"); }
        catch (Exception error) { return new(false, false, "无法启动管理员窗口，当前窗口已保留：" + error.Message); }
        finally
        {
            if (!success)
            {
                if (child is not null && !child.HasExited)
                {
                    Report(directory, token, "cancelled");
                    var recovery = Stopwatch.StartNew();
                    while (!reacquire() && recovery.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(120);
                }
                else reacquire();
                if (child is null || child.HasExited)
                    try { File.Delete(TicketPath(directory, token)); } catch (IOException) { }
            }
            child?.Dispose();
        }
    }
}
