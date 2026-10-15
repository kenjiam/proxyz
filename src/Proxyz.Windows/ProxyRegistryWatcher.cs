using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Proxyz.Windows;

/// <summary>Event-driven observation only; registry is never used to write proxy settings.</summary>
public sealed class ProxyRegistryWatcher : IDisposable
{
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private bool _disposed;
    private string _notice = "";
    public string Notice => Volatile.Read(ref _notice);
    public ProxyRegistryWatcher(Action changed)
    {
        _thread = new Thread(() => Watch(changed)) { IsBackground = true, Name = "Proxyz.ProxySettingsWatch" };
        _thread.Start();
    }
    private void Watch(Action changed)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
            if (key is null) { Fail("プロキシ変更の監視先がありません。必要に応じて状態を更新してください。", changed); return; }
            using var notification = new AutoResetEvent(false);
            while (!_stop.WaitOne(0))
            {
                // KEY_NOTIFY / REG_NOTIFY_CHANGE_LAST_SET + REG_NOTIFY_CHANGE_NAME.
                int error = RegNotifyChangeKeyValue(key.Handle, true, 0x00000004 | 0x00000001, notification.SafeWaitHandle, true);
                if (error != 0) { Fail($"プロキシ変更通知を購読できません（Windows エラー {error}）。必要に応じて状態を更新してください。", changed); return; }
                if (WaitHandle.WaitAny([_stop, notification]) == 0) return;
                try { changed(); } catch { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Fail("プロキシ変更の監視を開始できません。適用前の現在値確認は行いますが、必要に応じて状態を更新してください。", changed); }
    }
    private void Fail(string message, Action changed)
    {
        Volatile.Write(ref _notice, message);
        try { changed(); } catch { }
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool subtree,
        uint filter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        // Native call is asynchronous. The worker always wakes on _stop.
        _thread.Join();
        _stop.Dispose();
    }
}
