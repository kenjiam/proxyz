using System.Globalization;
using System.Text.Json;
using Proxyz.Core;
using Proxyz.Core.Models;

namespace Proxyz.Cli;

/// <summary>Shared CLI/tray command boundary. Native writes stay in ProxyController.</summary>
public sealed class CommandService(ProxyController controller, ICommandPlatform platform)
{
    private readonly object _gate = new();
    private string? _lastError;
    public ProxyController Controller { get; } = controller;
    public const string Help = """
Proxyz — current-user Windows proxy settings
  status [--verbose] [--json]
  up | down | switch <profile> | direct
  restore [--original]
  profile list | show <name-or-id> | capture <name> | remove <name-or-id>
  profile add <name> --direct|--wpad true|--pac <url>|--server <host:port>
  profile set <name-or-id> [--name <name>] [--server <value>] [--pac <value>]
      [--bypass <value>] [--note <value>] [--flags <1..15>]
      [--direct-flag true|false] [--wpad true|false] [--manual true|false] [--script true|false]
  rule list | show <name-or-id> | remove <name-or-id> | enable <name-or-id> | disable <name-or-id>
  rule add <name> --profile <profile> --current-network [--current-ssid]
  rule set <name-or-id> [--profile <profile>] [--name <name>]
      [--network-id <guid|none>] [--ssid <text>] [--ssid-hex <hex|none>] [--kind wifi|lan|other|any]
  config | config get <key> | config set <key> <value>
      keys: unknown-network (keep|direct), pause-on-vpn, observe-wifi, startup
  check [--url <http-or-https-url>]
  tray | quit | version | --help
Global: --demo (isolated in-memory backend), --dry-run (no changes), --yes (confirm explicitly)
down/quit keep Windows settings. Manual apply/restore and saves pause automation.
check without --url reads settings only. A URL test sends HEAD using WinINet and WinHTTP.
""";

    public StatusInfo Status()
    {
        lock (_gate)
        {
            var current = Controller.ReadCurrent(); var config = Controller.GetConfig();
            var network = platform.ReadNetwork(); var policy = Controller.ReadPolicy();
            var decision = RuleEngine.Decide(config, network);
            bool enabled = config.Preferences.AutomationEnabled && !config.Preferences.Paused && platform.Resident && !platform.AutomationSuspended;
            string state = Controller.RecoveryHold ? "Recovery" : _lastError is not null ? "Error" :
                Controller.ExternalHold || Controller.AutomaticFailureHold || !policy.CanChange ||
                decision.Kind is DecisionKind.Ambiguous or DecisionKind.Unavailable ||
                (network.VpnDetected && config.Preferences.PauseOnVpn) ? "Warning" : enabled ? "OK" : "Paused";
            string reason = Controller.RecoveryHold ? "未完了の変更があります。restore で復元してください。" :
                _lastError ?? (Controller.ExternalHold ? "外部変更を検出しました。確認して再開してください。" :
                Controller.AutomaticFailureHold ? "同じ接続先での自動再試行を停止しています。" :
                !policy.CanChange ? policy.Reason : !platform.Resident ? "常駐していないため、自動切換は動作していません。" :
                platform.AutomationSuspended ? "自動切換の再開確認待ちです。" : decision.Reason);
            var matches = network.Networks.SelectMany(n => config.Rules.Where(r => r.Matches(n)).Select(r =>
                new RuleMatch(n.NetworkId, n.Name, r.Name, config.Profiles.FirstOrDefault(p => p.Id == r.ProfileId)?.Name ?? "missing"))).ToList();
            return new(state, enabled, reason, current, config.Profiles.FirstOrDefault(p => p.Settings.SameAs(current))?.Name,
                network, Controller.RecoveryHold, Controller.ExternalHold, policy.CanChange ? policy.Notice : policy.Reason, matches);
        }
    }
    public void Reconcile()
    {
        lock (_gate)
        {
            try { Controller.Reconcile(platform.ReadNetwork()); _lastError = null; }
            catch (Exception e)
            {
                _lastError = e.Message;
                try { Controller.Pause(); } catch { /* retain error; do not claim a durable pause */ }
            }
        }
    }
    public CommandPlan Prepare(string[] args)
    {
        lock (_gate)
        {
            var line = CommandLine.Parse(args);
            if (line.Has("help") || line.Words.Count == 0) { line.Allow("help"); return Read(Help); }
            string verb = line.Words[0];
            switch (verb)
            {
                case "version": line.Count(1); line.Allow(); return Read("Proxyz 0.1.0 — CLI + tray");
                case "status":
                    line.Count(1); line.Allow("json", "verbose"); var status = Status();
                    string text = $"Proxyz - {status.State}\nProfile: {status.Profile ?? "Windows settings"}\nProxy: {status.Current.Summary}\nAutomation: {(status.AutomationEnabled ? "enabled" : "paused / disabled")}\n{status.Reason}\n" +
                        string.Join("\n", status.Network.Networks.Select(n => n.Display));
                    if (line.Has("verbose")) text += "\n" + ProxyDescription.Format(status.Current) + "\n" + JsonSerializer.Serialize(status.Network, Json) +
                        "\nMatched rules:\n" + string.Join("\n", status.Matches.Select(m => $"{m.Network}: {m.Rule} -> {m.Profile}")) + "\n" + status.PolicyNotice;
                    return Read(line.Has("json") ? JsonSerializer.Serialize(status, Json) : text, status);
                case "down": line.Count(1); line.Allow(); return Change("自動切換を停止。Windows 設定は維持します。", false, () => { Controller.Pause(); return Success("自動切換を停止しました。"); });
                case "switch": line.Count(2); line.Allow(); var profile = FindProfile(line.Words[1]); return Apply(profile.Settings, profile.Id);
                case "direct": line.Count(1); line.Allow(); return Apply(ProxySettings.Direct(), null);
                case "restore":
                    line.Count(1); line.Allow("original"); var journal = Controller.GetJournal();
                    var target = journal.Pending?.RecoveryTarget ?? journal.Pending?.Before ?? (line.Has("original") ? journal.Original : journal.Previous);
                    if (target is null) throw new ArgumentException("復元記録がありません。");
                    var before = Controller.ReadCurrent();
                    return Change(Preview(before, target), true, () => Result(Controller.Restore(line.Has("original"), true, before)));
                case "up":
                    line.Count(1); line.Allow(); var snapshot = platform.ReadNetwork(); var config = Controller.GetConfig();
                    var decision = RuleEngine.Decide(config, snapshot); var current = Controller.ReadCurrent();
                    var desired = decision.Kind == DecisionKind.ApplyDirect ? ProxySettings.Direct() :
                        decision.ProfileId is Guid id ? config.Profiles.Single(p => p.Id == id).Settings : current;
                    return Change("自動切換を再開。外部変更は表示中の値を採用します。\n" + decision.Reason + "\n" + Preview(current, desired, false), true, () =>
                    {
                        if (!platform.Resident) return new(6, "先に proxyz tray を起動してください。自動切換には常駐が必要です。");
                        var result = Controller.Resume(true, current);
                        if (!result.Success) return Result(result);
                        var operation = Controller.Reconcile(platform.ReadNetwork()).Operation;
                        return operation is null ? Result(result) : Result(operation);
                    }, snapshot.Fingerprint);
                case "profile": return ProfileCommand(line);
                case "rule": return RuleCommand(line);
                case "config": return ConfigCommand(line);
                case "check":
                    line.Count(1); line.Allow("url"); Uri? url = line.Has("url") ? ValidateUrl(line.Required("url")) : null;
                    return new(url is null ? "WinINet / WinHTTP の設定だけ読み取ります。通信しません。" :
                        $"通信先: {url}\nWinINet と WinHTTP から HEAD を送信します。PAC/WPAD に伴う通信が発生する場合があります。リダイレクト・認証の自動処理は無効。設定は変更しません。",
                        url is not null, () => { lock (_gate) return platform.Check(url); });
                case "quit": line.Count(1); line.Allow(); return Change("常駐を終了。現在の Windows 設定は維持します。", true, () => { platform.Quit(); return Success("Proxyz を終了します。Windows 設定は維持しています。"); });
                default: throw new ArgumentException($"Unknown command: {verb}. See proxyz --help.");
            }
        }
    }

    private CommandPlan ProfileCommand(CommandLine line)
    {
        if (line.Words.Count < 2) throw new ArgumentException("profile requires a subcommand.");
        string action = line.Words[1];
        if (action == "list") { line.Count(2); line.Allow(); var list = Controller.GetConfig().Profiles; return Read(string.Join("\n", list.Select(p => $"{p.Id}  {p.Name}  {p.Settings.Summary}")), list); }
        line.Count(3);
        if (action == "show") { line.Allow(); var item = FindProfile(line.Words[2]); return Read(JsonSerializer.Serialize(item, Json), item); }
        if (action == "remove") { line.Allow(); var item = FindProfile(line.Words[2]); return Change($"プロファイル {item.Name} を削除。自動切換を停止。", true, () => { Controller.DeleteProfile(item.Id); return Success("削除しました。Windows 設定は維持。"); }); }
        if (action is not ("add" or "set" or "capture")) throw new ArgumentException("Unknown profile subcommand.");
        var profile = action == "set" ? FindProfile(line.Words[2]) : new Profile { Name = line.Words[2] };
        if (action == "capture") { line.Allow(); profile = profile with { Settings = Controller.ReadCurrent() }; }
        else
        {
            line.Allow("direct", "server", "pac", "bypass", "flags", "direct-flag", "wpad", "manual", "script", "note", "name");
            var settings = profile.Settings;
            if (line.Has("direct")) settings = settings with { Flags = ProxySettings.DirectFlag };
            if (line.Has("flags")) settings = settings with { Flags = uint.Parse(line.Required("flags"), CultureInfo.InvariantCulture) };
            if (line.Has("server")) settings = settings with { ProxyServer = line.Required("server"), Flags = settings.Flags | ProxySettings.ManualFlag };
            if (line.Has("pac")) settings = settings with { AutoConfigUrl = line.Required("pac"), Flags = settings.Flags | ProxySettings.ScriptFlag };
            foreach (var (key, flag) in new[] { ("direct-flag", ProxySettings.DirectFlag), ("wpad", ProxySettings.AutoDetectFlag), ("manual", ProxySettings.ManualFlag), ("script", ProxySettings.ScriptFlag) })
                if (line.Has(key)) settings = settings with { Flags = line.Boolean(key) ? settings.Flags | flag : settings.Flags & ~flag };
            if (line.Has("bypass")) settings = settings with { ProxyBypass = line.Required("bypass") };
            profile = profile with { Settings = settings, Name = line.Value("name") ?? profile.Name, Note = line.Value("note") ?? profile.Note };
        }
        var errors = Validation.Profile(profile, Controller.GetConfig().Profiles);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return Change($"保存: {profile.Name}\n" + ProxyDescription.Format(profile.Settings) + "\nWindows 設定は変更せず、自動切換を停止します。", false,
            () => { Controller.SaveProfile(profile); return Success("プロファイルを保存しました。自動切換は停止。"); });
    }

    private CommandPlan RuleCommand(CommandLine line)
    {
        if (line.Words.Count < 2) throw new ArgumentException("rule requires a subcommand.");
        var config = Controller.GetConfig(); string action = line.Words[1];
        if (action == "list") { line.Count(2); line.Allow(); return Read(string.Join("\n", config.Rules.Select(r => $"{r.Id}  {r.Name}  enabled={r.Enabled}  {r.ConditionSummary}")), config.Rules); }
        line.Count(3);
        NetworkRule rule = action == "add" ? new() { Name = line.Words[2] } : FindRule(line.Words[2]);
        if (action == "show") { line.Allow(); return Read(JsonSerializer.Serialize(rule, Json), rule); }
        if (action == "remove") { line.Allow(); return Change($"ルール {rule.Name} を削除。自動切換を停止。", true, () => { Controller.DeleteRule(rule.Id); return Success("ルールを削除しました。自動切換は停止。"); }); }
        string? fingerprint = null;
        if (action is "enable" or "disable") { line.Allow(); rule = rule with { Enabled = action == "enable" }; }
        else if (action is "add" or "set")
        {
            line.Allow("profile", "name", "network-id", "ssid", "ssid-hex", "kind", "current-network", "current-ssid");
            if (line.Has("ssid") && line.Has("ssid-hex")) throw new ArgumentException("Choose --ssid or --ssid-hex.");
            if (line.Has("profile")) rule = rule with { ProfileId = FindProfile(line.Required("profile")).Id };
            if (line.Has("name")) rule = rule with { Name = line.Required("name") };
            if (line.Has("network-id")) rule = rule with { NetworkId = line.Required("network-id") == "none" ? null : Guid.Parse(line.Required("network-id")) };
            if (line.Has("ssid")) rule = rule with { SsidHex = Validation.SsidToHex(line.Required("ssid")) };
            if (line.Has("ssid-hex")) rule = rule with { SsidHex = line.Required("ssid-hex") == "none" ? null : line.Required("ssid-hex") };
            if (line.Has("kind")) rule = rule with { Kind = line.Required("kind") switch { "wifi" => NetworkKind.WiFi, "lan" => NetworkKind.Ethernet, "other" => NetworkKind.Other, "any" => null, _ => throw new ArgumentException("kind: wifi|lan|other|any") } };
            if (line.Has("current-network") || line.Has("current-ssid"))
            {
                var snapshot = platform.ReadNetwork();
                if (!snapshot.Reliable || snapshot.Networks.Count != 1) throw new ArgumentException("接続を一意に選べません。status --verbose で確認し、ID/SSID を指定してください。");
                var network = snapshot.Networks[0]; fingerprint = snapshot.Fingerprint;
                if (line.Has("current-network")) rule = rule with { NetworkId = network.NetworkId };
                if (line.Has("current-ssid")) rule = rule with { SsidHex = network.SsidHex ?? throw new ArgumentException("SSID を取得できません。Wi-Fi 識別の明示許可が必要です。") };
            }
        }
        else throw new ArgumentException("Unknown rule subcommand.");
        var errors = Validation.Rule(rule, config);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return Change($"ルールを保存: {rule.Name}\n{rule.ConditionSummary}\n自動切換を停止します。", false,
            () => { Controller.SaveRule(rule); return Success("ルールを保存しました。自動切換は停止。"); }, fingerprint);
    }

    private CommandPlan ConfigCommand(CommandLine line)
    {
        line.Allow(); var preferences = Controller.GetConfig().Preferences;
        var values = new Dictionary<string, string> { ["unknown-network"] = preferences.UnknownNetwork == UnknownNetworkAction.Direct ? "direct" : "keep", ["pause-on-vpn"] = preferences.PauseOnVpn.ToString().ToLowerInvariant(), ["observe-wifi"] = preferences.ObserveWifi.ToString().ToLowerInvariant(), ["startup"] = platform.StartupEnabled.ToString().ToLowerInvariant() };
        if (line.Words.Count == 1) return Read(string.Join("\n", values.Select(p => $"{p.Key}={p.Value}")), values);
        if (line.Words.Count < 3 || !values.ContainsKey(line.Words[2])) throw new ArgumentException("Unknown config key.");
        string key = line.Words[2];
        if (line.Words[1] == "get") { line.Count(3); return Read(values[key]); }
        if (line.Words[1] != "set") throw new ArgumentException("config get|set");
        line.Count(4); string value = line.Words[3]; bool? startup = null;
        preferences = key switch
        {
            "unknown-network" => preferences with { UnknownNetwork = value switch { "keep" => UnknownNetworkAction.KeepCurrent, "direct" => UnknownNetworkAction.Direct, _ => throw new ArgumentException("Expected keep or direct.") } },
            "pause-on-vpn" => preferences with { PauseOnVpn = CommandLine.ParseBoolean(value) },
            "observe-wifi" => preferences with { ObserveWifi = CommandLine.ParseBoolean(value) },
            "startup" => preferences,
            _ => throw new ArgumentException("Unknown config key.")
        };
        if (key == "startup") startup = CommandLine.ParseBoolean(value);
        preferences = preferences with { Paused = true };
        string? oldStartup = platform.StartupCommand;
        string explanation = key == "observe-wifi" && preferences.ObserveWifi ? "\n接続中 SSID を取得します。Windows が位置情報許可を要求する場合があります。スキャン・外部送信はしません。" :
            key == "startup" ? "\n対象: HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run の Proxyz 値。\n現在: " + (oldStartup ?? "未登録") + "\n変更先: " + (startup == true ? platform.StartupTarget : "登録解除") : "";
        return Change($"{key}: {values[key]} -> {value}\n保存後は自動切換を停止。" + explanation, key is "startup" or "observe-wifi", () =>
        {
            if (startup.HasValue && platform.StartupCommand != oldStartup) return new(3, "確認後に自動起動登録が変わりました。再確認してください。");
            // Ensure storage is writable, and pause durably before registry operations.
            Controller.SavePreferences(preferences);
            if (startup.HasValue) platform.SetStartup(startup.Value);
            return Success("設定を保存しました。自動切換は停止。");
        });
    }

    private CommandPlan Apply(ProxySettings target, Guid? profile)
    {
        var before = Controller.ReadCurrent();
        return Change(Preview(before, target), true, () => Result(Controller.ApplyManual(target, profile, true, before)));
    }
    private string StateStamp() => JsonSerializer.Serialize(Controller.GetConfig()) + JsonSerializer.Serialize(Controller.GetJournal());
    private CommandPlan Change(string preview, bool confirm, Func<CommandResult> execute, string? fingerprint = null)
    {
        string stamp = StateStamp();
        return new((platform.Demo ? "【DEMO: Windows 設定は変更しません】\n" : "") + preview, confirm, () =>
        {
            lock (_gate)
            {
                if (StateStamp() != stamp || (fingerprint is not null && platform.ReadNetwork().Fingerprint != fingerprint))
                    return new(3, "確認後に設定・復元記録・ネットワークが変わりました。再確認してください。");
                var result = execute(); if (result.ExitCode == 0) _lastError = null; return result;
            }
        });
    }
    private Profile FindProfile(string name) => Unique(Controller.GetConfig().Profiles, p => p.Name, p => p.Id, name);
    private NetworkRule FindRule(string name) => Unique(Controller.GetConfig().Rules, r => r.Name, r => r.Id, name);
    private static T Unique<T>(IEnumerable<T> items, Func<T, string> name, Func<T, Guid> id, string key)
    {
        var matches = items.Where(p => string.Equals(name(p), key, StringComparison.OrdinalIgnoreCase) || id(p).ToString().Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw new ArgumentException("名前 / ID が見つからないか一意ではありません。");
    }
    private static string Preview(ProxySettings before, ProxySettings after, bool pause = true) => "現在:\n" + ProxyDescription.Format(before) + "\n変更先:\n" + ProxyDescription.Format(after) + "\n適用は通信成功を保証しません。" + (pause ? "自動切換を停止します。" : "再開後は登録ルールに従って自動変更します。");
    private static CommandPlan Read(string text, object? data = null) => new(text, false, () => new(0, text, data));
    private static CommandResult Success(string text) => new(0, text);
    private static CommandResult Result(OperationResult result) => new(result.Success ? 0 : result.RecoveryRequired ? 5 : 3, result.Message);
    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("接続先は認証情報とフラグメントを含まない HTTP / HTTPS URL を指定してください。");
        return url;
    }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}
