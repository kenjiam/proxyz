using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Proxyz.App.Infrastructure;
using Proxyz.App.ViewModels;

namespace Proxyz.App;

public partial class App : Application
{
    private AppRuntime? _runtime;
    private SingleInstance? _instance;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private TrayHost? _tray;
    private DispatcherTimer? _debounce;
    private bool _stopping;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("予期しないエラーが発生したため、自動切替を停止して終了します。Windows 設定は自動では復元していません。\n\n" + args.Exception.Message, "Proxyz", MessageBoxButton.OK, MessageBoxImage.Error);
            _stopping = true; Shutdown(1);
        };
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Windows 10 または Windows 11 が必要です。");
            bool demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            if (e.Args.Any(x => x is not "--demo" and not "--background")) throw new ArgumentException("使用できる引数は --demo と --background です。");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo ? "Proxyz-Demo" : "Proxyz");
            _instance = SingleInstance.TryAcquire(directory, demo);
            if (_instance is null) { Shutdown(); return; }
            _runtime = new(directory, demo);
            _viewModel = new(_runtime, () => _window!, RequestExit);
            AccessibilityScale.Apply(_runtime.Controller.GetConfig().Preferences.UiScale);
            _window = new(_viewModel); MainWindow = _window;
            _tray = new(() => Ui(() => _window.ShowFromTray()),
                () => Ui(() => { _window.ShowFromTray(); if (_viewModel.ToggleAutomationCommand.CanExecute(null)) _viewModel.ToggleAutomationCommand.Execute(null); }),
                id => Ui(() => _viewModel.SelectAndApply(id)), () => Ui(RequestExit));
            _viewModel.StateChanged += UpdateTray;
            _viewModel.RefreshRequested += ScheduleRefresh;
            _runtime.Changed += ScheduleRefresh;
            _instance.OnActivate(() => Ui(() => _window.ShowFromTray()));
            _debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _debounce.Tick += async (_, _) =>
            {
                _debounce.Stop();
                if (!_stopping) await _viewModel.RefreshAsync();
            };
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            _runtime.StartMonitoring();
            if (!background || _runtime.Controller.RecoveryHold || _runtime.Controller.GetConfig().Profiles.Count == 0) _window.Show();
            UpdateTray();
            ScheduleRefresh();
        }
        catch (Exception error)
        {
            MessageBox.Show("Proxyz を安全に起動できませんでした。既存の設定ファイルを上書きしていません。\n\n" + error.Message +
                "\n\nデータフォルダー：%LOCALAPPDATA%\\Proxyz", "Proxyz の起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            _stopping = true; Shutdown(1);
        }
    }
    private void Ui(Action action)
    {
        if (_stopping || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_stopping) action(); }));
    }
    private void ScheduleRefresh() => Ui(() => { _debounce?.Stop(); _debounce?.Start(); });
    private void UpdateTray()
    {
        if (_viewModel is null) return;
        _tray?.Update(_viewModel.AutomaticState, _viewModel.Profiles, !_viewModel.AutomationRunning);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleRefresh();
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) ScheduleRefresh();
    }
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Ui(() =>
    {
        if (_runtime is not null) AccessibilityScale.Apply(_runtime.Controller.GetConfig().Preferences.UiScale);
    });
    public void RequestExit()
    {
        if (_viewModel?.IsBusy == true)
        {
            _window?.ShowFromTray();
            MessageBox.Show(_window!, "処理中は終了できません。操作結果を確認してから終了してください。", "Proxyz", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _window?.ShowFromTray();
        if (MessageBox.Show(_window!, "Proxyz を終了すると自動切替は止まります。現在の Windows プロキシ設定はそのまま残ります。\n元に戻す場合は、ホーム画面で復元してから終了してください。", "Proxyz を終了", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _stopping = true;
        if (_window is not null) _window.AllowClose = true;
        Shutdown();
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _stopping = true;
        if (_window is not null) _window.AllowClose = true;
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _stopping = true; _debounce?.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        _tray?.Dispose(); _runtime?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
}
