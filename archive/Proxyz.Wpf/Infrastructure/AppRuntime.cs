using Proxyz.Core;
using Proxyz.Core.Models;
using Proxyz.Core.Storage;
using Proxyz.Windows;

namespace Proxyz.App.Infrastructure;

public sealed class AppRuntime : IDisposable
{
    public bool Demo { get; }
    public string DataDirectory { get; }
    public ProxyController Controller { get; }
    private readonly WifiObserver? _wifi;
    private readonly NetworkReader? _reader;
    private NetworkEvents? _networks;
    private ProxyRegistryWatcher? _registry;
    public event Action? Changed;
    public string MonitorNotice => string.Join(Environment.NewLine, new[] { _networks?.Notice, _registry?.Notice }.Where(x => !string.IsNullOrEmpty(x)));

    public AppRuntime(string directory, bool demo)
    {
        DataDirectory = directory; Demo = demo;
        if (demo)
        {
            Controller = new(new DemoStore(), new DemoProxyBackend());
        }
        else
        {
            Controller = new(new JsonStateStore(directory), new WinInetProxyBackend(), new LocalAuditLog(directory));
            _wifi = new(); _reader = new(_wifi);
            _wifi.Changed += OnChanged;
        }
    }
    public void StartMonitoring()
    {
        if (Demo) return;
        _networks = new NetworkEvents(OnChanged);
        _registry = new ProxyRegistryWatcher(OnChanged);
    }
    public NetworkSnapshot ReadNetwork() => Demo ? DemoStore.Network() : _reader!.Read(Controller.GetConfig().Preferences.ObserveWifi);
    private void OnChanged() => Changed?.Invoke();
    public void Dispose()
    {
        _networks?.Dispose(); _registry?.Dispose();
        if (_wifi is not null) { _wifi.Changed -= OnChanged; _wifi.Dispose(); }
    }
}

internal sealed class DemoProxyBackend : IProxyBackend
{
    private ProxySettings _value = ProxySettings.Direct();
    public ProxySettings Read() => _value;
    public void Write(ProxySettings settings) => _value = settings;
    public PolicyStatus ReadPolicy() => PolicyStatus.Allowed;
}
internal sealed class DemoStore : IStateStore
{
    private static readonly Guid NetworkId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AdapterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private AppConfig _config;
    private ChangeJournal _journal = new();
    public DemoStore()
    {
        var direct = new Profile { Name = "自宅", Settings = ProxySettings.Direct() };
        var campus = new Profile { Name = "大学", Note = "デモ用の架空の設定です。", Settings = new() { Flags = 3, ProxyServer = "proxy.example.invalid:8080", ProxyBypass = "<local>;*.example.invalid" } };
        _config = new()
        {
            Profiles = [direct, campus],
            Rules = [new() { Name = "大学 Wi-Fi", NetworkId = NetworkId, ProfileId = campus.Id, Kind = NetworkKind.WiFi }]
        };
    }
    public static NetworkSnapshot Network() => new()
    {
        Networks = [new() { Name = "Campus（デモ）", NetworkId = NetworkId, AdapterId = AdapterId,
            Kind = NetworkKind.WiFi, SsidHex = Validation.SsidToHex("Campus-Demo"), SsidLabel = "Campus-Demo" }],
        Notice = "デモモード：Windows の設定変更・Wi-Fi API・自動起動登録・ネットワーク通信は行いません。"
    };
    public AppConfig LoadConfig() => _config;
    public void SaveConfig(AppConfig config) { Validation.Config(config); _config = config; }
    public ChangeJournal LoadJournal() => _journal;
    public void SaveJournal(ChangeJournal journal) => _journal = journal;
}
