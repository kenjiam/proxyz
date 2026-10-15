using System.Text.Json;
using Proxyz.Core.Models;
using Proxyz.Core.Storage;

namespace Proxyz.Core.Tests;

internal static class StorageTests
{
    public static void Register(TestSuite s)
    {
        s.Add("storage.missing-config-is-safe-default", () => { using var t = new Temp(); var c = new JsonStateStore(t.Path).LoadConfig(); Check.False(c.Preferences.AutomationEnabled); Check.Equal(UnknownNetworkAction.KeepCurrent, c.Preferences.UnknownNetwork); });
        s.Add("storage.config-roundtrip", () =>
        {
            using var t = new Temp(); var store = new JsonStateStore(t.Path); var f = Data.Fixture();
            store.SaveConfig(f.Store.Config); var actual = store.LoadConfig();
            Check.Equal(f.Profile.Id, actual.Profiles.Single().Id); Check.Equal(1, actual.Rules.Count); Check.True(actual.Profiles[0].Settings.SameAs(f.Profile.Settings));
        });
        s.Add("storage.replacement-has-backup", () =>
        {
            using var t = new Temp(); var store = new JsonStateStore(t.Path); store.SaveConfig(new());
            store.SaveConfig(new() { Preferences = new() { UiScale = 1.5 } });
            Check.True(File.Exists(System.IO.Path.Combine(t.Path, "config.json.bak"))); Check.Equal(1.5, store.LoadConfig().Preferences.UiScale);
        });
        s.Add("storage.oversized-write-preserves-existing", () =>
        {
            using var t = new Temp(); var store = new JsonStateStore(t.Path);
            store.SaveConfig(new() { Preferences = new() { UiScale = 1.25 } });
            var profiles = Enumerable.Range(0, 200).Select(i => new Profile
            { Name = "large-" + i, Settings = new() { ProxyBypass = new string('x', 16000) } }).ToList();
            Check.Throws<InvalidDataException>(() => store.SaveConfig(new() { Profiles = profiles }));
            Check.Equal(1.25, store.LoadConfig().Preferences.UiScale);
            Check.Equal(0, Directory.GetFiles(t.Path, "*.tmp").Length);
        });
        s.Add("storage.corrupt-config-never-overwritten", () =>
        {
            using var t = new Temp(); var path = System.IO.Path.Combine(t.Path, "config.json"); File.WriteAllText(path, "broken");
            Check.Throws<InvalidDataException>(() => new JsonStateStore(t.Path).LoadConfig()); Check.Equal("broken", File.ReadAllText(path));
        });
        s.Add("storage.unknown-property-rejected", () =>
        {
            using var t = new Temp(); File.WriteAllText(System.IO.Path.Combine(t.Path, "config.json"), "{\"Command\":\"anything\"}");
            Check.Throws<InvalidDataException>(() => new JsonStateStore(t.Path).LoadConfig());
        });
        s.Add("storage.journal-roundtrip", () =>
        {
            using var t = new Temp(); var store = new JsonStateStore(t.Path);
            store.SaveJournal(new() { Original = new() { Flags = 9, AutoConfigUrl = "disabled-value-preserved" }, Pending = new() { Before = new(), Intended = Data.Proxy() } });
            var j = store.LoadJournal(); Check.Equal("disabled-value-preserved", j.Original!.AutoConfigUrl); Check.True(j.Pending!.Intended.SameAs(Data.Proxy()));
        });
        s.Add("storage.newer-journal-rejected", () =>
        {
            using var t = new Temp(); File.WriteAllText(System.IO.Path.Combine(t.Path, "journal.json"), "{\"SchemaVersion\":2}");
            Check.Throws<InvalidDataException>(() => new JsonStateStore(t.Path).LoadJournal());
        });
        s.Add("storage.recovery-target-roundtrip", () =>
        {
            using var t = new Temp(); var store = new JsonStateStore(t.Path);
            store.SaveJournal(new() { Pending = new() { Before = Data.Proxy(), Intended = new(), RecoveryTarget = new() } });
            var journal = store.LoadJournal();
            Check.True(journal.Pending!.Before.SameAs(Data.Proxy()));
            Check.True(journal.Pending.RecoveryTarget!.IsDirectOnly);
        });
        s.Add("storage.legacy-pending-omits-recovery-target", () =>
        {
            var json = JsonSerializer.Serialize(new ChangeJournal { Pending = new() }, JsonStateStore.JsonOptions);
            Check.False(json.Contains("RecoveryTarget"));
            Check.True(JsonSerializer.Deserialize<ChangeJournal>(json, JsonStateStore.JsonOptions)!.Pending!.RecoveryTarget is null);
        });
        s.Add("storage.json-does-not-serialize-derived-values", () => Check.False(JsonSerializer.Serialize(new ProxySettings(), JsonStateStore.JsonOptions).Contains("Summary")));
        s.Add("storage.audit-excludes-settings", () =>
        {
            using var t = new Temp(); new LocalAuditLog(t.Path).Record("apply", "verified", Guid.NewGuid());
            string log = File.ReadAllText(System.IO.Path.Combine(t.Path, "events.jsonl")); Check.False(log.Contains("ProxyServer")); Check.True(log.Contains("verified"));
        });
    }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Proxyz-Tests-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
