using System.Runtime.InteropServices;
using System.Text;
using Proxyz.Windows.Native;

namespace Proxyz.Windows;

public sealed record WifiIdentity(string Hex, string Label);
public sealed record WifiObservation(Dictionary<Guid, WifiIdentity> Interfaces, string Notice);

/// <summary>No scan, BSSID persistence, or location API workaround. Activated only after opt-in.</summary>
public sealed class WifiObserver : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _disposed;
    private readonly WlanNative.NotificationCallback _callback;
    public event Action? Changed;
    public WifiObserver() => _callback = OnNotification;

    public WifiObservation Read(bool consent)
    {
        lock (_gate)
        {
            var found = new Dictionary<Guid, WifiIdentity>();
            if (_disposed) return new(found, "Wi-Fi 監視は終了しています。");
            if (!consent)
            {
                Close();
                return new(found, "Wi-Fi 名による判定は無効です。利用する場合は設定から Wi-Fi の識別を有効にしてください。");
            }
            if (_handle == IntPtr.Zero)
            {
                uint open = WlanNative.WlanOpenHandle(2, IntPtr.Zero, out _, out _handle);
                if (open != 0) { _handle = IntPtr.Zero; return new(found, Explain(open)); }
                uint registration = WlanNative.WlanRegisterNotification(_handle, 8, true, _callback, IntPtr.Zero, IntPtr.Zero, out _);
                if (registration != 0) { Close(); return new(found, Explain(registration)); }
            }
            uint result = WlanNative.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list);
            if (result != 0) return new(found, Explain(result));
            string notice = "";
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 256) return new(found, "Wi-Fi 情報の件数が不正なため読み取りを停止しました。");
                int stride = Marshal.SizeOf<WlanNative.InterfaceInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanNative.InterfaceInfo>(IntPtr.Add(list, 8 + i * stride));
                    if (info.State != 1) continue; // wlan_interface_state_connected
                    var id = info.Id;
                    uint query = WlanNative.WlanQueryInterface(_handle, ref id, 7, IntPtr.Zero, out var size, out var data, out _);
                    if (query != 0) { notice = Explain(query); continue; }
                    try
                    {
                        if (size < Marshal.SizeOf<WlanNative.ConnectionAttributes>()) { notice = "Wi-Fi 情報の形式を確認できません。"; continue; }
                        var attrs = Marshal.PtrToStructure<WlanNative.ConnectionAttributes>(data);
                        if (attrs.State != 1 || attrs.Association.Ssid.Length is 0 or > 32) continue;
                        var bytes = attrs.Association.Ssid.Bytes.AsSpan(0, (int)attrs.Association.Ssid.Length).ToArray();
                        string hex = Convert.ToHexString(bytes);
                        string label;
                        try
                        {
                            label = new UTF8Encoding(false, true).GetString(bytes);
                            if (label.Any(char.IsControl)) label = "SSID（16進数）: " + hex;
                        }
                        catch (DecoderFallbackException) { label = "SSID（16進数）: " + hex; }
                        found[id] = new(hex, label);
                    }
                    finally { WlanNative.WlanFreeMemory(data); }
                }
            }
            finally { WlanNative.WlanFreeMemory(list); }
            return new(found, notice);
        }
    }
    private void OnNotification(ref WlanNative.NotificationData data, IntPtr context)
    {
        if (data.Source == 8 && data.Code is 10 or 11 or 13 or 14 or 20 or 21)
        {
            try { Changed?.Invoke(); } catch { /* Never unwind across a native callback. */ }
        }
    }
    private static string Explain(uint code) => code switch
    {
        5 => "Wi-Fi 情報へのアクセスが拒否されました。Windows の位置情報の許可を確認してください。手動切替は利用できます。",
        1062 => "Windows の WLAN AutoConfig サービスが停止しています。手動切替は利用できます。",
        _ => $"Wi-Fi 情報を取得できません（Windows エラー {code}）。手動切替は利用できます。"
    };
    private void Close()
    {
        if (_handle == IntPtr.Zero) return;
        WlanNative.WlanRegisterNotification(_handle, 0, true, null, IntPtr.Zero, IntPtr.Zero, out _);
        WlanNative.WlanCloseHandle(_handle, IntPtr.Zero);
        _handle = IntPtr.Zero;
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; Close(); _disposed = true; } GC.KeepAlive(_callback); }
}
