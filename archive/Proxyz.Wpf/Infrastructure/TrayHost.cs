using System.Drawing;
using Forms = System.Windows.Forms;
using Proxyz.Core.Models;

namespace Proxyz.App.Infrastructure;

public sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Action _show;
    private readonly Action _pause;
    private readonly Action<Guid> _apply;
    private readonly Action _exit;
    public TrayHost(Action show, Action pause, Action<Guid> apply, Action exit)
    {
        _show = show; _pause = pause; _apply = apply; _exit = exit;
        _menu.AccessibleName = "Proxyz メニュー";
        _menu.ShowImageMargin = false;
        _icon = new() { Icon = SystemIcons.Application, Text = "Proxyz — 準備中", Visible = true, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => _show();
    }
    public void Update(string state, IReadOnlyList<Profile> profiles, bool paused)
    {
        string tooltip = "Proxyz — " + state;
        _icon.Text = tooltip[..Math.Min(tooltip.Length, 63)];
        var oldItems = _menu.Items.Cast<Forms.ToolStripItem>().ToArray();
        _menu.Items.Clear(); foreach (var item in oldItems) item.Dispose();
        _menu.Items.Add(new Forms.ToolStripMenuItem("Proxyz を開く(&O)", null, (_, _) => _show()));
        _menu.Items.Add(new Forms.ToolStripMenuItem(state) { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem(paused ? "自動切替を再開する(&A)…" : "自動切替を一時停止(&A)", null, (_, _) => _pause()));
        var list = new Forms.ToolStripMenuItem("プロファイルを選ぶ(&P)");
        foreach (var profile in profiles)
        {
            Guid id = profile.Id;
            list.DropDownItems.Add(new Forms.ToolStripMenuItem(profile.Name.Replace("&", "&&"), null, (_, _) => { _show(); _apply(id); }));
        }
        list.Enabled = profiles.Count > 0; _menu.Items.Add(list);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("終了（Windows 設定は維持）(&X)…", null, (_, _) => _exit()));
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
