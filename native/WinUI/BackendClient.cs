using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace AutoMusicPlayer;
public sealed class BackendClient : IAsyncDisposable
{
    private Process? process;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private int sequence;
    private bool closing;
    public event Action<string, JsonElement>? Event;
    public string DataDirectory { get; }
    public BackendClient(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoMusicPlayerLitePublic");
        Directory.CreateDirectory(DataDirectory);
    }
    public void Start(bool testMode = false)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "backend", "AmpEngine.exe");
        if (!System.IO.File.Exists(exe)) throw new FileNotFoundException("找不到本地引擎，请重新安装。", exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(DataDirectory);
        if (testMode) start.ArgumentList.Add("--test-mode");
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!closing) FailAll(new IOException("本地引擎已退出，请重新打开播放器。"));
        };
        process.Start();
        _ = ReadOutput();
        _ = ReadErrors();
    }
    private async Task ReadErrors()
    {
        try
        {
            while (process is not null && await process.StandardError.ReadLineAsync() is { } line)
                await System.IO.File.AppendAllTextAsync(Path.Combine(DataDirectory, "engine-errors.log"), line + Environment.NewLine);
        }
        catch (Exception) { }
    }
    private async Task ReadOutput()
    {
        try
        {
            while (process is not null && await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var name))
                {
                    Event?.Invoke(name.GetString()!, root.GetProperty("data").Clone());
                }
                else if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && pending.TryRemove(id.GetInt32(), out var waiter))
                {
                    if (root.GetProperty("ok").GetBoolean()) waiter.TrySetResult(root.GetProperty("result").Clone());
                    else waiter.TrySetException(new InvalidOperationException(root.GetProperty("error").GetString()));
                }
            }
            if (!closing) FailAll(new IOException("本地引擎连接已中断。"));
        }
        catch (Exception e) { if (!closing) FailAll(e); }
    }
    private void FailAll(Exception e)
    {
        foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var tcs)) tcs.TrySetException(e);
        Event?.Invoke("error", JsonSerializer.SerializeToElement(e.Message));
    }
    public async Task<JsonElement> Call(string method, object? args = null, int seconds = 60)
    {
        if (process is null || process.HasExited) throw new IOException("本地引擎未运行。");
        int id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await writeLock.WaitAsync();
            try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, args })); await process.StandardInput.FlushAsync(); }
            finally { writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        if (closing) return;
        closing = true;
        if (process is not null && !process.HasExited)
        {
            try { await Call("stop", seconds: 3); } catch (Exception) { }
            try { await process.StandardInput.WriteLineAsync("{\"method\":\"shutdown\"}"); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception) { if (!process.HasExited) process.Kill(true); }
        }
        FailAll(new ObjectDisposedException(nameof(BackendClient)));
        process?.Dispose();
    }
}
