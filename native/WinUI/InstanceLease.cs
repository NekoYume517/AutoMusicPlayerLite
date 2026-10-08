using System.Security.Cryptography;
using System.Text;

namespace AutoMusicPlayer;
public sealed class InstanceLease : IDisposable
{
    private readonly Mutex mutex;
    public bool Owned { get; private set; }
    public InstanceLease(string directory)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        mutex = new Mutex(false, "Local\\AutoMusicPlayerLite_" + hash);
        Acquire();
    }
    public bool Acquire()
    {
        if (Owned) return true;
        try { Owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Owned = true; }
        return Owned;
    }
    public void Release()
    {
        if (!Owned) return;
        mutex.ReleaseMutex(); Owned = false;
    }
    public void Dispose() { Release(); mutex.Dispose(); }
}
