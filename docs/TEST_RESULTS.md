# 第4段階の結果記録

最新：2026-10-15のCLI＋トレイ移行結果は末尾の「CLI＋トレイへのリファクタリング」を参照。以下の2026-10-14は旧WPF GUIでの履歴。旧GUIと補助スクリプトはarchive/Proxyz.Wpfへ移動した。

2026-10-14（JST）。**部分実施。第4段階の受け入れ・リリース判定は未完了。** 生成時の VERIFICATION.md、SOURCE_CHECKS.json、SOURCE_MANIFEST.sha256 は更新していない。旧マニフェストは今回修正したファイルには一致しない。

## 環境

- Windows 11 Pro 25H2 x64、ビルド26200.9711。RuntimeInformationは Microsoft Windows 10.0.26200、レジストリのProductNameはWindows 10 Proと報告するが、DisplayVersion=25H2、CurrentBuild=26200、UBR=9711を併記し、Windows 10の検証とは扱わない。
- SDK 10.0.402、MSBuild 18.9.15、Host / NETCore.App / WindowsDesktop.App 10.0.13、win-x64。
- 利用者が導入した Scoop のSDKを使用。このエージェントのPATHでは再起動後もdotnetを認識せず、フルパスで実行。
- PowerShell、対話ユーザー。管理者起動、SYSTEM、サービスは使用していない。サンドボックス外のrestore / GUI / UIAはツールの許可経由。これは管理者昇格を意味しない。
- 実プロキシ、自動起動、ポリシー、Wi-Fi/位置情報許可、ネットワーク変更の許可は未取得・未実施。
- 表示倍率、OS文字倍率、コントラスト、Narratorバージョンは未測定。
- Gitリポジトリではない。コミット、push、公開なし。

## 再現手順

作業ルートで、Windowsの通常ユーザーのPowerShellから実行する。今回のコマンドは以下のフルパス呼出しと環境変数を用いた。

```powershell
$proxyzDotnet = "$env:USERPROFILE\scoop\apps\dotnet-sdk\current\dotnet.exe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet-home'
$env:DOTNET_ROOT = Split-Path $proxyzDotnet
& $proxyzDotnet --info
& $proxyzDotnet restore .\Proxyz.slnx
& $proxyzDotnet build .\Proxyz.slnx -c Release --no-restore -t:Rebuild
& $proxyzDotnet run --project .\tests\Proxyz.Core.Tests -c Release --no-build
& $proxyzDotnet run --project .\tests\Proxyz.Windows.Tests -c Release --no-build
& $proxyzDotnet run --project .\src\Proxyz.App -c Release --no-build -- --demo
```

デモ起動中に別の通常ユーザーPowerShellで `scripts/demo-uia.ps1 -ExerciseRecovery` を実行できる。正確なデモのウィンドウ名とプロセスIDに限定した補助検証で、既定テストには含めない。デモを同時に操作すると焦点検証が失敗し得る。終了は利用者がメニューから行う。実行ポリシーを緩めない。

読み取り前に、現在ユーザーの標準LAN WinINet設定、変更制限ポリシー、NLM接続情報を読み、内部アドレスや名前を出力せず、WLAN識別・書込みは行わない旨を利用者へ説明した。

```powershell
& $proxyzDotnet run --project .\tests\Proxyz.Windows.Tests -c Release --no-build -- --read-os
```

## 実行した検証

すべて上記Windows 11 / SDK環境。終了コードを取得できなかった操作に成功コードを推測して付けない。

| コマンド | 終了コード | 観測結果と範囲 |
|---|---|---|
| PATH指定のdotnet --info / restore | 1 | dotnet未認識。フルパスに切替 |
| フルパス --info | 0 | SDKとDesktop Runtimeを確認 |
| 初回フルパス restore | 1 | サンドボックスでユーザーNuGet設定へのアクセス拒否 |
| 許可後のサンドボックス外 restore | 0 | 5プロジェクトを復元 |
| 初回Release build --no-restore | 0 | C# / WPF XAMLビルド成功、警告3、エラー0 |
| 初回Core.Tests --no-build | 0 | 111/111 passed; 0 failed. |
| 初回Windows.Tests --no-build | 0 | PASS native structure layout checks。ネイティブ読み取りなし |
| 修正中のRelease build | 1 | デモ実行中のDLLロックでMSB3027 / MSB3021。強制終了せず、デモ終了後のビルドで解消 |
| 競合修正後Core.Tests | 0 | 113/113 passed; 0 failed. |
| 最終Release build --no-restore -t:Rebuild | 0 | 全5プロジェクト、警告0、エラー0。全再ビルド |
| 最終Core.Tests --no-build | 0 | 117/117 passed; 0 failed. 偽バックエンド/ストアと一時ファイル |
| 最終Windows.Tests --no-build | 0 | x64 WinINet / WLAN構造体配置成功。ケース総数はハーネスが報告しない |
| Windows.Tests --read-os | 0 | WinINet / ポリシー / NLM読み取り成功、3接続列挙。終了前の再読取りとの一致成功。設定本文や名前は掲載しない |
| App --demo 初回 / 修正後 | 0 / 0 | UIAでWPF画面起動を観測。終了後に両起動コマンドの終了コード0を取得。最初の画面確認はデモのみ |
| 開発中demo-uia.ps1 -ExerciseRecovery -ExitDemo | 1 | 下記デモ確認は成功。最後の終了確認のMessageBoxがUIA Invokeを提供せず失敗。スクリプト全体成功とは扱わない |
| 最終demo-uia.ps1 PowerShell Parser | 0 | 構文確認。終了自動化を除去した最終ファイルのGUI全体再実行は未実施 |
| python scripts/verify-source.py 初回 | 1 | ルートCODEX_HANDOFF.mdの相対リンクが2箇所不正 |
| python scripts/verify-source.py リンク修正後 | 0 | 7分類の構造確認。C#実行、ネイティブ、UIの検証の代替にしない |
| publish / 発行先から起動 | — | 未実施。下記リリース阻害項目が残る |

## 修正と回帰テスト

1. 確認時の値との比較とApplyのBefore読取りが別で、その間の外部変更を未確認のまま採用し得た。Applyの同じ読取り値で照合し、その後の適用直前再比較も維持。手動適用と復元に、2回目の読取りで外部変更を注入して書込みなし・外部値維持を確認する各1件の回帰テストを追加。
2. 古いPendingを回復中、新しいPending.Beforeが回復試行直前の値に置き換わり、失敗時に元の復元先を失い得た。任意のRecoveryTargetに元の復元先を保持し、UIプレビューとRestoreで優先。回復・ロールバック両方の失敗後の再起動、書込み時点の耐久記録、JSON往復、旧記録と不要フィールド省略の4件を追加。RecoveryTargetのない旧記録は読める。このフィールドのある記録は旧ソース版で安全に拒否される点をARCHITECTURE.mdに記載。
3. NLM接続ポイントがnullなら既存の購読失敗通知・解放経路へ進める。テストのPending参照はnullチェックを明示。WinFormsはトレイ専用のためWFO0003のみ限定抑制し、WPFのPerMonitorV2 / Windows 10マニフェストを維持。DPI実機合格を意味しない。
4. common.ps1でCLIの開発証明書自動生成を無効化。ローカルCLIホームをgitignore・構造確認の対象外へ。デモ専用UIA補助検証を追加し、引継ぎリンクを修正、READMEに結果へのリンクを追加。

新規Core回帰テストは計6件。既存111件と合わせ117件が成功。機能・依存の追加は行っていない。偽バックエンドの成功は実OSの適用/復元成功ではない。

## デモで観測した操作

- 4タブをUIA SelectionItemで切替。OperationStatus / AutomationState / ProfileChoice / ProfileList / RuleList / SavePreferencesのName、Role、有効状態を観測。選択タブ上の対象はoffscreen=False。
- 確認ダイアログに現在値と変更先を表示し、取消が初期フォーカス。取消で適用せず戻れる。
- 確認付き再開で架空の大学プロファイルがデモの現在値へ反映。
- 確認付きDirect適用後に自動切替が一時停止。
- 確認付き直前復元で架空プロキシ、初回変更前の復元でDirectへ戻る。
- 終了確認を表示。OS標準MessageBoxのOKはこの環境のUIAでPaneと報告されInvokePatternなし。自動終了は失敗。利用者の終了報告と起動プロセス終了コード0を確認。Win32での終了代替は実行完了を確認せず、最終スクリプトから削除。
- 補助検証開発中に予約変数HOMEへの代入、同名の装飾テキスト取得、焦点を受け取れない項目へのSetFocusを修正。復元ダイアログの取消フォーカスが一度失敗。デモへフォーカスを戻して再実行すると復元確認を含め成功。ただし手動焦点確認の代替にしない。

## 未実施の検証・残る問題

| 対象 | 状態と理由 |
|---|---|
| Windows 10 22H2 x64 | 未実施。別OS/VMをこの作業で用意していない。Windows 11から推測しない |
| Windows 11 x64 | 上記のみ実施。全受け入れ未完了 |
| A01 / A03 / A13 | UIA操作・取消・復元の一部だけ観測。マウスなし初期設定、Tab順、Enter連打、編集後の焦点復帰は未実施 |
| A02 / A11 | Narrator発話とName/Role/Value/State、live region通知・連打は未実施 |
| A04 / A05 | コントラストテーマ、disabled/focusの見え方、実色比測定は未実施 |
| A06〜A10 / A14 | DPI100/150/200%、OS文字100/150/200%、アプリ200%、混在DPI、長文、テーマ・文字変更中の編集保持は未実施 |
| A12 | ×→隠す→再表示、トレイなし再表示、複数セッションは未実施。終了だけ利用者報告とコードを観測 |
| OS標準MessageBoxのアクセシビリティ | UIAがOKをPaneと報告しInvokeを提供しない未解決項目。キーボード/Narratorで終了・削除等の確認を扱えるか手動確認が必要 |
| W01〜W15の実設定・イベント確認 | 未実施。読取専用smokeのみ。native write/readback/rollback、PAC/WPAD残存値、ポリシー、外部同時書込み、WLAN拒否、VPN、復帰イベント、起動登録、Edge反映は偽テスト・固定デモで確認できない |
| NLM/WLAN購読と寿命 | 読取りと配置のみ成功。実イベント、拒否、スリープ後のハンドル寿命は未実施 |
| 発行・配布・署名 | 未実施。受け入れ後にscripts/publish.ps1と発行先からの再確認が必要。署名・インストーラーなし |
| CPU / メモリー / 起動時間 / 配布サイズ | 未測定 |

リリース阻害項目はWindows 10互換性、手動アクセシビリティ、許可した使い捨て環境でのnative適用・復元・競合確認、発行後検証。公開可能の判断は行っていない。

## 想定外の副作用

最初のフルパスdotnet起動では証明書自動生成の無効化を指定しておらず、SDKが「ASP.NET Core HTTPS 開発証明書をインストールしました」と表示した。証明書ストア変更の許可を事前に得ていない点は作業上の問題。信頼コマンドは実行していない。実際のストア変更有無は未確認であり、無変更とも断定しない。以後は自動生成を無効化。追加変更を起こさないため証明書ストアの操作は行っていない。必要なら読取りで対象を特定し、削除は許可後に行う。

アプリの実プロキシ、自動起動、Wi-Fi/位置情報、ポリシー、ネットワーク、DNS、ファイアウォールへの書込みは行っていない。復元操作はデモのメモリーと偽テストのみ。実復元確認は許可した使い捨てVM/アカウントで、現在値保存・バックアップ・戻し先の提示とアプリ内確認を経て行う。

## CLI＋トレイへのリファクタリング（2026-10-15）

### 変更範囲

利用者の指示に従い、WPF GUIをarchive/Proxyz.Wpfへ移し、ソリューションから除外。Core/Windowsは維持して、OS非依存のCommandServiceとCLI、最小のWinFormsトレイを接続した。アーカイブのプロジェクト参照は移動先に合わせ修正し、独立ビルドも確認。旧GUIのUIA補助スクリプトとアクセシビリティ表もアーカイブ側に保存した。

status / up / down / switch / direct / restore、profile、rule、config、check、tray、quit、version/helpを実装。networkはstatus --verboseへ統合。トレイは状態・ネットワーク・プロファイル・自動切換チェック・復元・接続確認・設定・About・終了。設定用メイン画面なし。操作仕様はCLI.md、構成はARCHITECTURE.mdへ更新した。

常駐とCLIは同じユーザー・セッション限定のパイプで接続し、同一ファイルロックで他プロセス/旧GUI/別セッションとの重複書込みを抑止する。OS書込みは既存Controllerに集約。プレビュー後のconfig/journal/現在値、必要な場合ネットワークを再照合。startupは登録文字列全体を照合し、ストアにpauseを保存してから登録を試みる。

### 検証環境とコマンド

Windows 11 Pro 25H2 x64、26200.9711。SDK10.0.402 / MSBuild18.9.15 / NETCore・WindowsDesktop Runtime10.0.13を再確認。通常ユーザー、管理者昇格なし。各dotnet呼出しでDOTNET_GENERATE_ASPNET_CERTIFICATE=false、DOTNET_CLI_TELEMETRY_OPTOUT=1、DOTNET_CLI_HOMEを作業ルートの.dotnet-homeに設定。SDKのScoopフルパスを使用。統合検証ではそのディレクトリを子プロセスのPATHとDOTNET_ROOTに設定。実機設定変更の許可は求めておらず、変更していない。

以下のdotnetは `C:\Users\kenjiam\scoop\apps\dotnet-sdk\current\dotnet.exe`。作業ルートから実行。成功行はツールが返した終了コードと観測結果を記録する。

| コマンド | 終了コード | 結果と範囲 |
|---|---|---|
| dotnet restore .\Proxyz.slnx | 0 | 7プロジェクト復元。現行はApp/Core/Cli/Windowsと3テスト |
| dotnet build .\Proxyz.slnx -c Release --no-restore -t:Rebuild | 0 | 全7プロジェクト、警告0、エラー0 |
| dotnet run --project tests/Proxyz.Core.Tests -c Release --no-build | 0 | 117/117 passed; 0 failed. 既存安全性/復元テストを維持 |
| dotnet run --project tests/Proxyz.Cli.Tests -c Release --no-build | 0 | 最終35/35 passed; 0 failed. 途中29件・33件から回帰ケース追加 |
| dotnet run --project tests/Proxyz.Windows.Tests -c Release --no-build | 0 | x64構造体配置と診断APIエクスポート。APIは呼ばず通信なし |
| dotnet run --project tests/Proxyz.Windows.Tests -c Release -- --read-os | 0 | 最後に追加した診断読取チェックを含めビルド/実行。WinINet・WinHTTP設定読取成功、NLM3接続、終了前再読取り一致。URLなしでHTTPプローブなし。設定本文/接続名は表示しない |
| scripts/test-cli-demo.ps1（子プロセスPATH指定、許可後サンドボックス外） | 0 | 最終15項目成功。初回画面/常駐確認は--demoのみ。下記の途中失敗も参照 |
| dotnet build archive/Proxyz.Wpf/Proxyz.App.csproj -c Release | 0 | ソリューション外の旧GUIも警告0、エラー0でビルド。GUI再起動はしていない |
| python scripts/verify-source.py | 0 | 構造確認7分類成功。アーカイブ込み8プロジェクト、59 C#ファイル。機能検証の代替ではない |
| PowerShell Parserでtest-cli-demo.ps1を解析 | 0 | 構文エラーなし |
| publish / 実HTTP URLテスト / 実設定書込み | — | 未実施 |

新Windows.Testsの--read-os実行前に、現在ユーザーWinINet、WinHTTP静的既定、管理制限、NLMを読み、設定本文/接続名を出さず外部通信しないと利用者へ説明した。新しいWinHTTP診断は読取成功のみで、実HTTP経路の成功を意味しない。

### 新しいテストで確認したこと

CLIの35件は、読取/プレビュー/取消で書き込まないこと、手動適用・復元とpause、常駐なしの再開拒否、確認後のconfig/現在値/ネットワーク変更拒否、RecoveryTargetのプレビュー、部分編集の値保持、複数方式、capture、入力拒否、参照中削除拒否、SSID/IDのAND、複数接続やSSID不足で推測しないこと、configの部分変更、保存失敗前の起動登録抑止、起動登録の外部文字列変更拒否、診断の明示確認、quitと使い捨てplan、JSON、CRLF/LF、不完全/過大メッセージ等。偽ICommandPlatform/IProxyBackendであり実OS書込みなし。

デモ統合15項目では、別CLIプロセス間の状態共有、プロファイル保存、dry-run非適用、確認済み切替とpause、非対話で--yesなしの拒否、初回復元、ルールでの自動適用、downでプロキシ維持、config、現在ID/SSIDからルール保存・無効化・削除、プロファイル削除、架空のcheck、ホスト正常終了、upによる常駐自動起動とCLIへの復帰を確認。実プロキシ、起動登録、WLAN、外部HTTPは使っていない。チェック数はハーネスの表示どおり15で、操作個数の総数ではない。

### 途中で見つけた不具合と修正

- 最初の単発--demo statusはサンドボックスでProxyz-Demoのロックへのアクセスが拒否され終了1。許可後に独立デモ統合検証で実行した。
- 統合スクリプト初版はダウンロード済みcommon.ps1が実行ポリシーで拒否され終了1。ポリシーやZone.Identifierは変更していない。新規統合スクリプトを小さな自己完結構成にして実行した。
- パイプの行読取りがCRLFのCRを残し、executeを取消と誤認して統合検証が終了1。行末CRを処理し、LF/CRLF/不完全/1MiB上限の回帰テストを追加。途中に残った自分のデモは通常のquit要求で終了し、強制終了はしていない。
- upの自動起動時に子プロセスがCLIの出力ハンドルを継承し、パイプを使う呼出し元が戻らなかった。Shell経由の非表示起動へ修正して出力を分離。待機中の自分のデモを通常quitで終了した後、最終統合テストでupの自動起動と復帰を確認した。
- 常駐なし/再開確認待ちでも保存されたAutomationEnabledだけで有効と見えていた状態表示を修正。常駐・保留状態を考慮し、回帰テストを追加。
- startupのプレビュー/再確認をboolだけでなく登録文字列全体へ強化。発行EXEの検証をレジストリキー作成より前へ移し、未対応起動元で不用意にキーを作らないようにした。実登録は未実施。

### 残る未確認事項

- Windows 10 22H2 x64の実行互換性。今回もWindows 11の結果から推測しない。
- 新しいトレイの全メニューと確認ダイアログを人が操作する確認、キーボード/Narrator、DPI/コントラスト。常駐を起動してCLIで共有処理を操作したことと、すべてのメニューをクリックしたことは別。
- WinINet/WinHTTPの実HTTP HEAD経路、PAC/WPAD、認証407、リダイレクト、タイムアウト、各ハンドルの実通信時の寿命。構造体/エクスポート/設定読取りは成功したがHTTP通信は未実施。
- 実OSのプロキシwrite/readback/rollback、外部競合、管理制限下、WLAN許可/拒否、VPN、有線+Wi-Fi、スリープ/セッション復帰、起動登録/解除、Edge等への反映。既存の偽テストの成功から実機成功を主張しない。
- 発行フォルダーからの起動、署名、インストーラー、Windows10/11配布受け入れ、メモリー/CPU/起動時間測定。軽量化の数値は未測定。

CLI/トレイへのソース移行と利用可能な検証は完了したが、上記のリリース受け入れは未完了。実設定や起動登録の変更・許可の取得、commit/push/公開は行っていない。生成時の履歴と旧WPFの検証履歴は保持した。
