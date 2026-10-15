using Proxyz.Core.Models;

namespace Proxyz.Core;

public enum DecisionKind { Keep, ApplyProfile, ApplyDirect, Ambiguous, Unavailable }
public sealed record RuleDecision(DecisionKind Kind, string Reason, Guid? ProfileId = null);

/// <summary>Pure decision function. No registry writes, routes, HTTP requests, or DNS.</summary>
public static class RuleEngine
{
    public static RuleDecision Decide(AppConfig config, NetworkSnapshot snapshot)
    {
        if (!snapshot.Reliable) return new(DecisionKind.Unavailable, "ネットワーク情報を取得できないため変更しません。");
        if (snapshot.VpnDetected && config.Preferences.PauseOnVpn)
            return new(DecisionKind.Keep, "VPN・トンネル接続を検出したため、自動変更を控えています。");
        var networks = snapshot.Networks.DistinctBy(n => (n.NetworkId, n.AdapterId)).ToList();
        if (networks.Count == 0) return new(DecisionKind.Keep, "ネットワーク未接続のため、現在の設定を維持します。");
        var targets = new HashSet<Guid>();
        bool unmatched = false, unknownWifi = false, unknownKind = false;
        foreach (var network in networks)
        {
            var matches = config.Rules.Where(r => r.Matches(network)).Select(r => r.ProfileId).Distinct().ToList();
            if (matches.Count > 1) return new(DecisionKind.Ambiguous, "同じネットワークに異なるプロファイルのルールが一致しています。変更しません。");
            if (matches.Count == 1) targets.Add(matches[0]);
            else
            {
                unmatched = true;
                unknownKind |= network.Kind == NetworkKind.Other;
                unknownWifi |= network.Kind == NetworkKind.WiFi && network.SsidHex is null;
            }
        }
        if (targets.Count > 1 || (targets.Count == 1 && unmatched))
            return new(DecisionKind.Ambiguous, "同時接続しているネットワークの条件が一致しないため変更しません。");
        if (targets.Count == 1)
        {
            var id = targets.Single();
            return config.Profiles.Any(p => p.Id == id)
                ? new(DecisionKind.ApplyProfile, "登録済みネットワークのルールに一致しました。", id)
                : new(DecisionKind.Unavailable, "ルールに対応するプロファイルがありません。");
        }
        if (unknownKind) return new(DecisionKind.Unavailable, "未登録の接続の種類を確認できないため、自動では変更しません。");
        if (unknownWifi) return new(DecisionKind.Unavailable, "Wi-Fi の識別情報が不足しています。位置情報の許可を確認するか、Windows ネットワークでルールを登録してください。");
        return config.Preferences.UnknownNetwork == UnknownNetworkAction.Direct
            ? new(DecisionKind.ApplyDirect, "未登録ネットワークでは直接接続する設定に従います。")
            : new(DecisionKind.Keep, "未登録ネットワークのため、現在の設定を維持します。");
    }
}
