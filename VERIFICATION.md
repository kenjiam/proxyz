# 作成環境で実施した確認

作成日：2026-10-14。対象：この ZIP のソース実装版 0.1.0。

## 実施したこと

Linux 上の Python を使い、`python scripts/verify-source.py` を実行しました。次の構造検査は終了コード0で完了しています。機械可読の記録は [docs/SOURCE_CHECKS.json](docs/SOURCE_CHECKS.json) にあります。

| 検査 | 結果と範囲 |
|---|---|
| XML | XAML / csproj / props / slnx / manifest / NuGet.Config が XML として読める |
| JSON | global.json、サンプル、確認記録が JSON として読める |
| プロジェクト参照 | 5プロジェクトとソリューションの参照先ファイルが存在する |
| XAML とコードビハインド | x:Class、ファイル、明示イベントハンドラー名、同一ファイル内の x:Name を照合 |
| C# の括弧 | コメントと文字列を飛ばした簡易的な括弧対応検査。構文/型検査ではない |
| ドキュメント | ローカルのファイルリンク先が存在する。外部URLとアンカーの自動検証ではない |
| 配布物の衛生 | EXE/DLL、秘密鍵、フォント、.env、NotImplementedException のスタブを含まない |

生成時にプロジェクト設定の TFM / SupportedOSPlatformVersion 不整合も確認し、基本 `net10.0-windows` と衝突しないよう修正しました。Windows 10 以降を対象にする実行時ガードとマニフェストは保持しています。

`SOURCE_MANIFEST.sha256` は、このファイルとソース/文書のハッシュを含みます（マニフェスト自身を除く）。最終ZIPのCRCと展開後のファイルハッシュは作成工程で照合しています。コードの正しさや署名を保証するものではありません。

## 実施していないこと

この環境には .NET SDK / C# コンパイラー、Windows、PowerShell、WPF ランタイムがありません。したがって以下は **すべて未実施** です。

| 項目 | 状態 |
|---|---|
| dotnet restore / build / publish | 未実施 |
| Core.Tests の実行 | 未実施。テストコードのみ同梱 |
| Windows.Tests の構造体配置検査の実行 | 未実施。テストコードのみ同梱 |
| WinINet の実設定の読取り/変更/復元 | 未実施 |
| NLM COM / WLAN 通知・権限の実機確認 | 未実施 |
| WPF の XAML コンパイル / 画面起動 | 未実施 |
| デモモードの Windows 上での実行 | 未実施 |
| Narrator / キーボード / コントラスト / 拡大の実機確認 | 未実施 |
| Windows 10 / Windows 11 の互換性確認 | 未実施 |
| CPU / メモリー / 起動時間 / バイナリー容量の測定 | 未実施 |
| Windows の実行ファイル / インストーラー / 署名 | 未作成 |

構造検査が通っただけで、コンパイル済み・テスト済み・実用可能と読み替えないでください。最初のビルドや実機確認で修正が必要になる可能性があります。

## 次に進める場所

[docs/CODEX_HANDOFF.md](docs/CODEX_HANDOFF.md) に検証順序・注意点・依頼文を記載しました。[docs/TEST_RESULTS.md](docs/TEST_RESULTS.md) は Windows 上での結果記録用です。これからの結果をこの歴史的な記録へ遡って合格と追記せず、別の実施日・環境の結果として記録してください。
