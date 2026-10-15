using Proxyz.Core.Models;

namespace Proxyz.Core;

/// <summary>
/// Serializes configuration and OS operations. All public methods are synchronous;
/// the UI invokes them on a worker thread. Windows settings are not transactional,
/// so a durable intent journal and conservative ownership checks are essential.
/// </summary>
public sealed class ProxyController
{
    private readonly object _gate = new();
    private readonly IStateStore _store;
    private readonly IProxyBackend _backend;
    private readonly IAuditLog _log;
    private AppConfig _config;
    private ChangeJournal _journal;
    private bool _externalHold;
    private bool _recoveryHold;
    private string? _failedAutomaticFingerprint;

    public ProxyController(IStateStore store, IProxyBackend backend, IAuditLog? log = null)
    {
        _store = store; _backend = backend; _log = log ?? new NullAuditLog();
        _config = store.LoadConfig();
        _journal = store.LoadJournal();
        _recoveryHold = _journal.Pending is not null;
    }
    public AppConfig GetConfig() { lock (_gate) return Clone(_config); }
    public ChangeJournal GetJournal() { lock (_gate) return _journal; }
    public bool ExternalHold { get { lock (_gate) return _externalHold; } }
    public bool RecoveryHold { get { lock (_gate) return _recoveryHold; } }
    public bool AutomaticFailureHold { get { lock (_gate) return _failedAutomaticFingerprint is not null; } }

    public ProxySettings ReadCurrent()
    {
        lock (_gate)
        {
            var current = _backend.Read();
            if (_journal.LastApplied is not null && !current.SameAs(_journal.LastApplied)) _externalHold = true;
            return current;
        }
    }
    public PolicyStatus ReadPolicy() { lock (_gate) return _backend.ReadPolicy(); }

    public void SaveProfile(Profile profile)
    {
        lock (_gate)
        {
            var errors = Validation.Profile(profile, _config.Profiles);
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            var list = _config.Profiles.Where(p => p.Id != profile.Id).Append(profile).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Save(_config with { Profiles = list, Preferences = _config.Preferences with { Paused = true } });
        }
    }
    public void DeleteProfile(Guid id)
    {
        lock (_gate)
        {
            if (_config.Rules.Any(r => r.ProfileId == id)) throw new InvalidOperationException("このプロファイルを使うルールを先に削除・変更してください。");
            Save(_config with { Profiles = _config.Profiles.Where(p => p.Id != id).ToList(), Preferences = _config.Preferences with { Paused = true } });
        }
    }
    public void SaveRule(NetworkRule rule)
    {
        lock (_gate)
        {
            var errors = Validation.Rule(rule, _config);
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            Save(_config with { Rules = _config.Rules.Where(r => r.Id != rule.Id).Append(rule).ToList(), Preferences = _config.Preferences with { Paused = true } });
        }
    }
    public void DeleteRule(Guid id)
    {
        lock (_gate) Save(_config with { Rules = _config.Rules.Where(r => r.Id != id).ToList(), Preferences = _config.Preferences with { Paused = true } });
    }
    public void SavePreferences(UserPreferences preferences)
    {
        lock (_gate) Save(_config with { Preferences = preferences });
    }
    public void Pause()
    {
        lock (_gate) Save(_config with { Preferences = _config.Preferences with { Paused = true } });
    }
    public OperationResult Resume(bool acknowledgeExternal = false, ProxySettings? expectedBefore = null)
    {
        lock (_gate)
        {
            if (_recoveryHold) return new(false, "前回の変更が完了していません。先に復元してください。", true);
            var current = ReadCurrent();
            if (expectedBefore is not null && !current.SameAs(expectedBefore)) return new(false, "確認画面の表示後に Windows 設定が変わりました。もう一度確認してください。");
            if (_externalHold && !acknowledgeExternal) return new(false, "Windows 設定が外部で変更されています。内容を確認してから再開してください。");
            var policy = _backend.ReadPolicy();
            if (!policy.CanChange) return new(false, policy.Reason);
            // Persist a pause before accepting external state. If a later save fails,
            // the next refresh/restart must not resume automation unexpectedly.
            Save(_config with { Preferences = _config.Preferences with { Paused = true } });
            if (_externalHold)
            {
                // Explicit adoption is local bookkeeping only, not an OS write.
                PersistJournal(_journal with { LastApplied = current, LastProfileId = null });
                _externalHold = false;
            }
            Save(_config with { Preferences = _config.Preferences with { AutomationEnabled = true, Paused = false } });
            _failedAutomaticFingerprint = null;
            return new(true, "自動切替を再開しました。");
        }
    }

    public OperationResult ApplyManual(ProxySettings desired, Guid? profileId, bool acknowledgeExternal = false, ProxySettings? expectedBefore = null)
    {
        lock (_gate)
        {
            var errors = Validation.Settings(desired);
            if (errors.Count > 0) return new(false, string.Join("\n", errors));
            Pause(); // Persist first: a failed manual operation must not cause an auto retry.
            if (_recoveryHold) return new(false, "前回の変更が未完了です。先に「元に戻す」で復元してください。", true);
            return Apply(desired, profileId, acknowledgeExternal, restoring: false, expectedBefore);
        }
    }

    public OperationResult Restore(bool original, bool acknowledgeExternal, ProxySettings? expectedBefore = null)
    {
        lock (_gate)
        {
            Pause();
            var target = _journal.Pending?.RecoveryTarget ?? _journal.Pending?.Before ?? (original ? _journal.Original : _journal.Previous);
            if (target is null) return new(false, "復元する記録がありません。");
            return Apply(target, null, acknowledgeExternal, restoring: true, expectedBefore);
        }
    }

    public (RuleDecision Decision, OperationResult? Operation) Reconcile(NetworkSnapshot snapshot)
    {
        lock (_gate)
        {
            ReadCurrent();
            var decision = RuleEngine.Decide(_config, snapshot);
            if (snapshot.Reliable && _failedAutomaticFingerprint is not null && _failedAutomaticFingerprint != snapshot.Fingerprint)
                _failedAutomaticFingerprint = null;
            if (!_config.Preferences.AutomationEnabled || _config.Preferences.Paused)
                return (decision, null);
            if (_recoveryHold) return (new(DecisionKind.Keep, "未完了の変更があるため自動切替を停止しています。先に復元してください。"), null);
            if (_externalHold) return (new(DecisionKind.Keep, "設定が外部で変更されたため自動切替を停止しています。確認して再開してください。"), null);
            if (_failedAutomaticFingerprint == snapshot.Fingerprint)
                return (new(DecisionKind.Keep, "直前の自動適用が失敗したため、同じ接続先での再試行を止めています。確認して再開してください。"), null);
            if (decision.Kind == DecisionKind.ApplyDirect)
            {
                var result = Apply(ProxySettings.Direct(), null, false, false);
                if (!result.Success) _failedAutomaticFingerprint = snapshot.Fingerprint;
                return (decision, result);
            }
            if (decision.Kind == DecisionKind.ApplyProfile)
            {
                var profile = _config.Profiles.Single(p => p.Id == decision.ProfileId);
                var result = Apply(profile.Settings, profile.Id, false, false);
                if (!result.Success) _failedAutomaticFingerprint = snapshot.Fingerprint;
                return (decision, result);
            }
            return (decision, null);
        }
    }

    private OperationResult Apply(ProxySettings desired, Guid? profileId, bool acknowledgeExternal, bool restoring, ProxySettings? expectedBefore = null)
    {
        var policy = _backend.ReadPolicy();
        if (!policy.CanChange) return new(false, policy.Reason, _recoveryHold);
        ProxySettings before;
        try { before = _backend.Read(); }
        catch (Exception e) { return new(false, "現在の設定を取得できないため変更しません。" + SafeError(e), _recoveryHold); }
        // Compare the preview with the same snapshot used for journaling. A separate
        // earlier read could accept an external change as the new Before value.
        if (expectedBefore is not null && !before.SameAs(expectedBefore))
            return new(false, "確認画面の表示後に Windows 設定が変わりました。もう一度確認してください。", _recoveryHold);
        bool externallyChanged = _journal.LastApplied is not null && !before.SameAs(_journal.LastApplied);
        if ((externallyChanged || _externalHold) && !acknowledgeExternal)
        {
            _externalHold = true;
            return new(false, "設定が外部で変更されています。現在の内容を確認してから操作してください。", _recoveryHold);
        }
        if (before.SameAs(desired))
        {
            if (restoring || acknowledgeExternal)
            {
                PersistJournal(_journal with { Pending = null, LastApplied = before, LastProfileId = profileId });
                _externalHold = false; _recoveryHold = false;
            }
            return new(true, "Windows はすでに選択した設定です。通信の成功を保証する表示ではありません。");
        }
        var previousJournal = _journal;
        // A failure here throws before touching Windows. Keep the intent durable.
        PersistJournal(_journal with
        {
            Original = _journal.Original ?? before,
            Pending = new()
            {
                Before = before, Intended = desired,
                RecoveryTarget = restoring ? previousJournal.Pending?.RecoveryTarget ?? previousJournal.Pending?.Before : null
            }
        });
        _recoveryHold = true;
        try
        {
            // Best-effort compare immediately before the nontransactional native call.
            if (!_backend.Read().SameAs(before))
            {
                PersistJournal(previousJournal);
                _externalHold = true;
                _recoveryHold = previousJournal.Pending is not null;
                return new(false, "適用直前に別の変更を検出しました。上書きしていません。", _recoveryHold);
            }
            policy = _backend.ReadPolicy();
            if (!policy.CanChange) throw new InvalidOperationException(policy.Reason);
            _backend.Write(desired);
            var actual = _backend.Read();
            if (!actual.SameAs(desired)) throw new IOException("Windows から読み戻した値が要求と一致しません。");
            PersistJournal(_journal with
            {
                Previous = before, LastApplied = actual, LastProfileId = profileId,
                LastChangeAt = DateTimeOffset.UtcNow, Pending = null
            });
            _recoveryHold = false; _externalHold = false;
            _log.Record(restoring ? "restore" : "apply", "verified", profileId);
            return new(true, "Windows 設定に適用しました。インターネットへの接続は未検証です。");
        }
        catch (Exception e)
        {
            _log.Record(restoring ? "restore" : "apply", "failed", profileId);
            try
            {
                var observed = _backend.Read();
                if (!observed.SameAs(before))
                {
                    if (!observed.CouldBePartialWriteOf(before, desired) || !_backend.ReadPolicy().CanChange)
                        return new(false, "変更を完了できず、外部変更または管理制限も検出しました。自動上書きせず復元記録を保持しています。" + SafeError(e), true);
                    _backend.Write(before);
                    if (!_backend.Read().SameAs(before)) throw new IOException("復元後の読み戻しが一致しません。");
                }
                // If this was recovery of an older pending transaction, do not erase it.
                PersistJournal(previousJournal);
                _recoveryHold = previousJournal.Pending is not null;
                _externalHold = previousJournal.LastApplied is not null && !before.SameAs(previousJournal.LastApplied);
                _log.Record("rollback", "verified");
                return new(false, "適用できませんでした。今回の操作前の設定に戻っています。" + SafeError(e), _recoveryHold);
            }
            catch (Exception rollbackError)
            {
                _recoveryHold = true;
                _log.Record("rollback", "requires-recovery");
                return new(false, "適用と復元を完了できませんでした。自動切替を停止し、復元記録を保持しています。" + SafeError(e) + SafeError(rollbackError), true);
            }
        }
    }
    private void Save(AppConfig config) { _store.SaveConfig(config); _config = config; _failedAutomaticFingerprint = null; }
    private void PersistJournal(ChangeJournal journal) { _store.SaveJournal(journal); _journal = journal; }
    private static AppConfig Clone(AppConfig config) => config with { Profiles = [.. config.Profiles], Rules = [.. config.Rules] };
    private static string SafeError(Exception e) => "\n詳細: " + e.GetType().Name + " — " + e.Message;
}
