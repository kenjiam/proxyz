using System.Windows;
using Proxyz.Core.Models;

namespace Proxyz.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string description, ProxySettings? current, ProxySettings? target, string confirm = "変更する(_A)")
    {
        InitializeComponent();
        Heading.Text = title; Description.Text = description; ConfirmButton.Content = confirm;
        CurrentBox.Text = Describe(current); TargetBox.Text = Describe(target);
        Loaded += (_, _) => CancelButton.Focus(); // Enter must not silently accept a proxy change.
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    public static string Describe(ProxySettings? settings) => settings is null ? "記録なし" :
        settings.Summary + "\n" +
        (settings.Manual ? "サーバー: " + settings.ProxyServer + "\n除外: " + settings.ProxyBypass + "\n" : "") +
        (settings.Script ? "PAC URL: " + settings.AutoConfigUrl + "\n" : "") +
        "自動検出: " + (settings.AutoDetect ? "有効" : "無効") + "\n" +
        "直接接続フラグ: " + (settings.AllowDirect ? "有効" : "無効");
}
