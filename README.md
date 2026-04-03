# proxyz

`proxyz` は Windows / macOS / Linux 向けの Rust 製プロキシ管理ツールです。CLI、バックグラウンドサービス、GUI の 3 層を同じ設定ストレージで共有し、手動切替と SSID 連動の自動切替を一貫して扱います。

## 実装計画

1. 共通ドメインモデル、永続化、OS アダプタ境界を定義する。
2. CLI からプロファイル管理と適用を実行できるようにする。
3. SSID 監視サービスと OS 別サービス制御を追加する。
4. GUI で同じ設定ストレージを編集・操作できるようにする。
5. テスト、ビルド手順、既知制約を整理する。

## ディレクトリ構成

```text
.
├─ src/
│  ├─ adapters/
│  │  ├─ network/
│  │  └─ proxy/
│  ├─ application/
│  ├─ domain/
│  ├─ gui/
│  ├─ service/
│  ├─ storage/
│  ├─ lib.rs
│  └─ main.rs
├─ Cargo.toml
└─ PROMPT.md
```

## 技術選定

- CLI: `clap`
- GUI: `eframe` / `egui`
- 永続化: OS 標準ユーザー設定ディレクトリ配下の `config.json` と `profiles.json`
- エラー: `anyhow` / `thiserror`
- ログ: `log` / `simplelog`

GUI は `eframe/egui` を採用しています。理由は、純 Rust でクロスプラットフォーム運用しやすく、CLI/サービスと依存関係を大きく分断せずに安定したネイティブアプリを構成できるためです。

設定ファイルは役割ごとに分離しています。

- `config.json`: UI 言語、ログレベル、未対応 SSID 時の動作などのアプリ設定
- `profiles.json`: プロキシプロファイルと SSID マッピング

この分離により、GUI の `Refresh` は主に `profiles.json` と OS の現在状態を読み直し、UI 設定の再注入による巻き戻りを起こしにくくしています。

## 主要機能

- `set`, `list`, `delete`, `save-current`
- プロキシプロファイル保存
- プロファイル適用時の任意コマンド逐次実行
- SSID マッピングと未対応 SSID 時の既定動作
- Windows はサービス起動、macOS/Linux はデーモン起動
- ダッシュボード、プロファイル編集、SSID マッピング、サービス制御、設定編集を持つ GUI
- Windows GUI から UAC を伴うサービス登録、登録解除、自動起動設定変更
- Windows では WinINet / WinHTTP / 環境変数のどこへ反映するかを選択可能

## CLI 使用例

```powershell
proxyz
proxyz save-current office --memo "current office proxy"
proxyz list
proxyz set office --yes
proxyz service start
proxyz service status
proxyz gui
```

Windows では `proxyz.exe` を引数なしで実行すると GUI が既定起動します。配布は `proxyz.exe` 単体を前提にしています。

## Windows プロキシ対象

Windows のプロキシ関連設定は 1 系統ではありません。`proxyz` は次の対象を選択して書き込めます。

- `WinINet`
  - 典型例: 設定アプリの「ネットワークとインターネット > プロキシ」
  - 代表的な利用先: Edge / Chrome / IE 系
- `Environment Variables`
  - 対象: `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`
  - 代表的な利用先: 環境変数を参照する CLI / ランタイム
- `WinHTTP`
  - 設定方法: `netsh winhttp`
  - 代表的な利用先: WinHTTP ベースのシステムコンポーネント

現状の「現在の OS プロキシ状態」表示は WinINet を基準に取得します。書き込み先は GUI の設定画面から選択します。

## 常駐メニュー案

Windows の通知領域へ常駐する場合のメニューは、現時点では次の構成を想定しています。

```text
Current Profile: campus
Current SSID: university-wifi
Service: Running
Open Dashboard
Refresh Now
Profiles
  - Direct
  - Campus
  - Lab
Service
  - Start Service
  - Stop Service
  - Register Service
  - Unregister Service
Apply Targets
  - WinINet
  - Environment Variables
  - WinHTTP
Launch at Sign-in
About proxyz
Quit
```

ZeroTier のように「状態確認」「即時切替」「常駐制御」を 1 段で触れる構成を意図しています。

### 任意コマンドの設定例

GUI ではコマンドを以下の形式で編集します。

- Program: `powershell`
- Arguments:
  - `-Command`
  - `git config --global http.proxy http://proxy.local:8080`

環境変数変更などシェル機能が必要なものは、必ず `powershell -Command ...` や `bash -lc ...` のように実行プログラムを明示してください。

## ビルド手順

Windows 11 では `scoop` を前提にしています。

```powershell
scoop install rustup gcc
$env:PATH = "$env:USERPROFILE\scoop\apps\rustup\current\.cargo\bin;$env:USERPROFILE\scoop\apps\gcc\current\bin;$env:PATH"
rustup toolchain install stable-x86_64-pc-windows-gnu
rustup default stable-x86_64-pc-windows-gnu
cargo build
```

macOS / Linux では標準の Rust ツールチェーンを導入したうえで `cargo build` を実行してください。

## テスト手順

```powershell
cargo test
cargo fmt --check
```

GUI の統合テスト方針:

- 主要フローは `AppService` を薄い UI 層から呼び出す構造にし、ロジックは単体テスト対象に寄せる。
- GUI 側は将来的に `egui` のイベント駆動 UI テスト、もしくは OS レベルの操作テストを追加する。
- 現状はプロファイル検証、設定検証、保存/読込、SSID 解決ロジックを優先的に自動化している。

## 追加機能

### `set --dry-run`

- 追加理由: 実際の OS 設定変更前に安全確認できるようにするため
- 期待効果: 誤適用の防止、CI やレビュー時の確認性向上
- 既存機能への影響: 通常の `set` 動作は変更なし
- 無効化方法: `--dry-run` を使わなければ従来どおり実適用される

## 既知の制約

- Windows のプロキシ適用はレジストリベースで、管理者ポリシーが強い環境では制限されることがあります。
- macOS / Linux の OS プロキシ操作は `networksetup` と `gsettings` 前提です。利用環境により別実装が必要になる場合があります。
- 任意コマンドのタイムアウト強制終了は未実装で、現状は終了コードと出力要約のログ記録を優先しています。
- Windows サービス登録は `sc.exe` を使用します。初回起動時に権限要件を満たす必要があります。

## 今後の拡張案

- 認証付きプロキシ情報の安全な保存
- Linux 向けに KDE / systemd-networkd など追加バックエンドを導入
- GUI の操作フロー自動テスト
- サービスのより詳細な監視状態表示
