using Proxyz.Core.Models;

namespace Proxyz.Core.Tests;

internal static class ControllerTests
{
    public static void Register(TestSuite s)
    {
        s.Add("apply.initial-backup-and-readback", () =>
        {
            var f = Data.Fixture(); var before = f.Backend.Value;
            var result = f.Controller.ApplyManual(f.Profile.Settings, f.Profile.Id);
            Check.True(result.Success); Check.Equal(1, f.Backend.Writes);
            Check.True(f.Store.Journal.Original!.SameAs(before)); Check.True(f.Store.Journal.Previous!.SameAs(before));
            Check.True(f.Store.Journal.LastApplied!.SameAs(f.Profile.Settings)); Check.True(f.Store.Journal.Pending is null);
            Check.True(f.Store.Config.Preferences.Paused);
        });
        s.Add("apply.noop-does-not-write", () => { var f = Data.Fixture(); Check.True(f.Controller.ApplyManual(new(), null).Success); Check.Equal(0, f.Backend.Writes); Check.True(f.Store.Journal.Original is null); });
        s.Add("apply.policy-blocked", () => { var f = Data.Fixture(); f.Backend.Policy = new(false, "Managed"); Check.False(f.Controller.ApplyManual(f.Profile.Settings, f.Profile.Id).Success); Check.Equal(0, f.Backend.Writes); });
        s.Add("apply.journal-failure-prevents-write", () =>
        {
            var f = Data.Fixture(); f.Store.OnJournalSave = (_, _) => throw new IOException("disk full");
            Check.Throws<IOException>(() => f.Controller.ApplyManual(f.Profile.Settings, f.Profile.Id)); Check.Equal(0, f.Backend.Writes);
        });
        s.Add("apply.pause-save-failure-prevents-write", () => { var f = Data.Fixture(); f.Store.FailConfigSave = true; Check.Throws<IOException>(() => f.Controller.ApplyManual(f.Profile.Settings, f.Profile.Id)); Check.Equal(0, f.Backend.Writes); });
        s.Add("apply.intent-is-durable-before-write", () =>
        {
            var f = Data.Fixture();
            f.Backend.OnWrite = (_, target) => { var pending = f.Store.Journal.Pending ?? throw new Exception("Missing durable intent"); Check.True(pending.Intended.SameAs(target)); f.Backend.Value = target; };
            Check.True(f.Controller.ApplyManual(f.Profile.Settings, null).Success);
        });
        s.Add("apply.preview-stale-rejected", () => { var f = Data.Fixture(); f.Backend.Value = Data.Proxy("external.invalid"); Check.False(f.Controller.ApplyManual(f.Profile.Settings, null, true, new()).Success); Check.Equal(0, f.Backend.Writes); });
        s.Add("apply.preview-race-never-adopts-unconfirmed-state", () =>
        {
            var f = Data.Fixture(); var preview = f.Backend.Value;
            var external = Data.Proxy("external.invalid");
            f.Backend.OnRead = count => { if (count == 2) f.Backend.Value = external; };
            Check.False(f.Controller.ApplyManual(f.Profile.Settings, null, true, preview).Success);
            Check.Equal(0, f.Backend.Writes); Check.True(f.Backend.Value.SameAs(external));
            Check.True(f.Store.Journal.Pending is null);
        });
        s.Add("apply.race-before-write-rejected", () =>
        {
            var f = Data.Fixture(); f.Backend.OnRead = count => { if (count == 2) f.Backend.Value = Data.Proxy("external.invalid"); };
            Check.False(f.Controller.ApplyManual(f.Profile.Settings, null).Success); Check.Equal(0, f.Backend.Writes); Check.True(f.Controller.ExternalHold);
        });
        s.Add("apply.failed-native-no-change", () =>
        {
            var f = Data.Fixture(); f.Backend.OnWrite = (_, _) => throw new IOException("native failure");
            var result = f.Controller.ApplyManual(f.Profile.Settings, null);
            Check.False(result.Success); Check.False(result.RecoveryRequired); Check.True(f.Backend.Value.IsDirectOnly); Check.True(f.Store.Journal.Pending is null);
        });
        s.Add("apply.failure-after-write-rolls-back", () =>
        {
            var f = Data.Fixture(); f.Backend.OnWrite = (count, target) => { f.Backend.Value = target; if (count == 1) throw new IOException("notification failure"); };
            var result = f.Controller.ApplyManual(f.Profile.Settings, null);
            Check.False(result.Success); Check.False(result.RecoveryRequired); Check.Equal(2, f.Backend.Writes); Check.True(f.Backend.Value.IsDirectOnly);
        });
        s.Add("apply.commit-failure-rolls-back", () =>
        {
            var f = Data.Fixture(); f.Store.OnJournalSave = (count, _) => { if (count == 2) throw new IOException("commit failed"); };
            var result = f.Controller.ApplyManual(f.Profile.Settings, null);
            Check.False(result.Success); Check.True(f.Backend.Value.IsDirectOnly); Check.False(result.RecoveryRequired);
        });
        s.Add("apply.rollback-failure-retains-intent", () =>
        {
            var f = Data.Fixture(); f.Backend.OnWrite = (count, target) => { if (count == 1) f.Backend.Value = target; throw new IOException("fail"); };
            var result = f.Controller.ApplyManual(f.Profile.Settings, null);
            Check.True(result.RecoveryRequired); Check.True(f.Store.Journal.Pending is not null); Check.True(f.Controller.RecoveryHold);
        });
        s.Add("apply.unrelated-external-write-not-rolled-back", () =>
        {
            var f = Data.Fixture(); f.Backend.OnWrite = (_, _) => f.Backend.Value = Data.Proxy("external.invalid");
            var result = f.Controller.ApplyManual(f.Profile.Settings, null);
            Check.True(result.RecoveryRequired); Check.Equal(1, f.Backend.Writes); Check.Equal("external.invalid:8080", f.Backend.Value.ProxyServer);
        });
        s.Add("automation.disabled-by-default", () => { var f = Data.Fixture(); Check.True(f.Controller.Reconcile(f.Snapshot).Operation is null); Check.Equal(0, f.Backend.Writes); });
        s.Add("automation.applies-when-enabled", () => { var f = Data.Fixture(true); Check.True(f.Controller.Reconcile(f.Snapshot).Operation!.Success); Check.Equal(1, f.Backend.Writes); });
        s.Add("automation.no-loop-on-own-notification", () => { var f = Data.Fixture(true); f.Controller.Reconcile(f.Snapshot); f.Controller.Reconcile(f.Snapshot); Check.Equal(1, f.Backend.Writes); });
        s.Add("automation.failure-latched-per-network", () =>
        {
            var f = Data.Fixture(true); f.Backend.OnWrite = (_, _) => throw new IOException("fail");
            f.Controller.Reconcile(f.Snapshot); int writes = f.Backend.Writes; f.Controller.Reconcile(f.Snapshot);
            Check.Equal(writes, f.Backend.Writes);
        });
        s.Add("automation.external-setting-holds", () =>
        {
            var f = Data.Fixture(true); f.Controller.Reconcile(f.Snapshot); f.Backend.Value = Data.Proxy("external.invalid");
            f.Controller.Reconcile(f.Snapshot); Check.True(f.Controller.ExternalHold); Check.Equal(1, f.Backend.Writes);
        });
        s.Add("automation.external-requires-consent", () =>
        {
            var f = Data.Fixture(true); f.Controller.Reconcile(f.Snapshot); f.Backend.Value = Data.Proxy("external.invalid");
            Check.False(f.Controller.Resume().Success); Check.True(f.Controller.Resume(true, f.Backend.Value).Success);
        });
        s.Add("automation.resume-stale-preview-rejected", () => { var f = Data.Fixture(); f.Backend.Value = Data.Proxy(); Check.False(f.Controller.Resume(true, new()).Success); });
        s.Add("automation.profile-edit-pauses", () => { var f = Data.Fixture(true); f.Controller.SaveProfile(f.Profile with { Note = "edit" }); Check.True(f.Store.Config.Preferences.Paused); });
        s.Add("automation.rule-edit-pauses", () => { var f = Data.Fixture(true); f.Controller.SaveRule(f.Store.Config.Rules[0] with { Name = "edit" }); Check.True(f.Store.Config.Preferences.Paused); });
        s.Add("restore.previous-and-initial", () =>
        {
            var f = Data.Fixture(); f.Controller.ApplyManual(f.Profile.Settings, null);
            f.Controller.ApplyManual(Data.Proxy("second.invalid"), null);
            Check.True(f.Controller.Restore(false, true).Success); Check.True(f.Backend.Value.SameAs(f.Profile.Settings));
            Check.True(f.Controller.Restore(true, true).Success); Check.True(f.Backend.Value.IsDirectOnly);
        });
        s.Add("restore.external-confirmation-required", () =>
        {
            var f = Data.Fixture(); f.Controller.ApplyManual(f.Profile.Settings, null); f.Backend.Value = Data.Proxy("other.invalid");
            Check.False(f.Controller.Restore(false, false).Success); Check.Equal(1, f.Backend.Writes);
        });
        s.Add("restore.preview-race-never-adopts-unconfirmed-state", () =>
        {
            var f = Data.Fixture(); f.Controller.ApplyManual(f.Profile.Settings, null);
            var preview = f.Backend.Value; var reads = f.Backend.Reads; var writes = f.Backend.Writes;
            var external = Data.Proxy("external.invalid");
            f.Backend.OnRead = count => { if (count == reads + 2) f.Backend.Value = external; };
            Check.False(f.Controller.Restore(false, true, preview).Success);
            Check.Equal(writes, f.Backend.Writes); Check.True(f.Backend.Value.SameAs(external));
            Check.True(f.Store.Journal.Pending is null);
        });
        s.Add("recovery.pending-blocks-automation", () =>
        {
            var f = Data.Fixture(true); f.Store.Journal = new() { Pending = new() { Before = new(), Intended = Data.Proxy() } };
            var c = new ProxyController(f.Store, f.Backend); c.Reconcile(f.Snapshot);
            Check.True(c.RecoveryHold); Check.Equal(0, f.Backend.Writes); Check.False(c.Resume(true).Success);
        });
        s.Add("recovery.pending-restores-before", () =>
        {
            var f = Data.Fixture(); f.Backend.Value = Data.Proxy();
            f.Store.Journal = new() { Original = new(), Pending = new() { Before = new(), Intended = Data.Proxy() } };
            var c = new ProxyController(f.Store, f.Backend);
            Check.True(c.Restore(false, true).Success); Check.True(f.Backend.Value.IsDirectOnly); Check.False(c.RecoveryHold); Check.True(f.Store.Journal.Pending is null);
        });
        s.Add("recovery.failed-rollback-preserves-older-target-after-restart", () =>
        {
            var f = Data.Fixture(); var originalTarget = new ProxySettings();
            f.Backend.Value = Data.Proxy();
            f.Store.Journal = new() { Pending = new() { Before = originalTarget, Intended = f.Backend.Value } };
            var controller = new ProxyController(f.Store, f.Backend);
            f.Backend.OnWrite = (count, target) =>
            {
                if (count == 1) f.Backend.Value = target;
                throw new IOException("Injected recovery and rollback failure");
            };
            Check.True(controller.Restore(false, true).RecoveryRequired);
            Check.True(f.Store.Journal.Pending!.RecoveryTarget!.SameAs(originalTarget));
            // A later external value makes loss of the older target observable.
            f.Backend.Value = Data.Proxy("external.invalid"); f.Backend.OnWrite = null;
            var restarted = new ProxyController(f.Store, f.Backend);
            Check.True(restarted.RecoveryHold);
            Check.True(restarted.Restore(false, true, f.Backend.Value).Success);
            Check.True(f.Backend.Value.SameAs(originalTarget));
            Check.True(f.Store.Journal.Pending is null);
        });
        s.Add("recovery.interrupted-at-write-preserves-older-target", () =>
        {
            var f = Data.Fixture(); var originalTarget = new ProxySettings();
            f.Backend.Value = Data.Proxy();
            f.Store.Journal = new() { Pending = new() { Before = originalTarget, Intended = f.Backend.Value } };
            var controller = new ProxyController(f.Store, f.Backend);
            PendingChange? durable = null;
            f.Backend.OnWrite = (_, _) =>
            {
                durable = f.Store.Journal.Pending;
                throw new IOException("Injected failure");
            };
            Check.False(controller.Restore(false, true).Success);
            Check.True(durable!.Before.SameAs(Data.Proxy()));
            Check.True(durable.RecoveryTarget!.SameAs(originalTarget));
            Check.True(f.Store.Journal.Pending!.Before.SameAs(originalTarget));
        });
        s.Add("profile.delete-referenced-rejected", () => { var f = Data.Fixture(); Check.Throws<InvalidOperationException>(() => f.Controller.DeleteProfile(f.Profile.Id)); });
        s.Add("profile.delete-unused", () => { var f = Data.Fixture(); f.Controller.DeleteRule(f.Store.Config.Rules[0].Id); f.Controller.DeleteProfile(f.Profile.Id); Check.Equal(0, f.Store.Config.Profiles.Count); });
        s.Add("config.snapshot-lists-not-shared", () => { var f = Data.Fixture(); f.Controller.GetConfig().Profiles.Clear(); Check.Equal(1, f.Controller.GetConfig().Profiles.Count); });
        s.Add("controller.serializes-parallel-applies", () =>
        {
            var f = Data.Fixture();
            Parallel.For(0, 20, i => Check.True(f.Controller.ApplyManual(Data.Proxy("host" + i + ".invalid"), null, true).Success));
            Check.Equal(20, f.Backend.Writes); Check.True(f.Store.Journal.Pending is null);
        });
    }
}
