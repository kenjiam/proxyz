using System.Collections.ObjectModel;
using System.Text;
using Proxyz.App.Infrastructure;
using Proxyz.Core;
using Proxyz.Core.Models;

namespace Proxyz.App.ViewModels;

public sealed class RuleEditorModel : ObservableObject
{
    private readonly NetworkRule _original;
    private string _name, _networkId, _ssid = "", _error = "";
    private bool _enabled, _hex;
    private int _mode;
    private Profile? _profile;
    private NetworkIdentity? _network;
    public ObservableCollection<NetworkIdentity> Networks { get; }
    public IReadOnlyList<Profile> Profiles { get; }
    public string[] Modes { get; } = ["登録済み Windows ネットワーク", "Wi-Fi の SSID だけ", "Windows ネットワークと SSID の両方"];
    public RuleEditorModel(NetworkRule original, AppConfig config, NetworkSnapshot snapshot)
    {
        _original = original; _name = original.Name; _enabled = original.Enabled;
        _networkId = original.NetworkId?.ToString() ?? "";
        _mode = original.NetworkId.HasValue ? (original.SsidHex is null ? 0 : 2) : (original.SsidHex is null ? 0 : 1);
        Profiles = config.Profiles; _profile = Profiles.FirstOrDefault(p => p.Id == original.ProfileId) ?? Profiles.FirstOrDefault();
        Networks = new(snapshot.Networks);
        if (original.NetworkId.HasValue && Networks.All(n => n.NetworkId != original.NetworkId.Value))
            Networks.Add(new() { NetworkId = original.NetworkId.Value, Kind = original.Kind ?? NetworkKind.Other, Name = "保存済み（現在は未接続）", SsidHex = original.SsidHex });
        _network = Networks.FirstOrDefault(n => n.NetworkId == original.NetworkId) ?? Networks.FirstOrDefault();
        if (original.NetworkId is null && original.SsidHex is null && _network is not null) _networkId = _network.NetworkId.ToString();
        LoadSsid(original.SsidHex ?? _network?.SsidHex);
    }
    private void LoadSsid(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) { _ssid = ""; return; }
        try
        {
            _ssid = new UTF8Encoding(false, true).GetString(Convert.FromHexString(hex));
            _hex = _ssid.Any(char.IsControl);
            if (_hex) _ssid = hex;
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { _ssid = hex; _hex = true; }
        Notify(nameof(Ssid)); Notify(nameof(SsidIsHex));
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int ModeIndex { get => _mode; set { if (Set(ref _mode, value)) { Notify(nameof(ShowNetwork)); Notify(nameof(ShowSsid)); } } }
    public bool ShowNetwork => _mode is 0 or 2;
    public bool ShowSsid => _mode is 1 or 2;
    public string NetworkId { get => _networkId; set => Set(ref _networkId, value); }
    public string Ssid { get => _ssid; set => Set(ref _ssid, value); }
    public bool SsidIsHex { get => _hex; set => Set(ref _hex, value); }
    public Profile? SelectedProfile { get => _profile; set => Set(ref _profile, value); }
    public NetworkIdentity? SelectedNetwork
    {
        get => _network;
        set
        {
            if (!Set(ref _network, value) || value is null) return;
            NetworkId = value.NetworkId.ToString(); LoadSsid(value.SsidHex);
        }
    }
    public string Error { get => _error; set { Set(ref _error, value); Notify(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;
    public NetworkRule Build()
    {
        Guid? id = null;
        if (ShowNetwork)
        {
            if (!Guid.TryParse(_networkId, out var parsed) || parsed == Guid.Empty) throw new ArgumentException("現在のネットワークを選ぶか、有効なネットワーク ID を指定してください。");
            id = parsed;
        }
        string? ssid = ShowSsid ? (_hex ? _ssid.Trim().ToUpperInvariant() : Validation.SsidToHex(_ssid)) : null;
        return _original with { Name = _name.Trim(), Enabled = _enabled, ProfileId = _profile?.Id ?? Guid.Empty,
            NetworkId = id, SsidHex = ssid, Kind = ShowSsid ? NetworkKind.WiFi : _network?.Kind };
    }
}
