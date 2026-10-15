using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Proxyz.App.Infrastructure;
using Proxyz.App.Views;
using Proxyz.Core;
using Proxyz.Core.Models;
using Proxyz.Windows;

namespace Proxyz.App.ViewModels;

public sealed record RuleRow(NetworkRule Rule, string ProfileName)
{
    public string Name => Rule.Name;
    public string State => Rule.Enabled ? "有効" : "無効";
    public string Condition => Rule.ConditionSummary;
    public string Display => $"{Name} — {ProfileName} / {State}";
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppRuntime _runtime;
    private readonly Func<Window> _owner;
    private readonly Action _exit;
    private readonly List<AsyncCommand> _async = [];
    private readonly List<RelayCommand> _sync = [];
    private AppConfig _config;
    private ChangeJournal _journal = new();
    private NetworkSnapshot _snapshot = new();
    private ProxySettings? _current;
    private bool _busy, _wifi, _pauseVpn, _startup;
    private double _scale = 1;
    private int _unknown, _tab;
    private Profile? _selectedProfile;
    private RuleRow? _selectedRule;
    private string _status = "準備しています。", _automatic = "読み込み中", _reason = "", _policy = "", _networkNotice = "", _currentName = "読み込み中";
    public event Action? StateChanged;
    public event Action? RefreshRequested;
    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<NetworkIdentity> Networks { get; } = [];
    public string[] UnknownOptions { get; } = ["現在の設定を維持する（推奨）", "直接接続に切り替える"];
    public bool IsDemo => _runtime.Demo;
    public bool IsReal => !IsDemo;
    public string WindowTitle => IsDemo ? "Proxyz — デモ（Windows 設定は変更しません）" : "Proxyz";
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Notify(nameof(IsReady)); Notify(nameof(BusyText)); RefreshCommands(); } } }
    public bool IsReady => !IsBusy;
    public string BusyText => IsBusy ? "処理中…" : "";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string AutomaticState { get => _automatic; private set => Set(ref _automatic, value); }
    public string Reason { get => _reason; private set => Set(ref _reason, value); }
    public string PolicyNotice { get => _policy; private set { Set(ref _policy, value); Notify(nameof(HasPolicyNotice)); } }
    public bool HasPolicyNotice => _policy.Length > 0;
    public string NetworkNotice { get => _networkNotice; private set { Set(ref _networkNotice, value); Notify(nameof(HasNetworkNotice)); } }
    public bool HasNetworkNotice => _networkNotice.Length > 0;
    public string CurrentProfileName { get => _currentName; private set => Set(ref _currentName, value); }
    public string CurrentDescription => ConfirmWindow.Describe(_current);
    public string BackupDescription => _journal.Pending is not null
        ? "前回の変更が未完了です。自動切替は停止しています。「直前の設定に戻す」で復元してください。"
        : _journal.Original is null ? "まだ設定を変更していません。初めて変更する直前に復元用の記録を保存します。"
        : "初回変更前と直前の設定を端末内に記録しています。復元後の自動切替は一時停止します。";
    public bool AutomationRunning => _config.Preferences.AutomationEnabled && !_config.Preferences.Paused && !_runtime.Controller.ExternalHold && !_runtime.Controller.RecoveryHold && !_runtime.Controller.AutomaticFailureHold;
    public string ToggleText => AutomationRunning ? "自動切替を一時停止(_A)" : "自動切替を再開(_A)…";
    public string NetworkCountText => Networks.Count == 0 ? "接続中のネットワークがありません。" : $"{Networks.Count} 件の接続を検出";
    public Profile? SelectedProfile { get => _selectedProfile; set { if (Set(ref _selectedProfile, value)) RefreshCommands(); } }
    public RuleRow? SelectedRule { get => _selectedRule; set { if (Set(ref _selectedRule, value)) RefreshCommands(); } }
    public int SelectedTab { get => _tab; set => Set(ref _tab, value); }
    public bool ObserveWifi { get => _wifi; set => Set(ref _wifi, value); }
    public bool PauseOnVpn { get => _pauseVpn; set => Set(ref _pauseVpn, value); }
    public bool StartupEnabled { get => _startup; set => Set(ref _startup, value); }
    public int UnknownActionIndex { get => _unknown; set => Set(ref _unknown, value); }
    public double UiScale { get => _scale; set { if (Set(ref _scale, value)) Notify(nameof(ScaleLabel)); } }
    public string ScaleLabel => $"アプリ内の追加文字倍率：{_scale:P0}";
    public ICommand RefreshCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DirectCommand { get; }
    public ICommand ToggleAutomationCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestoreOriginalCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand EditRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand OpenProxySettingsCommand { get; }
    public ICommand OpenLocationSettingsCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ExitCommand { get; }

    public MainViewModel(AppRuntime runtime, Func<Window> owner, Action exit)
    {
        _runtime = runtime; _owner = owner; _exit = exit; _config = runtime.Controller.GetConfig();
        ObserveWifi = _config.Preferences.ObserveWifi; PauseOnVpn = _config.Preferences.PauseOnVpn;
        UnknownActionIndex = (int)_config.Preferences.UnknownNetwork; UiScale = _config.Preferences.UiScale;
        try { StartupEnabled = !IsDemo && StartupRegistration.IsRegistered(); } catch { StartupEnabled = false; }
        RefreshCommand = Async(() => RefreshCoreAsync());
        ApplyCommand = Async(() => ApplyProfileAsync(SelectedProfile!), () => SelectedProfile is not null);
        DirectCommand = Async(() => ApplySettingsAsync(ProxySettings.Direct(), null, "直接接続に切り替える"));
        ToggleAutomationCommand = Async(ToggleAutomationAsync);
        RestoreCommand = Async(() => RestoreAsync(false), () => _journal.Previous is not null || _journal.Pending is not null);
        RestoreOriginalCommand = Async(() => RestoreAsync(true), () => _journal.Original is not null || _journal.Pending is not null);
        AddProfileCommand = Async(() => EditProfileAsync(new Profile { Name = "新しい設定" }));
        CaptureCommand = Async(CaptureAsync);
        EditProfileCommand = Async(() => EditProfileAsync(SelectedProfile!), () => SelectedProfile is not null);
        DeleteProfileCommand = Async(DeleteProfileAsync, () => SelectedProfile is not null);
        AddRuleCommand = Async(() => EditRuleAsync(new() { Name = "新しいルール", ProfileId = SelectedProfile?.Id ?? Profiles.First().Id }), () => Profiles.Count > 0);
        EditRuleCommand = Async(() => EditRuleAsync(SelectedRule!.Rule), () => SelectedRule is not null);
        DeleteRuleCommand = Async(DeleteRuleAsync, () => SelectedRule is not null);
        SaveSettingsCommand = Async(SaveSettingsAsync);
        OpenProxySettingsCommand = Sync(() => OpenUri("ms-settings:network-proxy"));
        OpenLocationSettingsCommand = Sync(() => OpenUri("ms-settings:privacy-location"));
        OpenDataCommand = Sync(() =>
        {
            Directory.CreateDirectory(_runtime.DataDirectory);
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { Arguments = "\"" + _runtime.DataDirectory + "\"", UseShellExecute = true });
        });
        HelpCommand = Sync(() => new GuideWindow { Owner = _owner() }.ShowDialog());
        ExitCommand = Sync(_exit);
    }
    private ICommand Async(Func<Task> action, Func<bool>? allowed = null)
    {
        var command = new AsyncCommand(() => RunBusyAsync(action), ReportError, () => !IsBusy && (allowed?.Invoke() ?? true));
        _async.Add(command); return command;
    }
    private ICommand Sync(Action action)
    {
        var command = new RelayCommand(() => { try { action(); } catch (Exception e) { ReportError(e); } }, () => !IsBusy);
        _sync.Add(command); return command;
    }
    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; StateChanged?.Invoke(); }
    }
    public async Task RefreshAsync()
    {
        if (IsBusy) { RefreshRequested?.Invoke(); return; }
        try { await RunBusyAsync(RefreshCoreAsync); }
        catch (Exception e)
        {
            Status = "状態を確認できません。自動変更を中断しています。詳細: " + e.Message;
            try { await Task.Run(() => _runtime.Controller.Pause()); } catch { }
            AutomaticState = "エラーのため停止";
        }
    }
    private async Task RefreshCoreAsync()
    {
        var data = await Task.Run(() =>
        {
            var snapshot = _runtime.ReadNetwork();
            var evaluated = _runtime.Controller.Reconcile(snapshot);
            return (snapshot, evaluated, current: _runtime.Controller.ReadCurrent(),
                policy: _runtime.Controller.ReadPolicy(), config: _runtime.Controller.GetConfig(), journal: _runtime.Controller.GetJournal());
        });
        _snapshot = data.snapshot; _current = data.current; _config = data.config; _journal = data.journal;
        var profileId = SelectedProfile?.Id; var ruleId = SelectedRule?.Rule.Id;
        Replace(Profiles, _config.Profiles);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profileId) ?? Profiles.FirstOrDefault();
        Replace(Rules, _config.Rules.Select(r => new RuleRow(r, _config.Profiles.FirstOrDefault(p => p.Id == r.ProfileId)?.Name ?? "不明")));
        SelectedRule = Rules.FirstOrDefault(r => r.Rule.Id == ruleId) ?? Rules.FirstOrDefault();
        Replace(Networks, _snapshot.Networks);
        CurrentProfileName = _config.Profiles.FirstOrDefault(p => p.Settings.SameAs(_current))?.Name ?? "Windows の現在の設定";
        AutomaticState = _runtime.Controller.RecoveryHold ? "復元待ち" : _runtime.Controller.ExternalHold ? "外部変更のため停止" :
            !_config.Preferences.AutomationEnabled ? "自動切替：無効" : _config.Preferences.Paused ? "自動切替：一時停止" :
            _runtime.Controller.AutomaticFailureHold ? "適用失敗のため停止" : "自動切替：有効";
        Reason = data.evaluated.Decision.Reason;
        PolicyNotice = data.policy.CanChange ? data.policy.Notice : data.policy.Reason;
        NetworkNotice = string.Join(Environment.NewLine, new[] { _snapshot.Notice, _runtime.MonitorNotice }.Where(s => s.Length > 0));
        if (data.evaluated.Operation is not null) Status = data.evaluated.Operation.Message;
        else if (Status == "準備しています。") Status = IsDemo ? "デモモードです。Windows は変更しません。" : "設定を読み込みました。現在の内容を確認してください。";
        Notify(nameof(CurrentDescription)); Notify(nameof(BackupDescription)); Notify(nameof(ToggleText)); Notify(nameof(AutomationRunning)); Notify(nameof(NetworkCountText));
        RefreshCommands(); StateChanged?.Invoke();
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var items = source.ToList();
        if (target.SequenceEqual(items)) return;
        target.Clear(); foreach (var item in items) target.Add(item);
    }
    private void RefreshCommands() { foreach (var c in _async) c.Refresh(); foreach (var c in _sync) c.Refresh(); }

    public void SelectAndApply(Guid id)
    {
        SelectedTab = 0; SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id);
        if (ApplyCommand.CanExecute(null)) ApplyCommand.Execute(null);
    }
    private Task ApplyProfileAsync(Profile profile) => ApplySettingsAsync(profile.Settings, profile.Id, $"「{profile.Name}」に切り替える");
    private async Task ApplySettingsAsync(ProxySettings target, Guid? id, string title)
    {
        var before = await Task.Run(_runtime.Controller.ReadCurrent);
        if (!Confirm(title, "手動で切り替えると、自動切替は一時停止します。再開するまでは別のネットワークに移動しても自動では変わりません。", before, target)) return;
        var result = await Task.Run(() => _runtime.Controller.ApplyManual(target, id, acknowledgeExternal: true, expectedBefore: before));
        Status = result.Message;
        await RefreshCoreAsync();
    }
    private async Task ToggleAutomationAsync()
    {
        if (AutomationRunning)
        {
            await Task.Run(_runtime.Controller.Pause); Status = "自動切替を一時停止しました。Windows 設定は維持しています。";
        }
        else
        {
            var before = await Task.Run(_runtime.Controller.ReadCurrent);
            var decision = RuleEngine.Decide(_runtime.Controller.GetConfig(), _snapshot);
            ProxySettings? target = decision.Kind == DecisionKind.ApplyDirect ? ProxySettings.Direct() :
                decision.ProfileId.HasValue ? _config.Profiles.FirstOrDefault(p => p.Id == decision.ProfileId)?.Settings : before;
            if (!Confirm("自動切替を再開する", "登録済みのルールと未登録ネットワークの設定に従って Windows のプロキシを自動変更します。\n" + decision.Reason +
                "\n外部変更がある場合は、表示中の現在の設定を確認したうえで再開します。", before, target, "再開する(_A)")) return;
            Status = (await Task.Run(() => _runtime.Controller.Resume(true, before))).Message;
        }
        await RefreshCoreAsync();
    }
    private async Task RestoreAsync(bool original)
    {
        var before = await Task.Run(_runtime.Controller.ReadCurrent);
        var journal = _runtime.Controller.GetJournal();
        var target = journal.Pending?.RecoveryTarget ?? journal.Pending?.Before ?? (original ? journal.Original : journal.Previous);
        if (target is null) { Status = "復元記録がありません。"; return; }
        if (!Confirm(journal.Pending is not null ? "未完了の変更前に戻す" : original ? "初回変更前の設定に戻す" : "直前の設定に戻す", "表示された設定に戻します。自動切替は一時停止します。外部で設定が変更されている場合も、内容を確認してから復元してください。", before, target, "復元する(_R)")) return;
        Status = (await Task.Run(() => _runtime.Controller.Restore(original, true, before))).Message;
        await RefreshCoreAsync();
    }
    private async Task CaptureAsync()
    {
        var settings = await Task.Run(_runtime.Controller.ReadCurrent);
        string baseName = "現在の Windows 設定";
        string name = baseName; int i = 2;
        while (Profiles.Any(p => p.Name == name)) name = baseName + " " + i++;
        await EditProfileAsync(new Profile { Name = name, Settings = settings });
    }
    private async Task EditProfileAsync(Profile profile)
    {
        var dialog = new ProfileEditorWindow(profile, _config.Profiles) { Owner = _owner() };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        await Task.Run(() => _runtime.Controller.SaveProfile(dialog.Result));
        Status = "プロファイルを保存しました。Windows は変更していません。自動切替を使う場合は再開してください。";
        await RefreshCoreAsync(); SelectedProfile = Profiles.FirstOrDefault(p => p.Id == dialog.Result.Id);
    }
    private async Task DeleteProfileAsync()
    {
        var profile = SelectedProfile!;
        if (MessageBox.Show(_owner(), $"「{profile.Name}」を削除します。Windows の現在の設定は変更しません。", "プロファイルの削除", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Task.Run(() => _runtime.Controller.DeleteProfile(profile.Id));
        Status = "プロファイルを削除しました。Windows 設定は維持しています。";
        await RefreshCoreAsync();
    }
    private async Task EditRuleAsync(NetworkRule rule)
    {
        var dialog = new RuleEditorWindow(rule, _config, _snapshot) { Owner = _owner() };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        await Task.Run(() => _runtime.Controller.SaveRule(dialog.Result));
        Status = "ルールを保存しました。自動切替を使う場合は再開してください。";
        await RefreshCoreAsync(); SelectedRule = Rules.FirstOrDefault(r => r.Rule.Id == dialog.Result.Id);
    }
    private async Task DeleteRuleAsync()
    {
        var rule = SelectedRule!.Rule;
        if (MessageBox.Show(_owner(), $"「{rule.Name}」を削除します。", "ルールの削除", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await Task.Run(() => _runtime.Controller.DeleteRule(rule.Id)); Status = "ルールを削除しました。自動切替は一時停止しました。";
        await RefreshCoreAsync();
    }
    private async Task SaveSettingsAsync()
    {
        if (ObserveWifi && !_config.Preferences.ObserveWifi && !IsDemo &&
            MessageBox.Show(_owner(), "接続中の Wi-Fi 名を取得するために、Windows が位置情報の許可を求める場合があります。近隣ネットワークのスキャンや外部送信は行いません。", "Wi-Fi の識別", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        bool oldStartup = IsDemo ? false : StartupRegistration.IsRegistered();
        var preferences = _config.Preferences with { ObserveWifi = ObserveWifi, PauseOnVpn = PauseOnVpn,
            UnknownNetwork = UnknownActionIndex == 1 ? UnknownNetworkAction.Direct : UnknownNetworkAction.KeepCurrent, UiScale = UiScale, Paused = true };
        await Task.Run(() =>
        {
            bool changedStartup = !IsDemo && oldStartup != StartupEnabled;
            if (changedStartup) StartupRegistration.Set(StartupEnabled);
            try { _runtime.Controller.SavePreferences(preferences); }
            catch
            {
                if (changedStartup) { try { StartupRegistration.Set(oldStartup); } catch { } }
                throw;
            }
        });
        AccessibilityScale.Apply(UiScale);
        Status = "設定を保存しました。自動切替は一時停止しています。内容を確認して再開してください。";
        await RefreshCoreAsync();
    }
    private bool Confirm(string title, string description, ProxySettings? before, ProxySettings? target, string button = "変更する(_A)") =>
        new ConfirmWindow(title, (IsDemo ? "【デモ】Windows は変更しません。\n\n" : "") + description, before, target, button) { Owner = _owner() }.ShowDialog() == true;
    private static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    public void ReportError(Exception e)
    {
        Status = "処理を完了できませんでした。" + Environment.NewLine + e.Message;
        MessageBox.Show(_owner(), Status, "Proxyz", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshRequested?.Invoke();
    }
}
