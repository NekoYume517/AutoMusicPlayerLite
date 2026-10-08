using AutoMusicPlayer;
using System.ComponentModel;
using System.Diagnostics;
int checks = 0;
void Check(bool condition) { if (!condition) throw new Exception("Elevation check failed: " + checks); checks++; }
string data = Path.Combine(Path.GetTempPath(), "AMP restart " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
try
{
    string token = Guid.NewGuid().ToString("N");
    var info = ElevationHandoff.StartInfo(Environment.ProcessPath!, data, token, true, true);
    Check(info.UseShellExecute && info.Verb == "runas" && info.ArgumentList.Contains(data) && info.ArgumentList.Contains("--set-default-admin") && info.ArgumentList.Contains("--test-driver"));
    Check(ElevationHandoff.ShouldAutoElevate(false, true, false) && !ElevationHandoff.ShouldAutoElevate(true, true, false) && !ElevationHandoff.ShouldAutoElevate(false, false, false));
    Check(ElevationHandoff.ShouldRemind(false, false, false) && !ElevationHandoff.ShouldRemind(false, true, false) && !ElevationHandoff.ShouldRemind(true, false, false));
    bool invalid = false; try { ElevationHandoff.TicketPath(data, "../outside"); } catch (InvalidDataException) { invalid = true; } Check(invalid);
    int released = 0, acquired = 0;
    var canceled = await ElevationHandoff.Start(info, data, token, () => released++, () => { acquired++; return true; }, _ => throw new Win32Exception(1223));
    Check(canceled.Cancelled && !canceled.Started && released == 1 && acquired == 1);
    var failed = await ElevationHandoff.Start(info, data, token, () => released++, () => { acquired++; return true; }, _ => throw new Win32Exception(2));
    Check(!failed.Started && !failed.Cancelled && acquired == 2);
    var empty = await ElevationHandoff.Start(info, data, token, () => released++, () => { acquired++; return true; }, _ => null);
    Check(!empty.Started && acquired == 3);
    ElevationHandoff.Report(data, token, "ready");
    Check(ElevationHandoff.Read(data, token)?.ProcessId == Environment.ProcessId);
    File.WriteAllText(ElevationHandoff.TicketPath(data, token), "partial json"); Check(ElevationHandoff.Read(data, token) is null);
    using var lease = new InstanceLease(data); Check(lease.Acquire()); lease.Release();
    bool otherAcquired = false;
    var thread = new Thread(() => { using var other = new InstanceLease(data); otherAcquired = other.Acquire(); }); thread.Start(); thread.Join();
    Check(otherAcquired); Check(lease.Acquire());
    Console.WriteLine($"{checks} elevation checks passed");
}
finally { Directory.Delete(data, true); }
