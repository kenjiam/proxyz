using Proxyz.App.Infrastructure;
using Proxyz.Core.Models;

namespace Proxyz.App.ViewModels;

public sealed class ProfileEditorModel : ObservableObject
{
    private readonly Profile _original;
    private string _name, _note, _host = "", _port = "8080", _raw, _pac, _bypass;
    private readonly string _initialBypass;
    private readonly bool _initialLocal;
    private bool _local, _direct, _manual, _script, _auto;
    private int _mode;
    private string _error = "";
    public string[] Modes { get; } = ["直接接続（プロキシを使わない）", "自動検出（WPAD）", "設定スクリプト（PAC）", "手動プロキシ（1つのサーバー）", "詳細設定（方式の併用・HTTP/HTTPS 別など）"];
    public ProfileEditorModel(Profile original)
    {
        _original = original;
        _name = original.Name; _note = original.Note;
        var s = original.Settings;
        _raw = s.ProxyServer; _pac = s.AutoConfigUrl;
        _direct = s.AllowDirect; _manual = s.Manual; _script = s.Script; _auto = s.AutoDetect;
        _local = s.ProxyBypass.Split(';', StringSplitOptions.TrimEntries).Any(x => x.Equals("<local>", StringComparison.OrdinalIgnoreCase));
        _bypass = string.Join(Environment.NewLine, s.ProxyBypass.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !x.Equals("<local>", StringComparison.OrdinalIgnoreCase)));
        _initialBypass = _bypass; _initialLocal = _local;
        _mode = s.Flags switch { 1 => 0, 9 => 1, 5 => 2, 3 when !s.ProxyServer.Contains('=') => 3, _ => 4 };
        if (_mode == 3)
        {
            int colon = s.ProxyServer.LastIndexOf(':');
            if (colon > 0) { _host = s.ProxyServer[..colon].Trim('[', ']'); _port = s.ProxyServer[(colon + 1)..]; }
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public int ModeIndex { get => _mode; set { if (Set(ref _mode, value)) { Notify(nameof(ShowSimple)); Notify(nameof(ShowAdvanced)); Notify(nameof(ShowPac)); Notify(nameof(ShowBypass)); } } }
    public bool ShowSimple => _mode == 3;
    public bool ShowAdvanced => _mode == 4;
    public bool ShowPac => _mode is 2 or 4;
    public bool ShowBypass => _mode is 3 or 4;
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string RawServers { get => _raw; set => Set(ref _raw, value); }
    public string PacUrl { get => _pac; set => Set(ref _pac, value); }
    public string Bypass { get => _bypass; set => Set(ref _bypass, value); }
    public bool BypassLocal { get => _local; set => Set(ref _local, value); }
    public bool DirectEnabled { get => _direct; set => Set(ref _direct, value); }
    public bool ManualEnabled { get => _manual; set => Set(ref _manual, value); }
    public bool ScriptEnabled { get => _script; set => Set(ref _script, value); }
    public bool AutoDetectEnabled { get => _auto; set => Set(ref _auto, value); }
    public string Error { get => _error; set { Set(ref _error, value); Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    public Profile Build()
    {
        var flags = _mode switch
        {
            0 => 1u, 1 => 9u, 2 => 5u, 3 => 3u,
            _ => (_direct ? 1u : 0) | (_manual ? 2u : 0) | (_script ? 4u : 0) | (_auto ? 8u : 0) | (_original.Settings.Flags & ~15u)
        };
        string server = _original.Settings.ProxyServer;
        if (_mode == 3)
        {
            string host = _host.Trim().Trim('[', ']');
            server = (host.Contains(':') ? "[" + host + "]" : host) + ":" + _port.Trim();
        }
        else if (_mode == 4) server = _raw.Trim();
        string bypass = _original.Settings.ProxyBypass;
        if (_bypass != _initialBypass || _local != _initialLocal)
        {
            var parts = _bypass.Replace("\r", "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (_local && !parts.Contains("<local>", StringComparer.OrdinalIgnoreCase)) parts.Add("<local>");
            bypass = string.Join(";", parts);
        }
        return _original with
        {
            Name = _name.Trim(), Note = _note.Trim(),
            Settings = _original.Settings with { Flags = flags, ProxyServer = server, ProxyBypass = bypass, AutoConfigUrl = _pac.Trim() }
        };
    }
}
