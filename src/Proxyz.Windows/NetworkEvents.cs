using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Proxyz.Windows;

[ComVisible(true)]
[Guid("DCB00004-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INetworkEventsSink
{
    void NetworkAdded(Guid networkId);
    void NetworkDeleted(Guid networkId);
    void NetworkConnectivityChanged(Guid networkId, uint connectivity);
    void NetworkPropertyChanged(Guid networkId, uint flags);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class NetworkEventsSink(Action changed) : INetworkEventsSink
{
    public void NetworkAdded(Guid networkId) => Raise();
    public void NetworkDeleted(Guid networkId) => Raise();
    public void NetworkConnectivityChanged(Guid networkId, uint connectivity) => Raise();
    public void NetworkPropertyChanged(Guid networkId, uint flags) => Raise();
    private void Raise() { try { changed(); } catch { /* COM callback boundary. */ } }
}

/// <summary>NLM + IP changes. Construct/dispose on the UI thread to own COM subscription.</summary>
public sealed class NetworkEvents : IDisposable
{
    private object? _manager;
    private IConnectionPoint? _point;
    private NetworkEventsSink? _sink;
    private int _cookie;
    private readonly Action _changed;
    public string Notice { get; private set; } = "";
    public NetworkEvents(Action changed)
    {
        _changed = changed;
        NetworkChange.NetworkAddressChanged += OnAddress;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        try
        {
            _manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!)!;
            var container = (IConnectionPointContainer)_manager;
            var id = typeof(INetworkEventsSink).GUID;
            container.FindConnectionPoint(ref id, out _point);
            if (_point is null) throw new COMException("NLM did not return a connection point.");
            _sink = new NetworkEventsSink(changed);
            _point.Advise(_sink, out _cookie);
        }
        catch
        {
            Notice = "ネットワーク識別イベントを購読できません。IPアドレス変更と Wi-Fi イベントは監視します。必要に応じて「更新」を押してください。";
            ReleaseCom();
        }
    }
    private void OnAddress(object? sender, EventArgs e) => _changed();
    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) => _changed();
    private void ReleaseCom()
    {
        if (_point is not null)
        {
            if (_cookie != 0) { try { _point.Unadvise(_cookie); } catch (COMException) { } }
            Marshal.ReleaseComObject(_point); _point = null; _cookie = 0;
        }
        if (_manager is not null && Marshal.IsComObject(_manager)) Marshal.ReleaseComObject(_manager);
        _manager = null; _sink = null;
    }
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddress;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
        ReleaseCom();
    }
}
