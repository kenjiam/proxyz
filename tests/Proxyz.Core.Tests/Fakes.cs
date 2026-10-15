using Proxyz.Core.Models;

namespace Proxyz.Core.Tests;

internal sealed class MemoryStore : IStateStore
{
    public AppConfig Config { get; set; } = new();
    public ChangeJournal Journal { get; set; } = new();
    public int JournalSaves { get; private set; }
    public Action<int, ChangeJournal>? OnJournalSave { get; set; }
    public bool FailConfigSave { get; set; }
    public AppConfig LoadConfig() => Config;
    public void SaveConfig(AppConfig config)
    {
        if (FailConfigSave) throw new IOException("Injected config save failure");
        Validation.Config(config); Config = config;
    }
    public ChangeJournal LoadJournal() => Journal;
    public void SaveJournal(ChangeJournal journal) { OnJournalSave?.Invoke(++JournalSaves, journal); Journal = journal; }
}
internal sealed class FakeProxy : IProxyBackend
{
    public ProxySettings Value { get; set; } = ProxySettings.Direct();
    public PolicyStatus Policy { get; set; } = PolicyStatus.Allowed;
    public int Writes { get; private set; }
    public int Reads { get; private set; }
    public Action<int>? OnRead { get; set; }
    public Action<int, ProxySettings>? OnWrite { get; set; }
    public ProxySettings Read() { OnRead?.Invoke(++Reads); return Value; }
    public PolicyStatus ReadPolicy() => Policy;
    public void Write(ProxySettings settings)
    {
        Writes++;
        if (OnWrite is null) Value = settings;
        else OnWrite(Writes, settings);
    }
}
internal static class Data
{
    public static ProxySettings Proxy(string name = "proxy.example.invalid") => new() { Flags = 3, ProxyServer = name + ":8080" };
    public static Profile Profile(string name = "Campus") => new() { Name = name, Settings = Proxy() };
    public static NetworkIdentity Network(string ssid = "Campus") => new()
    {
        NetworkId = Guid.NewGuid(), AdapterId = Guid.NewGuid(), Kind = NetworkKind.WiFi,
        Name = ssid, SsidHex = Validation.SsidToHex(ssid), SsidLabel = ssid
    };
    public static NetworkSnapshot Snapshot(params NetworkIdentity[] networks) => new() { Networks = [.. networks] };
    public static NetworkRule Rule(Profile profile, NetworkIdentity network) => new()
    { Name = "Rule", ProfileId = profile.Id, NetworkId = network.NetworkId };
    public static (MemoryStore Store, FakeProxy Backend, ProxyController Controller, NetworkSnapshot Snapshot, Profile Profile) Fixture(bool automatic = false)
    {
        var profile = Profile(); var network = Network();
        var store = new MemoryStore
        {
            Config = new() { Profiles = [profile], Rules = [Rule(profile, network)], Preferences = new() { AutomationEnabled = automatic } }
        };
        var backend = new FakeProxy();
        return (store, backend, new(store, backend), Snapshot(network), profile);
    }
}
