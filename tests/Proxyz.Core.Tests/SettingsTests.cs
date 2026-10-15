using Proxyz.Core.Models;

namespace Proxyz.Core.Tests;

internal static class SettingsTests
{
    public static void Register(TestSuite s)
    {
        s.Add("settings.direct", () => { Check.Equal(1u, ProxySettings.Direct().Flags); Check.True(ProxySettings.Direct().IsDirectOnly); });
        s.Add("settings.combined-flags", () => { var p = new ProxySettings { Flags = 15 }; Check.True(p.AutoDetect && p.Script && p.Manual && p.AllowDirect); });
        s.Add("settings.disabled-fields-preserved", () => Check.False(new ProxySettings { AutoConfigUrl = "http://a.invalid/pac" }.SameAs(new())));
        s.Add("settings.server-case-insensitive", () => Check.True(Data.Proxy("PROXY.EXAMPLE.INVALID").SameAs(Data.Proxy())));
        s.Add("settings.pac-path-case-sensitive", () => Check.False(new ProxySettings { AutoConfigUrl = "https://a.invalid/A" }.SameAs(new() { AutoConfigUrl = "https://a.invalid/a" })));
        s.Add("settings.partial-own-write", () => Check.True(new ProxySettings { Flags = 3 }.CouldBePartialWriteOf(new(), Data.Proxy())));
        s.Add("settings.external-write-not-owned", () => Check.False(Data.Proxy("other.invalid").CouldBePartialWriteOf(new(), Data.Proxy())));
        foreach (var valid in new[] { "proxy.example.com:8080", "127.0.0.1:1", "[::1]:65535", "[2001:db8::1]:3128", "intranet:80" })
        {
            string endpoint = valid;
            s.Add("validation.endpoint-valid." + endpoint, () => Check.True(Validation.ValidEndpoint(endpoint)));
        }
        foreach (var invalid in new[] { "", "proxy", "proxy:0", "proxy:65536", "proxy:-1", "proxy:+1", "http://proxy:80", "user:pass@proxy:80", "a b:80", "::1:80", "[::1]", "host:port", "host:80/path", "host:80;other:81" })
        {
            string endpoint = invalid;
            s.Add("validation.endpoint-invalid." + endpoint, () => Check.False(Validation.ValidEndpoint(endpoint)));
        }
        s.Add("validation.protocol-map", () => Check.True(Validation.ValidServerList("http=proxy:80;https=[::1]:8080")));
        s.Add("validation.duplicate-protocol-rejected", () => Check.False(Validation.ValidServerList("http=a:80;http=b:80")));
        s.Add("validation.mixed-map-rejected", () => Check.False(Validation.ValidServerList("http=a:80;b:80")));
        s.Add("validation.credentials-rejected", () => Check.True(Validation.Settings(new() { Flags = 5, AutoConfigUrl = "https://u:p@a.invalid/pac" }).Count > 0));
        s.Add("validation.pac-http", () => Check.Equal(0, Validation.Settings(new() { Flags = 5, AutoConfigUrl = "http://a.invalid/proxy.pac" }).Count));
        s.Add("validation.pac-https", () => Check.Equal(0, Validation.Settings(new() { Flags = 5, AutoConfigUrl = "https://a.invalid/proxy.pac?version=1" }).Count));
        s.Add("validation.pac-file-rejected", () => Check.True(Validation.Settings(new() { Flags = 5, AutoConfigUrl = "file:///c:/proxy.pac" }).Count > 0));
        s.Add("validation.no-mode", () => Check.True(Validation.Settings(new() { Flags = 0 }).Count > 0));
        s.Add("validation.control-characters", () => Check.True(Validation.Settings(new() { ProxyBypass = "a\n" }).Count > 0));
        s.Add("validation.duplicate-name", () => { var p = Data.Profile(); Check.True(Validation.Profile(Data.Profile("campus"), [p]).Count > 0); });
        s.Add("validation.same-profile-edit", () => { var p = Data.Profile(); Check.Equal(0, Validation.Profile(p, [p]).Count); });
        s.Add("ssid.32-bytes", () => Check.Equal(64, Validation.SsidToHex(new string('a', 32)).Length));
        s.Add("ssid.33-bytes-rejected", () => Check.Throws<ArgumentException>(() => Validation.SsidToHex(new string('a', 33))));
        s.Add("ssid.utf8-byte-limit", () => Check.Throws<ArgumentException>(() => Validation.SsidToHex(new string('界', 11))));
        s.Add("ssid.case-sensitive-bytes", () => Check.False(Validation.SsidToHex("a") == Validation.SsidToHex("A")));
        s.Add("ssid.hex-valid", () => Check.True(Validation.ValidSsidHex("00ff")));
        s.Add("ssid.hex-invalid", () => Check.False(Validation.ValidSsidHex("xyz")));
        s.Add("config.newer-schema-rejected", () => Check.Throws<InvalidDataException>(() => Validation.Config(new() { SchemaVersion = 2 })));
        s.Add("config.nan-scale-rejected", () => Check.Throws<InvalidDataException>(() => Validation.Config(new() { Preferences = new() { UiScale = double.NaN } })));
        s.Add("config.duplicate-id-rejected", () => { var p = Data.Profile(); Check.Throws<InvalidDataException>(() => Validation.Config(new() { Profiles = [p, p] })); });
        s.Add("rule.broad-match-rejected", () => { var p = Data.Profile(); Check.True(Validation.Rule(new() { Name = "any", ProfileId = p.Id }, new() { Profiles = [p] }).Count > 0); });
    }
}
