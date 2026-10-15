using System.Text.Json.Serialization;

namespace Proxyz.Core.Models;

/// <summary>
/// Raw values from the user's default LAN/Internet connection, not a PAC evaluation
/// result. Keep disabled fields and flags so reading then restoring is lossless.
/// </summary>
public sealed record ProxySettings
{
    public const uint DirectFlag = 1;
    public const uint ManualFlag = 2;
    public const uint ScriptFlag = 4;
    public const uint AutoDetectFlag = 8;
    public uint Flags { get; init; } = DirectFlag;
    public string ProxyServer { get; init; } = "";
    public string ProxyBypass { get; init; } = "";
    public string AutoConfigUrl { get; init; } = "";

    [JsonIgnore] public bool Manual => (Flags & ManualFlag) != 0;
    [JsonIgnore] public bool Script => (Flags & ScriptFlag) != 0;
    [JsonIgnore] public bool AutoDetect => (Flags & AutoDetectFlag) != 0;
    [JsonIgnore] public bool AllowDirect => (Flags & DirectFlag) != 0;
    [JsonIgnore] public bool IsDirectOnly => Flags == DirectFlag;
    [JsonIgnore] public string Summary
    {
        get
        {
            if (IsDirectOnly) return "直接接続（プロキシを使わない）";
            var names = new List<string>();
            if (AutoDetect) names.Add("自動検出（WPAD）");
            if (Script) names.Add("設定スクリプト（PAC）");
            if (Manual) names.Add("手動プロキシ");
            return names.Count > 0 ? string.Join(" ＋ ", names) : "その他の Windows 設定";
        }
    }
    public static ProxySettings Direct() => new();

    public bool SameAs(ProxySettings? other) => other is not null &&
        Flags == other.Flags &&
        string.Equals(ProxyServer ?? "", other.ProxyServer ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ProxyBypass ?? "", other.ProxyBypass ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(AutoConfigUrl ?? "", other.AutoConfigUrl ?? "", StringComparison.Ordinal);

    // A rollback must not overwrite a value written by another application.
    // This is deliberately conservative; Windows cannot make this comparison atomic.
    public bool CouldBePartialWriteOf(ProxySettings before, ProxySettings intended) =>
        (Flags == before.Flags || Flags == intended.Flags) &&
        (ProxyServer == before.ProxyServer || ProxyServer == intended.ProxyServer) &&
        (ProxyBypass == before.ProxyBypass || ProxyBypass == intended.ProxyBypass) &&
        (AutoConfigUrl == before.AutoConfigUrl || AutoConfigUrl == intended.AutoConfigUrl);
}
