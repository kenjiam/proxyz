# Proxyz

Windows 10 / 11 の現在ユーザーのプロキシ設定を、CLI とタスクトレイから管理するアプリです。C# / .NET 10、通常ユーザー権限で動作します。

プロファイル・ルール・適用ジャーナル・復元処理は維持し、WPF の設定画面を CLI と小さなトレイ操作へ置き換えました。旧 GUI は [archive/Proxyz.Wpf](archive/Proxyz.Wpf) に保存し、現行ソリューションから除外しています。

## 起動

.NET 10 SDK / Windows Desktop Runtime がある Windows でビルドします。最初は必ずデモを使います。

```powershell
dotnet restore .\Proxyz.slnx
dotnet build .\Proxyz.slnx -c Release --no-restore
dotnet run --project .\tests\Proxyz.Core.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Cli.Tests -c Release --no-build
dotnet run --project .\tests\Proxyz.Windows.Tests -c Release --no-build

# デモのトレイを起動。CLI に戻ります。
dotnet run --project .\src\Proxyz.App -c Release --no-build -- --demo tray
# 別の CLI プロセスから同じデモを操作できます。
dotnet run --project .\src\Proxyz.App -c Release --no-build -- --demo status
```

以下の `proxyz` は、ビルド / 発行された `Proxyz.exe` の呼出しです。開発中は上記 `dotnet run ... --` の後へ引数を渡せます。Scoop 等の独自ランタイム配置では、apphost を使う場合に DOTNET_ROOT の設定が必要なことがあります。

```powershell
proxyz --help
proxyz --demo up
proxyz --demo status --verbose
proxyz --demo down
proxyz --demo quit
```

デモの設定は常駐プロセスのメモリー内だけに保持し、終了時に破棄します。トレイが動いていないデモの単発コマンド間では状態は引き継がれません。デモは実プロキシ、起動登録、WLAN、外部通信を使用しません。

## 普段の操作

```powershell
proxyz profile capture "大学"
proxyz profile add "自宅" --direct
proxyz rule add "大学LAN" --current-network --profile "大学"
proxyz up
proxyz status
proxyz switch "自宅"
proxyz restore
proxyz down
```

- `up` は必要ならトレイを起動し、確認して自動切換を再開します。
- `down` は自動切換を停止し、現在の Windows 設定を維持します。
- 手動切替、復元、プロファイル / ルール / 全般設定の保存後は自動切換が一時停止します。
- `status --verbose` にネットワーク・一致ルール・現在値をまとめています。独立した network コマンドはありません。
- 全般設定は `config` / `config get <key>` / `config set <key> <value>` です。
- 適用・復元・再開は変更前後を表示し、既定は取消。非対話では `--yes` が必要です。`--dry-run` は変更せずプレビューします。
- トレイはネットワーク、プロファイル、自動切換チェック、復元、接続確認、設定、About、終了を提供します。OK は通信成功を意味しません。

全コマンド・終了コード・トレイ仕様は [CLI.md](docs/CLI.md) を参照してください。

## 変更対象と診断

変更するのは現在ユーザーの標準 LAN WinINet 設定です。WinHTTP・VPN専用設定・DNS・証明書・ファイアウォールを同期しません。サービス、管理者権限、クラウド、テレメトリー、第三者 NuGet 依存はありません。

`proxyz check` は WinINet と WinHTTP の設定の読み取りだけです。`check --url <URL>` は接続先を確認した後、WinINet、WinHTTP静的既定、WinHTTP自動構成の3方式で HEAD を試します。リダイレクト・認証・Cookieの自動処理や証明書例外を有効にせず、失敗時にProxyzがDirectへ切り替えることもありません。PAC/WPAD解決はOSに委ねるため付随通信が起きる場合があります。

Wi-Fi識別とログイン時起動はオプトインです。自動起動登録は固定した Proxyz.exe の場所で明示確認後に行います。実 OS の設定変更を試す場合は、対象・戻し先・バックアップを確認してください。

## 保存と互換性

`%LOCALAPPDATA%\Proxyz` の既存 config.json / journal.json を継続使用します。読めないファイルを初期化しません。デモは別の Proxyz-Demo ディレクトリをロックにだけ使用します。設定・SSID・PAC・復元記録は端末内の平文であり、公開しないでください。

Windows 10 22H2 x64 と Windows 11 x64 が互換性確認対象です。Windows 11だけからWindows 10の成功を推測しません。実行互換性と Microsoft の OS / .NET サポート判断は別です。[公式資料](docs/SOURCES.md#s01-runtime)

ビルド / 偽テスト / デモの成功は実OSの適用・復元、全トレイ操作、Windows 10での動作の証明ではありません。今回と過去の結果・未確認事項は [TEST_RESULTS.md](docs/TEST_RESULTS.md) に記録します。生成時の履歴は [VERIFICATION.md](VERIFICATION.md) のまま保持しています。

発行手順は `scripts/publish.ps1` にあります。自己完結型でもトリミング / AOT は使いません。未検証の配布物を完成品とは扱いません。

[仕様](docs/SPEC.md)・[設計](docs/ARCHITECTURE.md)・[安全性](docs/SECURITY.md)・[引き継ぎ](docs/CODEX_HANDOFF.md)
