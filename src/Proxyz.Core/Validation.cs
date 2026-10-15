using System.Net;
using System.Text;
using Proxyz.Core.Models;

namespace Proxyz.Core;

public static class Validation
{
    public static IReadOnlyList<string> Settings(ProxySettings settings)
    {
        var errors = new List<string>();
        if ((settings.Flags & 15) == 0) errors.Add("接続方法を1つ以上指定してください。");
        if ((settings.Flags & ~15u) != 0) errors.Add("未対応のプロキシ設定フラグが含まれています。");
        if (settings.ProxyServer is null || settings.ProxyBypass is null || settings.AutoConfigUrl is null)
        {
            errors.Add("設定値に null は使用できません。");
            return errors;
        }
        if (settings.ProxyServer.Length > 4096 || settings.ProxyBypass.Length > 16384 || settings.AutoConfigUrl.Length > 8192)
            errors.Add("設定値が長すぎます。");
        if (new[] { settings.ProxyServer, settings.ProxyBypass, settings.AutoConfigUrl }.Any(s => s.Any(char.IsControl)))
            errors.Add("設定値に改行や制御文字を含めることはできません。");
        if (settings.Script &&
            (!Uri.TryCreate(settings.AutoConfigUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            errors.Add("PAC はユーザー名・パスワードを含まない http:// または https:// の URL を指定してください。");
        if (settings.Manual)
        {
            if (string.IsNullOrWhiteSpace(settings.ProxyServer)) errors.Add("プロキシサーバーを入力してください。");
            else if (!ValidServerList(settings.ProxyServer))
                errors.Add("プロキシは host:port または http=host:port;https=host:port の形式にしてください。IPv6 は [アドレス]:port を使います。");
        }
        return errors;
    }

    public static bool ValidServerList(string value)
    {
        if (value.Contains('@') || value.Any(char.IsControl)) return false;
        var parts = value.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        bool mapped = parts.Any(p => p.Contains('='));
        if (!mapped) return parts.Length == 1 && ValidEndpoint(parts[0]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !new[] { "http", "https", "ftp", "socks" }.Contains(pieces[0], StringComparer.OrdinalIgnoreCase) ||
                !seen.Add(pieces[0]) || !ValidEndpoint(pieces[1])) return false;
        }
        return true;
    }

    public static bool ValidEndpoint(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.Contains('@') || value.Contains('/') || value.Contains('=') || value.Contains(';')) return false;
        string host, port;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close < 2 || close + 2 > value.Length || value[close + 1] != ':') return false;
            host = value[1..close]; port = value[(close + 2)..];
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            int colon = value.LastIndexOf(':');
            if (colon < 1 || value[..colon].Contains(':')) return false;
            host = value[..colon]; port = value[(colon + 1)..];
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        }
        return port.Length > 0 && port.All(char.IsAsciiDigit) && int.TryParse(port, out var number) && number is >= 1 and <= 65535;
    }

    public static IReadOnlyList<string> Profile(Profile profile, IEnumerable<Profile> others)
    {
        var errors = Settings(profile.Settings).ToList();
        if (profile.Id == Guid.Empty) errors.Add("プロファイル ID がありません。");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name.Any(char.IsControl)) errors.Add("名前は1～80文字で入力してください。");
        if (profile.Note is null || profile.Note.Length > 1000) errors.Add("メモは1000文字以内で入力してください。");
        if (others.Any(p => p.Id != profile.Id && string.Equals(p.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase))) errors.Add("同じ名前のプロファイルがあります。");
        return errors;
    }

    public static IReadOnlyList<string> Rule(NetworkRule rule, AppConfig config)
    {
        var errors = new List<string>();
        if (rule.Id == Guid.Empty) errors.Add("ルール ID がありません。");
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80 || rule.Name.Any(char.IsControl)) errors.Add("ルール名は1～80文字で入力してください。");
        if (!config.Profiles.Any(p => p.Id == rule.ProfileId)) errors.Add("適用するプロファイルを選んでください。");
        if (rule.NetworkId is null && string.IsNullOrEmpty(rule.SsidHex)) errors.Add("ネットワーク ID または SSID を指定してください。");
        if (rule.NetworkId == Guid.Empty) errors.Add("ネットワーク ID が不正です。");
        if (rule.SsidHex is not null && !ValidSsidHex(rule.SsidHex)) errors.Add("SSID は1～32バイトで指定してください。");
        if (rule.Kind is not null && !Enum.IsDefined(rule.Kind.Value)) errors.Add("接続の種類が不正です。");
        return errors;
    }

    public static bool ValidSsidHex(string value) => value.Length is >= 2 and <= 64 && value.Length % 2 == 0 && value.All(Uri.IsHexDigit);
    public static string SsidToHex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length is < 1 or > 32) throw new ArgumentException("SSID は UTF-8 で1～32バイトにしてください。");
        return Convert.ToHexString(bytes);
    }

    public static void Config(AppConfig config)
    {
        if (config.SchemaVersion != 1) throw new InvalidDataException("このバージョンでは読み込めない設定ファイルです。上書きしません。");
        if (config.Preferences is null || config.Profiles is null || config.Rules is null) throw new InvalidDataException("設定ファイルに必要な項目がありません。");
        if (config.Profiles.Count > 500 || config.Rules.Count > 1000) throw new InvalidDataException("設定数が上限を超えています。");
        if (!double.IsFinite(config.Preferences.UiScale) || config.Preferences.UiScale is < 1 or > 2) throw new InvalidDataException("表示倍率が不正です。");
        if (!Enum.IsDefined(config.Preferences.UnknownNetwork)) throw new InvalidDataException("未登録ネットワークの動作が不正です。");
        if (config.Profiles.Any(p => p is null || p.Settings is null || p.Name is null || p.Note is null) ||
            config.Rules.Any(r => r is null || r.Name is null)) throw new InvalidDataException("設定ファイルの内容が不正です。");
        if (config.Profiles.Select(p => p.Id).Distinct().Count() != config.Profiles.Count ||
            config.Rules.Select(r => r.Id).Distinct().Count() != config.Rules.Count) throw new InvalidDataException("ID が重複しています。");
        foreach (var p in config.Profiles)
        {
            var errors = Profile(p, config.Profiles);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        }
        foreach (var r in config.Rules)
        {
            var errors = Rule(r, config);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        }
    }
}
