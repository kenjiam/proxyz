using System.Windows;
using System.Windows.Controls;

namespace Proxyz.App.Views;

public sealed class GuideWindow : Window
{
    public GuideWindow()
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "使い方 — Proxyz"; Width = 730; Height = 660; MinWidth = 400; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(22) };
        var close = new Button { Content = "閉じる(_C)", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var text = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "Proxyz の使い方\n\n" +
                "1. まず、現在の Windows 設定を『現在の設定を保存』で取り込みます。保存だけでは Windows は変わりません。\n\n" +
                "2. 大学・自宅などのプロファイルを作ります。通常は『直接接続』『自動検出』『PAC』『手動プロキシ』から選びます。\n\n" +
                "3. 『自動切替ルール』で接続中のネットワークとプロファイルを結び付けます。SSID を使う場合は、設定で Wi-Fi 識別を有効にし、Windows の許可を確認します。\n\n" +
                "4. ホームの『自動切替を再開』で内容を確認して開始します。手動切替・復元後は一時停止します。必要なときに再開してください。\n\n" +
                "安全な動作\n" +
                "・最初は自動切替が無効です。未登録ネットワークでは設定を維持します。\n" +
                "・接続の競合、外部からの設定変更、未完了の適用があると、自動上書きを止めます。\n" +
                "・変更直前と初回変更前の設定を端末内に記録します。Windows 設定を元に戻してからアンインストールすることもできます。\n" +
                "・ウィンドウの × はトレイに隠す操作です。アプリを終了しても、Windows の設定は自動では戻りません。\n\n" +
                "対応範囲\n" +
                "Windows ユーザーの通常のプロキシ設定を管理します。WinHTTP、VPN 専用設定、Git・npm、DNS、証明書、ファイアウォールは変更しません。\n" +
                "ブラウザー独自のポリシーやアプリ固有の設定が優先される場合があります。『適用済み』は通信成功の保証ではありません。\n\n" +
                "キーボード\n" +
                "Tab / Shift+Tab：項目間を移動\nAlt：メニュー・アクセスキー\nCtrl+1～4：各タブ\nCtrl+M：手動切替の選択へ\nF5：状態を更新\nEsc：ダイアログをキャンセル\n" +
                "すべての操作は通常の画面からも行えます。トレイ操作は必須ではありません。\n\n" +
                "データとプライバシー\n" +
                "設定は %LOCALAPPDATA%\\Proxyz に保存します。同期・テレメトリー・プロキシサーバーの探索はありません。プロファイルや復元記録には組織内のアドレスが含まれます。共有する前に確認してください。\n\n" +
                "この版は Windows 実機検証前のソース実装です。Windows 10/11、読み上げ、スリープ復帰、実際のプロキシ環境での検証は別途必要です。"
        };
        System.Windows.Automation.AutomationProperties.SetName(text, "Proxyz の使い方");
        panel.Children.Add(text); Content = panel;
        Loaded += (_, _) => text.Focus();
    }
}
