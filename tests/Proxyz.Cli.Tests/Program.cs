using System.Text.Json;
using Proxyz.Cli;
using Proxyz.Core;
using Proxyz.Core.Models;
using Proxyz.Core.Tests;

var suite = new TestSuite();
suite.Add("cli.status-is-read-only-and-json", () =>
{
    var f = Data.Fixture(true); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["status", "--json"]); var result = plan.Execute();
    using var json = JsonDocument.Parse(result.Text); Check.Equal("OK", json.RootElement.GetProperty("State").GetString());
    Check.Equal(0, f.Backend.Writes); Check.Equal(0, f.Store.JournalSaves);
});
suite.Add("cli.preview-and-dispose-never-write", () =>
{
    var f = Data.Fixture(true); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using (var plan = service.Prepare(["switch", f.Profile.Name])) { Check.True(plan.RequiresConfirmation); Check.True(plan.Preview.Contains(f.Profile.Settings.ProxyServer)); }
    Check.Equal(0, f.Backend.Writes); Check.False(f.Store.Config.Preferences.Paused);
});
suite.Add("cli.confirmed-switch-pauses-and-restores", () =>
{
    var f = Data.Fixture(true); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var apply = service.Prepare(["switch", f.Profile.Name]); Check.Equal(0, apply.Execute().ExitCode);
    Check.True(f.Store.Config.Preferences.Paused); Check.True(f.Backend.Value.SameAs(f.Profile.Settings));
    using var restore = service.Prepare(["restore", "--original"]); Check.Equal(0, restore.Execute().ExitCode); Check.True(f.Backend.Value.IsDirectOnly);
});
suite.Add("cli.down-keeps-proxy", () =>
{
    var f = Data.Fixture(true); f.Backend.Value = Data.Proxy(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["down"]); Check.Equal(0, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes); Check.True(f.Backend.Value.Manual);
});
suite.Add("cli.up-applies-only-with-resident-host", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot) { Resident = false }; var service = new CommandService(f.Controller, p);
    using (var plan = service.Prepare(["up"])) Check.Equal(6, plan.Execute().ExitCode);
    Check.False(f.Store.Config.Preferences.AutomationEnabled); Check.Equal(0, f.Backend.Writes);
    p.Resident = true; using var up = service.Prepare(["up"]); Check.Equal(0, up.Execute().ExitCode); Check.Equal(1, f.Backend.Writes);
});
suite.Add("cli.status-no-host-or-pending-confirmation-is-paused", () =>
{
    var f = Data.Fixture(true); var p = new TestPlatform(f.Snapshot) { Resident = false }; var service = new CommandService(f.Controller, p);
    Check.False(service.Status().AutomationEnabled); Check.Equal("Paused", service.Status().State);
    p.Resident = true; p.AutomationSuspended = true;
    Check.False(service.Status().AutomationEnabled); Check.Equal("Paused", service.Status().State); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.stale-config-preview-rejected", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["switch", f.Profile.Name]); f.Controller.SaveProfile(f.Profile with { Note = "changed" });
    Check.Equal(3, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.stale-proxy-preview-rejected", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["switch", f.Profile.Name]); f.Backend.Value = Data.Proxy("external.invalid");
    Check.Equal(3, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.stale-network-up-rejected", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["up"]); p.Network = new(); Check.Equal(3, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.restore-preview-shows-recovery-target-and-inactive-values", () =>
{
    var f = Data.Fixture(); var target = new ProxySettings { AutoConfigUrl = "disabled-pac" };
    f.Store.Journal = new() { Pending = new() { Before = Data.Proxy(), Intended = new(), RecoveryTarget = target } };
    var controller = new ProxyController(f.Store, f.Backend); var service = new CommandService(controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["restore"]); Check.True(plan.Preview.Contains("disabled-pac")); Check.Equal(0, plan.Execute().ExitCode); Check.True(f.Backend.Value.SameAs(target));
});
suite.Add("cli.profile-set-preserves-unspecified-fields", () =>
{
    var f = Data.Fixture(); var settings = f.Profile.Settings with { Flags = 15, AutoConfigUrl = "https://example.invalid/Proxy.pac", ProxyBypass = "<local>" };
    f.Controller.SaveProfile(f.Profile with { Settings = settings }); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["profile", "set", f.Profile.Name, "--note", "new note"]); Check.Equal(0, plan.Execute().ExitCode);
    Check.True(f.Store.Config.Profiles[0].Settings.SameAs(settings)); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.profile-combined-flags", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["profile", "add", "Combined", "--server", "http=a.invalid:80;https=b.invalid:81", "--pac", "https://example.invalid/P.pac", "--wpad", "true", "--bypass", "<local>"]);
    Check.Equal(0, plan.Execute().ExitCode); Check.Equal(15u, f.Store.Config.Profiles.Single(p => p.Name == "Combined").Settings.Flags);
});
suite.Add("cli.capture-saves-not-applies", () =>
{
    var f = Data.Fixture(true); f.Backend.Value = Data.Proxy("captured.invalid"); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["profile", "capture", "Captured"]); Check.Equal(0, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes);
    Check.True(f.Store.Config.Profiles.Single(p => p.Name == "Captured").Settings.SameAs(f.Backend.Value)); Check.True(f.Store.Config.Preferences.Paused);
});
suite.Add("cli.invalid-profile-does-not-save", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    Check.Throws<ArgumentException>(() => service.Prepare(["profile", "add", "Bad", "--server", "user:pass@host:80"])); Check.Equal(1, f.Store.Config.Profiles.Count);
});
suite.Add("cli.referenced-profile-cannot-be-removed", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["profile", "remove", f.Profile.Name]); Check.Throws<InvalidOperationException>(() => plan.Execute()); Check.Equal(1, f.Store.Config.Profiles.Count);
});
suite.Add("cli.rule-current-network-and-ssid", () =>
{
    var f = Data.Fixture(true); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["rule", "add", "AND", "--profile", f.Profile.Name, "--current-network", "--current-ssid"]);
    Check.Equal(0, plan.Execute().ExitCode); var rule = f.Store.Config.Rules.Single(r => r.Name == "AND");
    Check.Equal(f.Snapshot.Networks[0].NetworkId, rule.NetworkId!.Value); Check.Equal(f.Snapshot.Networks[0].SsidHex, rule.SsidHex); Check.True(f.Store.Config.Preferences.Paused); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.rule-ambiguous-current-rejected", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(Data.Snapshot(Data.Network(), Data.Network())));
    Check.Throws<ArgumentException>(() => service.Prepare(["rule", "add", "Bad", "--profile", f.Profile.Name, "--current-network"]));
});
suite.Add("cli.rule-missing-ssid-does-not-enable-wifi", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(Data.Snapshot(f.Snapshot.Networks[0] with { SsidHex = null })); var service = new CommandService(f.Controller, p);
    Check.Throws<ArgumentException>(() => service.Prepare(["rule", "add", "Bad", "--profile", f.Profile.Name, "--current-ssid"])); Check.False(f.Store.Config.Preferences.ObserveWifi);
});
suite.Add("cli.config-get-set-partial", () =>
{
    var f = Data.Fixture(true); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["config", "set", "unknown-network", "direct"]); Check.Equal(0, plan.Execute().ExitCode);
    Check.True(f.Store.Config.Preferences.PauseOnVpn); Check.False(f.Store.Config.Preferences.ObserveWifi); Check.True(f.Store.Config.Preferences.Paused); Check.Equal(0, f.Backend.Writes);
    using var read = service.Prepare(["config", "get", "unknown-network"]); Check.Equal("direct", read.Execute().Text);
});
suite.Add("cli.config-startup-storage-failure-prevents-registry-write", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["config", "set", "startup", "true"]); f.Store.FailConfigSave = true;
    Check.True(plan.RequiresConfirmation); Check.Throws<IOException>(() => plan.Execute()); Check.Equal(0, p.StartupWrites);
});
suite.Add("cli.config-startup-preview-detects-external-change", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["config", "set", "startup", "true"]); p.StartupEnabled = true;
    Check.Equal(3, plan.Execute().ExitCode); Check.Equal(0, p.StartupWrites);
});
suite.Add("cli.config-startup-compares-full-registration-command", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot) { StartupEnabled = true, Registration = "old.exe tray" }; var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["config", "set", "startup", "true"]);
    Check.True(plan.Preview.Contains("old.exe tray")); p.Registration = "different.exe tray";
    Check.Equal(3, plan.Execute().ExitCode); Check.Equal(0, p.StartupWrites);
});
suite.Add("cli.status-includes-rule-explanation", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    Check.Equal(1, service.Status().Matches.Count);
    using var plan = service.Prepare(["status", "--verbose"]); Check.True(plan.Execute().Text.Contains("Rule -> Campus"));
});
suite.Add("cli.config-wifi-requires-confirmation", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["config", "set", "observe-wifi", "true"]); Check.True(plan.RequiresConfirmation); Check.False(f.Store.Config.Preferences.ObserveWifi);
});
suite.Add("cli.check-does-not-run-during-preview", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["check", "--url", "https://example.invalid/"]); Check.True(plan.RequiresConfirmation); Check.Equal(0, p.Checks);
    Check.Equal(0, plan.Execute().ExitCode); Check.Equal(1, p.Checks); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.check-default-does-not-request-traffic", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["check"]); Check.False(plan.RequiresConfirmation); plan.Execute(); Check.True(p.LastUrl is null);
});
suite.Add("cli.check-rejects-credentials-and-file", () =>
{
    Check.Throws<ArgumentException>(() => CommandService.ValidateUrl("https://u:p@example.invalid/"));
    Check.Throws<ArgumentException>(() => CommandService.ValidateUrl("file:///C:/Windows/a"));
});
suite.Add("cli.quit-requires-confirmation-and-keeps-settings", () =>
{
    var f = Data.Fixture(); var p = new TestPlatform(f.Snapshot); var service = new CommandService(f.Controller, p);
    using var plan = service.Prepare(["quit"]); Check.True(plan.RequiresConfirmation); Check.False(p.QuitCalled); plan.Execute(); Check.True(p.QuitCalled); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.plan-is-single-use", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    using var plan = service.Prepare(["direct"]); plan.Execute(); Check.Throws<InvalidOperationException>(() => plan.Execute());
});
suite.Add("cli.reject-unknown-and-duplicate-options", () =>
{
    var f = Data.Fixture(); var service = new CommandService(f.Controller, new TestPlatform(f.Snapshot));
    Check.Throws<ArgumentException>(() => service.Prepare(["down", "--bogus"]));
    Check.Throws<ArgumentException>(() => CommandLine.Parse(["status", "--json", "--json"]));
    Check.Throws<ArgumentException>(() => CommandLine.Parse(["profile", "add", "x", "--server"]));
});
suite.Add("cli.recovery-blocks-up", () =>
{
    var f = Data.Fixture(); f.Store.Journal = new() { Pending = new() { Before = new(), Intended = Data.Proxy() } };
    var service = new CommandService(new(f.Store, f.Backend), new TestPlatform(f.Snapshot));
    Check.Equal("Recovery", service.Status().State); using var plan = service.Prepare(["up"]); Check.Equal(5, plan.Execute().ExitCode); Check.Equal(0, f.Backend.Writes);
});
suite.Add("cli.status-conflict-is-warning-not-ok", () =>
{
    var f = Data.Fixture(true); var service = new CommandService(f.Controller, new TestPlatform(Data.Snapshot(f.Snapshot.Networks[0], Data.Network())));
    Check.Equal("Warning", service.Status().State); Check.Equal(0, f.Backend.Writes);
});
suite.Add("protocol.lf-and-windows-crlf-confirmation", () =>
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("execute\r\nexecute\ncancel\r\n")); using var reader = new StreamReader(stream);
    Check.Equal("execute", LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult());
    Check.Equal("execute", LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult());
    Check.Equal("cancel", LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult());
    Check.True(LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult() is null);
});
suite.Add("protocol.truncated-confirmation-never-executes", () =>
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("execute")); using var reader = new StreamReader(stream);
    Check.Throws<EndOfStreamException>(() => LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult());
});
suite.Add("protocol.oversized-message-rejected", () =>
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('a', 1024 * 1024 + 1))); using var reader = new StreamReader(stream);
    Check.Throws<InvalidDataException>(() => LineProtocol.ReadLine(reader, default).GetAwaiter().GetResult());
});
return suite.Run();

internal sealed class TestPlatform(NetworkSnapshot network) : ICommandPlatform
{
    public bool Demo => true;
    public bool Resident { get; set; } = true;
    public bool AutomationSuspended { get; set; }
    public NetworkSnapshot Network { get; set; } = network;
    public NetworkSnapshot ReadNetwork() => Network;
    public bool StartupEnabled { get; set; }
    public string Registration { get; set; } = "registered";
    public string? StartupCommand => StartupEnabled ? Registration : null;
    public int StartupWrites { get; private set; }
    public void SetStartup(bool enabled) { StartupWrites++; StartupEnabled = enabled; }
    public int Checks { get; private set; }
    public Uri? LastUrl { get; private set; }
    public CommandResult Check(Uri? target) { Checks++; LastUrl = target; return new(0, "fake diagnostics"); }
    public bool QuitCalled { get; private set; }
    public void Quit() => QuitCalled = true;
}
