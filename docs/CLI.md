# CLI + トレイ操作仕様

2026-10-15の利用者指示でWPFメイン画面をCLIとトレイへ置換。現在の操作仕様はこの文書を優先する。判定・保存・回復の制約はSPEC.md / SECURITY.mdを維持する。

## コマンド

| コマンド | 動作 |
|---|---|
| status [--verbose] [--json] | 現在値・適用プロファイル・自動切換・理由・接続の概要。verboseはID/SSID・設定文字列・一致ルール。JSONはStatusInfo |
| up | 常駐がなければ確認待ちで起動。プレビュー後に再開し、現在のルールで一度判定。以後はイベントで判定 |
| down | 自動切換を永続的に一時停止。プロキシは維持 |
| switch <名前またはID> | 確認付き手動適用、自動切換を停止 |
| direct | 確認付き直接接続、自動切換を停止 |
| restore [--original] | 直前/初回へ確認付き復元。Pendingがあればその回復先を優先 |
| profile list / show <対象> | 保存済み設定を表示 |
| profile capture <名前> | 現在値を検証して新規プロファイルに保存。OS変更なし |
| profile add <名前> ... | 新規作成 |
| profile set <対象> ... | 指定項目だけ更新。省略項目は保持 |
| profile remove <対象> | 確認付き削除。ルールから参照中は拒否 |
| rule list / show <対象> | ルール表示 |
| rule add <名前> ... | 条件と適用先を登録 |
| rule set <対象> ... | 指定項目だけ更新 |
| rule enable / disable / remove <対象> | 有効化/無効化/確認付き削除 |
| config / config get <key> / config set <key> <value> | 全般設定の表示/取得/変更 |
| check [--url <URL>] | URLなしはWinINet/WinHTTPの設定読取りのみ。URL指定時は確認後にHEAD診断 |
| tray | 常駐を起動してCLIへ戻る。既存ホストは再利用 |
| quit | 確認付き常駐終了。Windows設定は維持 |
| version / --help | ユーザーデータを開かず表示 |

プロファイル・ルール・config保存と手動適用・復元は自動切換を一時停止する。保存だけでプロキシを変更しない。再開はup。

## プロファイル

```powershell
proxyz profile add "自宅" --direct
proxyz profile add "大学" --server "proxy.example.invalid:8080" --bypass "<local>;*.example.invalid"
proxyz profile add "会社" --pac "https://example.invalid/proxy.pac"
proxyz profile add "自動検出" --wpad true
proxyz profile set "会社" --note "組織指定値" --name "会社PAC"
```

--server / --pacは値を設定してその方式を有効にする。--directはフラグをDirectだけにするが残存文字列を消さない。組合せは --flags <1..15> または --direct-flag / --wpad / --manual / --script のtrue|falseで指定。HTTP/HTTPS別サーバーは既存のhttp=host:port;https=host:port表現を使う。無効方式の文字列も省略時は保持。認証情報は含めない。

## ルール

```powershell
proxyz rule add "大学LAN" --profile "大学" --current-network
proxyz rule add "大学WiFi" --profile "大学" --current-network --current-ssid
proxyz rule set "大学LAN" --kind lan
```

現在情報は接続が一意かつ確実な場合だけ利用。複数接続なら勝手に選ばず、status --verboseから --network-id <GUID> を指定する。SSIDは --ssid <UTF-8文字列> または --ssid-hex <生バイト16進数>。自動取得は識別を明示的に有効にしている場合だけ。IDとSSIDの指定はAND。--network-id none / --ssid-hex noneで条件を外す。--kind wifi|lan|other|any。無条件ルールは拒否。

## 全般設定

| key | value | 意味 |
|---|---|---|
| unknown-network | keep / direct | 既定keep。不明・競合・未接続ならDirect指定でも変更しない |
| pause-on-vpn | true / false | 既定true。VPN検出の限界は既存仕様のまま |
| observe-wifi | true / false | 既定false。変更時はSSID/位置情報の説明と確認 |
| startup | true / false | HKCU RunのProxyz値のみ。現在値と変更先を確認。固定した発行済みEXEで設定 |

旧configのUiScale等は互換性のため保持するが、現行CLIはWPF倍率を使わない。起動登録は「EXE絶対パス tray --host」。旧登録の --background も受付ける。--host / --idleは内部起動引数。

## 確認と終了コード

--dry-runはプレビューのみ。OS・保存・診断通信を変更/実行しない。適用・復元・再開、起動登録、Wi-Fi識別、削除、終了、URL通信には確認が必要。CLIは[y/N]、トレイは取消が既定。非対話は --yesがなければ取消。--yesでも管理制限・Pending・外部変更・確認後の状態比較は維持する。

stdoutは結果、stderrはエラー。status --jsonは単一JSON。設定/SSIDを含む出力は公開しない。

| 終了コード | 意味 |
|---|---|
| 0 | 成功 / dry-run完了 |
| 1 | 引数・入力不正 |
| 2 | 取消 / 非対話で確認なし |
| 3 | 外部変更・確認の陳腐化・管理制限・適用失敗（回復待ち以外） |
| 4 | 保存・IPC・起動・診断エラー |
| 5 | 回復待ち |
| 6 | 常駐が必要（共通処理側。通常CLIのupは自動起動） |

## トレイ

先頭はProxyz - OK / Paused / Warning / Error / Recovery。クリックで詳細。OKは設定管理上の状態で疎通成功ではない。Pausedは無効・手動停止・再開確認待ち。Warningは競合・外部変更・適用失敗抑止・管理制限。Errorは状態取得処理等の例外。RecoveryはPendingあり。

ネットワーク（Wi-Fi/LAN同時表示）、プロファイル（現在値一致にチェック、Direct）、自動切換チェック、元に戻す（直前/初回）、接続を確認、設定、About、終了を提供。自動切換が有効でも競合等で保留となるため、チェックと先頭状態は分ける。メイン設定画面はなく、確認・詳細・URL入力には標準WinFormsの小さなダイアログを使う。

## 診断

checkはWinINet現在ユーザー設定とWinHTTP静的既定を読むだけ。URL指定時はWinINet現在ユーザー、WinHTTP静的既定、WinHTTP自動構成の3方式でHEADを送りHTTPコード/Windowsエラーを表示する。設定は同期しない。

各ネイティブ段階のタイムアウトは5秒。PAC/WPAD等を含む総所要時間を5秒以内とは保証しない。認証/Cookie/リダイレクトの自動処理、証明書例外は使わない。200〜399を応答成功とし、3xxは転送未追従と表示。いずれかの読み取り/経路失敗、401/407等なら終了コード4。HEAD非対応でも失敗になり得る。他アプリの通信成功は保証しない。

既定で外部サイトへ接続しない。URL指定時は名前解決・プロキシ接続・OSのPAC/WPAD処理の付随通信が起き得る。第三者診断サービスへの設定送信、Proxyz独自のDirectフォールバックはない。

## デモと検証

--demoは別ホスト/別ロック/メモリーのみ。トレイなしの単発コマンド間では状態を引き継がない。scripts/test-cli-demo.ps1は専用デモを起動し複数CLI間の操作後に正常終了する。既定のCore/CLI/Windows実行形式テストとは別に実行。実行ポリシーを解除しない。手動トレイ、Windows10、実OS復元、実HTTP経路は別検証し [TEST_RESULTS.md](TEST_RESULTS.md) に記録する。
