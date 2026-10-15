# 設計と実装の対応

2026-10-15：CLI＋トレイへ移行。旧WPFソースは [archive/Proxyz.Wpf](../archive/Proxyz.Wpf) に保存し、現行ソリューションから除外した。Core/Windowsは共有するソースアーカイブで、凍結済みバイナリーではない。

## 構成

```text
Proxyz.exe CLI ── current-user / current-session named pipe ── Proxyz.exe tray
     │（常駐なしでは同じファイルロックを取得して単発処理）          │
     └──────────────────── CommandService ──────────────────────┘
                                 │
                         ProxyController
                 RuleEngine / Validation / JsonStateStore
                                 │
                         WinInetProxyBackend

tray: WinForms NotifyIcon + 最小限の確認/詳細ダイアログ
      NLM / WLAN / registry / power / session events → 1.5秒デバウンス
```

全処理は通常ユーザー権限。サービス・管理者プロセス・外部サーバーなし。現行アプリにWPF参照なし。WinForms Desktop Runtimeは必要で、メモリー使用量の削減幅は未測定。第三者NuGetパッケージは追加していない。

## プロジェクト

- Proxyz.Core：既存の設定、ルール、保存、適用/復元、排他、回復。
- Proxyz.Cli：OS非依存の引数解析、共通操作、プレビュー、状態表示、改行フレーム。Coreのみ参照。
- Proxyz.Windows：WinINet、NLM、WLAN、起動登録と、WinINet/WinHTTP読取り・明示診断。
- Proxyz.App：CLI起点、限定したローカル通信、トレイ、実/デモRuntime。WPFなし。
- Core.Tests / Cli.Tests / Windows.Tests：実行形式ハーネス。既定では実プロキシ・起動登録・通信を変更/実行しない。

## プロセスと所有権

常駐があればCLIはそのホストを利用し、新しいControllerやストアを開かない。なければ単発CLIも同じinstance.lockを保持し、その間だけデータを所有する。別セッションや旧GUIがロックを持つ場合は処理を拒否する。upは必要なら同じEXEのトレイを確認待ちで起動し、確認前に自動適用しない。trayは別プロセスを起動してCLIへ戻る。標準出力を引き継がず、パイプ利用時にもCLI終了を妨げない。

通信はユーザーSIDのハッシュとセッションIDを含む名前付きパイプで、サーバーとクライアントの両方にCurrentUserOnlyを指定。demoは別名。汎用RPC・shell・任意コマンド実行ではなく、許可したCLI動詞のみ。要求/応答はJSON行、1MiB上限、引数数/長さ制限、3分の確認待ち上限。切断・取消・不完全な行では準備済み操作を実行しない。CRLFとLFを受け付ける。同一ユーザーによる悪意ある操作を防ぐ暗号境界とはみなさない。

## 確認と直列化

CommandServiceはCLI・トレイ・イベント判定を直列化する。Prepareは変更前後と実行処理を持つ使い捨てPlanを作るだけ。確認後にconfig/journalのスナップショットを再比較し、upや現在ネットワークを用いたルール登録は接続のfingerprintも比較する。OS適用はControllerのexpectedBefore照合を維持。トレイの状態取得・ネイティブ処理はworkerで行い、WinForms表示とCOM購読の作成/破棄はUIスレッドで行う。

## データと復元

%LOCALAPPDATA%/Proxyzの既存config.json、journal.json、.bak、限定audit、instance.lockを使用。demoはProxyz-Demoのロックとメモリーのみ。schema1と旧設定項目は保持。無効方式の残存文字列も保持する。未知/破損データを初期化しない。

書込みは次の順序を維持する。

1. 現在値、ポリシー、所有確認。
2. Before/IntendedのPendingを同一ディレクトリの一時ファイルへ書きFlush(true)、Replace/Moveで永続化。
3. 現在値と制限を再確認。
4. WinINet APIで現在ユーザーの標準LANに適用、通知、読戻し。
5. 一致時にPrevious/LastAppliedを保存してPendingを除去。

失敗時はBefore/Intendedからなる自分の部分書込みと保守的に判断できる範囲でのみ戻す。外部値や制限があればPendingを保持。古いPendingの回復中はRecoveryTargetに元の復元先を保持し、UIプレビューとRestoreで優先する。旧記録はPending.Beforeを使う。RecoveryTargetのない記録には追加フィールドを出さない。これを含む記録を旧ソース版が未知フィールドとして拒否する場合も、削除して起動させない。

手動適用/復元/保存は自動切換を永続的に一時停止。OS書込みは常にController経由。外部変更は自動上書きしない。同一接続での自動失敗のラッチを維持する。終了やdownでDirectにはしない。

## 診断と起動登録

ConnectionDiagnosticsはWinINet現在値とWinHTTP静的既定の読取り、および明示URLへのネイティブHEADに限定。WinHTTPの設定書込みAPIはない。返されたWinHTTP文字列はGlobalFree、native HTTPハンドルはSafeHandleで解放。診断は設定適用や通信経路の同期ではなく、プロキシ認証や証明書例外を設定しない。

起動登録は確認後にHKCU RunのProxyz値のみ更新。先にストアへの保存とpauseを行う。現在の登録文字列と変更先をプレビューし、確認後の登録変更も再比較する。登録失敗時はpauseを維持し、成功とは表示しない。

OS設定とファイル間の原子的トランザクションは不可能。最終比較とnative呼出しの間の外部変更、全VPN検出、全ポリシー先行検出、全アプリへの反映、PAC解決・認証の成功は保証しない。
