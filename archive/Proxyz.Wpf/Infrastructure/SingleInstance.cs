using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Proxyz.App.Infrastructure;

/// <summary>
/// A per-user file lock also excludes another RDP session. The optional local event
/// only requests foreground activation; it carries no commands or privileged data.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _file;
    private readonly EventWaitHandle _event;
    private RegisteredWaitHandle? _registration;
    private SingleInstance(FileStream file, EventWaitHandle signal) { _file = file; _event = signal; }
    public static SingleInstance? TryAcquire(string directory, bool demo)
    {
        Directory.CreateDirectory(directory);
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..20];
        string name = @"Local\Proxyz.Activate." + key + (demo ? ".Demo" : "");
        FileStream stream;
        try { stream = new(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            try { if (EventWaitHandle.TryOpenExisting(name, out var existing)) { using (existing) existing.Set(); } } catch (UnauthorizedAccessException) { }
            return null;
        }
        try { return new(stream, new EventWaitHandle(false, EventResetMode.AutoReset, name)); }
        catch { stream.Dispose(); throw; }
    }
    public void OnActivate(Action action) => _registration = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => action(), null, Timeout.Infinite, false);
    public void Dispose()
    {
        _registration?.Unregister(null);
        _event.Dispose(); _file.Dispose();
    }
}
