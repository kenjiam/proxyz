using System.Drawing;
using Microsoft.Win32;
using Proxyz.App.Infrastructure;
using Proxyz.Cli;
using Proxyz.Core.Models;
using Forms = System.Windows.Forms;

namespace Proxyz.App;

internal sealed class TrayContext : Forms.ApplicationContext
{
    private readonly AppRuntime _runtime;
    private readonly CommandService _commands;
    private readonly RuntimePlatform _platform;
    private readonly Forms.Control _dispatch = new();
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new() { ShowImageMargin = false, AccessibleName = "Proxyz" };
    private readonly Forms.Timer _debounce = new() { Interval = 1500 };
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private StatusInfo? _status;
    private bool _busy, _idle, _quitting, _disposed;
    public TrayContext(string directory, bool demo, bool idle)
    {
        _idle = idle;
        _ = _dispatch.Handle;
        _runtime = new(directory, demo);
        _platform = new(_runtime, true, () => _quitting = true, () => _idle);
        _commands = new(_runtime.Controller, _platform);
        _icon = new() { Icon = SystemIcons.Application, Text = demo ? "Proxyz - DEMO" : "Proxyz - Loading", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => Details();
        _menu.Opening += (_, e) => { if (_busy) e.Cancel = true; };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshAsync(); };
        _runtime.Changed += Schedule;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        _runtime.StartMonitoring();
        _server = Task.Run(() => LocalProtocol.Serve(demo, _commands.Prepare, () => Post(() =>
        {
            if (_quitting) { ExitThread(); return; }
            var prefs = _runtime.Controller.GetConfig().Preferences;
            if (prefs.AutomationEnabled && !prefs.Paused) _idle = false;
            Schedule();
        }), _stop.Token));
        Schedule();
    }
    private void Post(Action action)
    {
        if (!_disposed && !_dispatch.IsDisposed)
            try { _dispatch.BeginInvoke(new Action(() => { if (!_disposed) action(); })); } catch (InvalidOperationException) { }
    }
    private void Schedule() => Post(() => { _debounce.Stop(); _debounce.Start(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Schedule(); }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) Schedule();
    }
    private async Task RefreshAsync()
    {
        if (_busy) { Schedule(); return; }
        _busy = true;
        try
        {
            _status = await Task.Run(() => { if (!_idle) _commands.Reconcile(); return _commands.Status(); });
            BuildMenu();
        }
        catch (Exception e)
        {
            _icon.Icon = SystemIcons.Error; _icon.Text = "Proxyz - Error";
            ShowText("Proxyz - Error", e.Message);
        }
        finally { _busy = false; }
    }
    private static string Escape(string value) => value.Replace("&", "&&");
    private Forms.ToolStripMenuItem Item(string label, Action action) => new(label, null, (_, _) => action());
    private Forms.ToolStripMenuItem Command(string label, params string[] args) => Item(label, () => _ = RunAsync(args));
    private void BuildMenu()
    {
        if (_status is null) return;
        var old = _menu.Items.Cast<Forms.ToolStripItem>().ToArray(); _menu.Items.Clear(); foreach (var item in old) item.Dispose();
        string heading = $"Proxyz - {_status.State}" + (_runtime.Demo ? " [DEMO]" : "");
        _icon.Text = heading; _icon.Icon = _status.State switch { "Error" or "Recovery" => SystemIcons.Error, "Warning" => SystemIcons.Warning, _ => SystemIcons.Application };
        _menu.Items.Add(Item(heading, Details)); _menu.Items.Add(new Forms.ToolStripSeparator());
        var network = new Forms.ToolStripMenuItem("ネットワーク");
        foreach (var connection in _status.Network.Networks) network.DropDownItems.Add(Item(Escape(connection.Display), () => ShowText("ネットワーク", System.Text.Json.JsonSerializer.Serialize(connection, CommandService.Json))));
        if (network.DropDownItems.Count == 0) network.DropDownItems.Add(new Forms.ToolStripMenuItem("未接続 / 情報なし") { Enabled = false });
        _menu.Items.Add(network);
        var profiles = new Forms.ToolStripMenuItem("プロファイル");
        foreach (var profile in _runtime.Controller.GetConfig().Profiles)
        {
            var item = Command(Escape(profile.Name) + "…", "switch", profile.Id.ToString()); item.Checked = profile.Settings.SameAs(_status.Current); profiles.DropDownItems.Add(item);
        }
        profiles.DropDownItems.Add(new Forms.ToolStripSeparator()); profiles.DropDownItems.Add(Command("直接接続…", "direct")); _menu.Items.Add(profiles);
        var automatic = Command("自動切換", _status.AutomationEnabled ? "down" : "up"); automatic.Checked = _status.AutomationEnabled; _menu.Items.Add(automatic);
        var journal = _runtime.Controller.GetJournal(); var restore = new Forms.ToolStripMenuItem("元に戻す");
        var previous = Command("直前の設定…", "restore"); previous.Enabled = journal.Pending is not null || journal.Previous is not null;
        var original = Command("初回変更前…", "restore", "--original"); original.Enabled = journal.Pending is not null || journal.Original is not null;
        restore.DropDownItems.Add(previous); restore.DropDownItems.Add(original); _menu.Items.Add(restore);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("接続を確認…", () =>
        {
            using var prompt = new Forms.Form { Text = "接続を確認", Width = 650, Height = 230, StartPosition = Forms.FormStartPosition.CenterScreen };
            var box = new Forms.TextBox { Dock = Forms.DockStyle.Top, AccessibleName = "テスト先 HTTP / HTTPS URL（空欄なら設定の確認だけ）" };
            var label = new Forms.Label { Text = "URL を空欄にすると設定の確認のみ。URL 指定時は確認後に通信します。", Dock = Forms.DockStyle.Top, Height = 55 };
            var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 50, FlowDirection = Forms.FlowDirection.RightToLeft };
            var cancel = new Forms.Button { Text = "取消", DialogResult = Forms.DialogResult.Cancel }; var check = new Forms.Button { Text = "確認", DialogResult = Forms.DialogResult.OK };
            buttons.Controls.Add(cancel); buttons.Controls.Add(check); prompt.Controls.Add(box); prompt.Controls.Add(label); prompt.Controls.Add(buttons); prompt.CancelButton = cancel; prompt.AcceptButton = check;
            if (prompt.ShowDialog() == Forms.DialogResult.OK) _ = RunAsync(string.IsNullOrWhiteSpace(box.Text) ? ["check"] : ["check", "--url", box.Text]);
        }));
        var settings = new Forms.ToolStripMenuItem("設定"); var prefs = _runtime.Controller.GetConfig().Preferences;
        var wifi = Command("Wi-Fi 識別", "config", "set", "observe-wifi", (!prefs.ObserveWifi).ToString().ToLowerInvariant()); wifi.Checked = prefs.ObserveWifi;
        var vpn = Command("VPN 検出時は保留", "config", "set", "pause-on-vpn", (!prefs.PauseOnVpn).ToString().ToLowerInvariant()); vpn.Checked = prefs.PauseOnVpn;
        settings.DropDownItems.Add(wifi); settings.DropDownItems.Add(vpn);
        var unknown = new Forms.ToolStripMenuItem("未登録ネットワーク");
        foreach (var (value, labelText) in new[] { ("keep", "現在の設定を維持"), ("direct", "直接接続") })
        { var item = Command(labelText, "config", "set", "unknown-network", value); item.Checked = (value == "direct") == (prefs.UnknownNetwork == UnknownNetworkAction.Direct); unknown.DropDownItems.Add(item); }
        settings.DropDownItems.Add(unknown);
        var startup = Command("ログイン時に起動", "config", "set", "startup", (!_platform.StartupEnabled).ToString().ToLowerInvariant()); startup.Checked = _platform.StartupEnabled; settings.DropDownItems.Add(startup);
        settings.DropDownItems.Add(Item("CLI の使い方", () => ShowText("Proxyz CLI", CommandService.Help))); _menu.Items.Add(settings);
        _menu.Items.Add(Item("About…", () => ShowText("About Proxyz", "Proxyz 0.1.0\nCLI + tray / current-user WinINet\nWindows 10 / 11\n" + (File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE")) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LICENSE")) : "License: MIT — see the source LICENSE file."))));
        _menu.Items.Add(new Forms.ToolStripSeparator()); _menu.Items.Add(Command("終了（現在の設定を維持）…", "quit"));
    }
    private async Task RunAsync(string[] args)
    {
        if (_busy) return; _busy = true;
        try
        {
            using var plan = await Task.Run(() => _commands.Prepare(args));
            if (plan.RequiresConfirmation && !Confirm(plan.Preview)) return;
            var result = await Task.Run(plan.Execute);
            if (_quitting) { ExitThread(); return; }
            if (args[0] == "up") _idle = false;
            ShowText(result.ExitCode == 0 ? "Proxyz" : "Proxyz - Warning", result.Text);
        }
        catch (Exception e) { ShowText("Proxyz - Error", e.Message); }
        finally { _busy = false; if (!_disposed) Schedule(); }
    }
    private void Details() { if (_status is not null) ShowText($"Proxyz - {_status.State}", _status.Reason + "\n" + ProxyDescription.Format(_status.Current) + "\n" + _status.PolicyNotice + "\n" + _status.Network.Notice + "\n" + _runtime.MonitorNotice + "\nOK は設定管理上の状態です。通信成功を意味しません。"); }
    private static bool Confirm(string text)
    {
        using var form = TextForm("Proxyz — 変更内容の確認", text);
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 50, FlowDirection = Forms.FlowDirection.RightToLeft };
        var cancel = new Forms.Button { Text = "取消", DialogResult = Forms.DialogResult.Cancel, AccessibleName = "取消" };
        panel.Controls.Add(cancel); panel.Controls.Add(new Forms.Button { Text = "実行", DialogResult = Forms.DialogResult.OK, AccessibleName = "実行" }); form.Controls.Add(panel);
        form.AcceptButton = cancel; form.CancelButton = cancel; form.Shown += (_, _) => cancel.Focus();
        return form.ShowDialog() == Forms.DialogResult.OK;
    }
    private static Forms.Form TextForm(string title, string text)
    {
        var form = new Forms.Form { Text = title, Width = 720, Height = 560, MinimumSize = new Size(430, 300), StartPosition = Forms.FormStartPosition.CenterScreen, AutoScaleMode = Forms.AutoScaleMode.Dpi };
        form.Controls.Add(new Forms.TextBox { Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Both, WordWrap = false, Dock = Forms.DockStyle.Fill, Text = text.Replace("\n", Environment.NewLine), AccessibleName = title, BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText });
        return form;
    }
    private static void ShowText(string title, string text)
    {
        using var form = TextForm(title, text); var close = new Forms.Button { Text = "閉じる", Dock = Forms.DockStyle.Bottom, Height = 40, DialogResult = Forms.DialogResult.Cancel }; form.Controls.Add(close); form.CancelButton = close; form.ShowDialog();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _stop.Cancel(); _debounce.Stop();
            SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged; _runtime.Changed -= Schedule;
            _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _debounce.Dispose();
            try { _server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _runtime.Dispose(); _dispatch.Dispose(); _stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
