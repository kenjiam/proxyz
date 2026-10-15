using Proxyz.Core.Models;

namespace Proxyz.Core.Tests;

internal static class RuleTests
{
    public static void Register(TestSuite s)
    {
        s.Add("rule.network-id-match", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(f.Store.Config, f.Snapshot).Kind); });
        s.Add("rule.ssid-only-match", () =>
        {
            var p = Data.Profile(); var n = Data.Network();
            var c = new AppConfig { Profiles = [p], Rules = [new() { Name = "SSID", ProfileId = p.Id, SsidHex = n.SsidHex }] };
            Check.Equal(p.Id, RuleEngine.Decide(c, Data.Snapshot(n)).ProfileId!.Value);
        });
        s.Add("rule.ssid-and-id-use-and", () =>
        {
            var p = Data.Profile(); var n = Data.Network();
            var c = new AppConfig { Profiles = [p], Rules = [Data.Rule(p, n) with { SsidHex = Validation.SsidToHex("Other") }] };
            Check.Equal(DecisionKind.Keep, RuleEngine.Decide(c, Data.Snapshot(n)).Kind);
        });
        s.Add("rule.same-ssid-different-network", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.Keep, RuleEngine.Decide(f.Store.Config, Data.Snapshot(Data.Network())).Kind); });
        s.Add("rule.disabled-ignored", () => { var f = Data.Fixture(); var c = f.Store.Config with { Rules = [f.Store.Config.Rules[0] with { Enabled = false }] }; Check.Equal(DecisionKind.Keep, RuleEngine.Decide(c, f.Snapshot).Kind); });
        s.Add("rule.unknown-keeps-default", () => Check.Equal(DecisionKind.Keep, RuleEngine.Decide(new(), Data.Snapshot(Data.Network())).Kind));
        s.Add("rule.unknown-direct-opt-in", () => Check.Equal(DecisionKind.ApplyDirect, RuleEngine.Decide(new() { Preferences = new() { UnknownNetwork = UnknownNetworkAction.Direct } }, Data.Snapshot(Data.Network())).Kind));
        s.Add("rule.disconnected-never-direct", () => Check.Equal(DecisionKind.Keep, RuleEngine.Decide(new() { Preferences = new() { UnknownNetwork = UnknownNetworkAction.Direct } }, new()).Kind));
        s.Add("rule.unreliable-never-direct", () => Check.Equal(DecisionKind.Unavailable, RuleEngine.Decide(new() { Preferences = new() { UnknownNetwork = UnknownNetworkAction.Direct } }, Data.Snapshot(Data.Network()) with { Reliable = false }).Kind));
        s.Add("rule.missing-ssid-never-direct", () => Check.Equal(DecisionKind.Unavailable, RuleEngine.Decide(new() { Preferences = new() { UnknownNetwork = UnknownNetworkAction.Direct } }, Data.Snapshot(Data.Network() with { SsidHex = null })).Kind));
        s.Add("rule.missing-ssid-network-id-still-works", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(f.Store.Config, Data.Snapshot(f.Snapshot.Networks[0] with { SsidHex = null })).Kind); });
        s.Add("rule.unknown-adapter-kind-holds", () => Check.Equal(DecisionKind.Unavailable, RuleEngine.Decide(new() { Preferences = new() { UnknownNetwork = UnknownNetworkAction.Direct } }, Data.Snapshot(Data.Network() with { Kind = NetworkKind.Other })).Kind));
        s.Add("rule.vpn-pause-default", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.Keep, RuleEngine.Decide(f.Store.Config, f.Snapshot with { VpnDetected = true }).Kind); });
        s.Add("rule.vpn-explicit-opt-out", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(f.Store.Config with { Preferences = new() { PauseOnVpn = false } }, f.Snapshot with { VpnDetected = true }).Kind); });
        s.Add("rule.same-network-conflicting-targets", () =>
        {
            var f = Data.Fixture(); var p2 = Data.Profile("Other");
            var c = f.Store.Config with { Profiles = [f.Profile, p2], Rules = [.. f.Store.Config.Rules, Data.Rule(p2, f.Snapshot.Networks[0])] };
            Check.Equal(DecisionKind.Ambiguous, RuleEngine.Decide(c, f.Snapshot).Kind);
        });
        s.Add("rule.duplicate-rules-same-target-ok", () => { var f = Data.Fixture(); var c = f.Store.Config with { Rules = [.. f.Store.Config.Rules, Data.Rule(f.Profile, f.Snapshot.Networks[0])] }; Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(c, f.Snapshot).Kind); });
        s.Add("rule.two-networks-same-target-ok", () =>
        {
            var f = Data.Fixture(); var n2 = Data.Network("Wired") with { Kind = NetworkKind.Ethernet, SsidHex = null };
            var c = f.Store.Config with { Rules = [.. f.Store.Config.Rules, Data.Rule(f.Profile, n2)] };
            Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(c, Data.Snapshot(f.Snapshot.Networks[0], n2)).Kind);
        });
        s.Add("rule.two-networks-unmatched-holds", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.Ambiguous, RuleEngine.Decide(f.Store.Config, Data.Snapshot(f.Snapshot.Networks[0], Data.Network("Home"))).Kind); });
        s.Add("rule.two-networks-different-targets-holds", () =>
        {
            var f = Data.Fixture(); var p2 = Data.Profile("Other"); var n2 = Data.Network("Other");
            var c = f.Store.Config with { Profiles = [f.Profile, p2], Rules = [.. f.Store.Config.Rules, Data.Rule(p2, n2)] };
            Check.Equal(DecisionKind.Ambiguous, RuleEngine.Decide(c, Data.Snapshot(f.Snapshot.Networks[0], n2)).Kind);
        });
        s.Add("rule.interface-kind-restriction", () => { var f = Data.Fixture(); var c = f.Store.Config with { Rules = [f.Store.Config.Rules[0] with { Kind = NetworkKind.Ethernet }] }; Check.Equal(DecisionKind.Keep, RuleEngine.Decide(c, f.Snapshot).Kind); });
        s.Add("rule.duplicate-network-enumeration", () => { var f = Data.Fixture(); Check.Equal(DecisionKind.ApplyProfile, RuleEngine.Decide(f.Store.Config, Data.Snapshot(f.Snapshot.Networks[0], f.Snapshot.Networks[0])).Kind); });
        s.Add("network.fingerprint-order-independent", () => { var a = Data.Network(); var b = Data.Network(); Check.Equal(Data.Snapshot(a, b).Fingerprint, Data.Snapshot(b, a).Fingerprint); });
        s.Add("network.fingerprint-name-not-identity", () => { var n = Data.Network(); Check.Equal(Data.Snapshot(n).Fingerprint, Data.Snapshot(n with { Name = "Renamed" }).Fingerprint); });
        s.Add("network.fingerprint-adapter-change", () => { var n = Data.Network(); Check.False(Data.Snapshot(n).Fingerprint == Data.Snapshot(n with { AdapterId = Guid.NewGuid() }).Fingerprint); });
    }
}
