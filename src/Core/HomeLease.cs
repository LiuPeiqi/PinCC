using System.Security.Cryptography;
using System.Text;

namespace PinCC.Core;

/// <summary>Coordinates file maintenance with writers in other PinCC processes using the same home.</summary>
internal sealed class HomeLease : IDisposable
{
    readonly Mutex _mutex;
    HomeLease(Mutex mutex) => _mutex = mutex;

    public static HomeLease? TryEnter(string home, string purpose, int waitMs = 0)
    {
        var path = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        var mutex = new Mutex(false, @"Local\PinCC." + purpose + "." + hash);
        bool held;
        try { held = mutex.WaitOne(waitMs); }
        catch (AbandonedMutexException) { held = true; }
        catch { mutex.Dispose(); throw; }
        if (held) return new HomeLease(mutex);
        mutex.Dispose();
        return null;
    }

    public static HomeLease EnterImages(string home) => TryEnter(home, "Images", 250)
        ?? throw new MaintenanceBusyException();

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}

public sealed class MaintenanceBusyException() : IOException("图片维护正忙，请稍后重试");
