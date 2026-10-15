# Codex 引き継ぎ — CLI + トレイ

2026-10-15の利用者承認でWPFメイン画面をCLI＋トレイへ移行。現在の操作は [CLI.md](CLI.md)、安全要件はAGENTS.md / SECURITY.md、結果と未確認事項は [TEST_RESULTS.md](TEST_RESULTS.md)。旧GUIはarchive/Proxyz.Wpfに残し、現行ソリューションに戻さない。

## 検証順序

通常ユーザーのWindows / .NET 10 SDKを使う。Windows10 22H2 x64とWindows11 x64は別の互換性ターゲット。

```powershell
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet restore .\Proxyz.slnx
dotnet build .\Proxyz.slnx -c Release --no-restore
dotnet run --project .\tests\Proxyz.Core.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Cli.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Windows.Tests -c Release --no-build
dotnet run --project .\src\Proxyz.App -c Release --no-build -- --demo tray
```

テストは実行形式ハーネスで、dotnet testは代替にならない。最初の画面確認は必ずdemo。scripts/test-cli-demo.ps1は別のデモ統合検証。実行ポリシーによりスクリプトが禁止されたら制限を解除せず、個別コマンドを使う。

## 未確認項目

Windows10、手動トレイ操作、新しいダイアログのキーボード/Narrator/DPI/コントラスト、実OSのwrite/readback/rollback、外部同時変更、Wi-Fi拒否、VPN、復帰イベント、起動登録、実HTTP診断、発行先からの起動を別々に記録する。テスト数、署名、メモリー量を推測しない。

実設定・起動登録・位置情報許可・ネットワーク変更は対象と戻し先を提示し許可後のみ。使い捨てVM/アカウント、元設定のバックアップ、アプリ内確認を用いる。WinHTTPは読取り/明示診断だけで管理対象へ拡張しない。PAC/SSID/組織設定/ログは外部送信しない。サービス・昇格・証明書/ポリシー変更で解決しない。

## 発行

受け入れ後にscripts/publish.ps1で発行し、固定のEXE場所で再確認。WPFアーカイブは発行対象に含めない。アクティブなアプリと旧GUIを同じ実データで同時実行しない。commit/push/公開は別途承認が必要。
