# 実装時に参照した公式資料

確認日：2026-10-14。資料は API / 設計判断の根拠であり、このソースコードが動作検証済みである証拠ではありません。OS のサポート状況は変更されるので、配布時にも確認してください。

<a id="s01-runtime"></a>
## S01 — .NET / Windows の互換性とサポート

- [.NET 10 overview](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- [.NET 10 supported OS](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [.NET installation on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Windows 10 Home and Pro lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro)
- [What's new in WPF .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)

.NET 10 の LTS と、個別の Windows エディション/ビルドのサポートを区別する。本プロジェクトの Win10 22H2 互換性は実機確認対象であり、Microsoft の公式サポートを延長しない。

<a id="s02-frameworks"></a>
## S02 — プロジェクト設定

- [Target frameworks](https://learn.microsoft.com/en-us/dotnet/standard/frameworks)
- [NETSDK1135](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1135)
- [WPF .NET 9 — Fluent / ThemeMode](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)

TFM の API バージョンと最小実行対象、ランタイムの公式対応 OS は別。Windows SDK 追加 API を使わない WPF / P/Invoke 構成なので基本 TFM を使用する。

<a id="s03-wininet"></a>
## S03 — WinINet の読み取り・適用

- [INTERNET_PER_CONN_OPTIONW](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_optionw)
- [INTERNET_PER_CONN_OPTION_LISTW](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_option_listw)
- [Option flags](https://learn.microsoft.com/en-us/windows/win32/wininet/option-flags)
- [Setting and retrieving internet options](https://learn.microsoft.com/en-us/windows/win32/wininet/setting-and-retrieving-internet-options)

FLAGS_UI / FLAGS の区別、接続別オプション、文字列の解放、変更通知を参照。現版は標準 LAN 接続だけを対象とし、サービスからの利用を前提にしない。

<a id="s04-network"></a>
## S04 — Network List Manager

- [INetworkListManager](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetworklistmanager)
- [INetwork](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetwork)
- [INetworkConnection](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetworkconnection)
- [IEnumNetworks](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-ienumnetworks)
- [IEnumNetworkConnections](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-ienumnetworkconnections)
- [INetworkEvents](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nn-netlistmgr-inetworkevents)

型付き COM 列挙と接続イベントの契約に利用。NLM ID を不変な組織 ID と解釈しない。

<a id="s05-wifi"></a>
## S05 — Wi-Fi イベント・アクセス権

- [Wi-Fi access and location changes](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)
- [WlanRegisterNotification](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanregisternotification)
- [WlanQueryInterface](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface)
- [WLAN_CONNECTION_ATTRIBUTES](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_connection_attributes)
- [WLAN_ASSOCIATION_ATTRIBUTES](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_association_attributes)

取得許可の拒否を回避しない。位置情報に関係する API は明示 opt-in 後に使用する。構造体レイアウトはテスト対象。

<a id="s06-policy"></a>
## S06 — 組織の管理

- [Policy CSP — InternetExplorer](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-internetexplorer)
- [netsh winhttp](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-winhttp)

ユーザー/マシン単位や管理による禁止を考慮。WinHTTP は別の対象として認識し、本版では操作しない。

<a id="s07-accessibility"></a>
## S07 — アクセシビリティ

- [Accessibility best practices — UI Automation](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/accessibility-best-practices)
- [Accessibility overview — Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)
- [Windows app accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)

標準コントロール、キーボード、UIA、システム色、拡大、支援技術との実機テストを重視。TextScaleFactor レジストリはこの資料で保証された公開契約ではなく、互換性ブリッジとして隔離している。

<a id="s08-codex"></a>
## S08 — Codex の引き継ぎ

- [OpenAI: AGENTS.md guidance](https://developers.openai.com/codex/guides/agents-md/)

ルートの AGENTS.md に作業範囲・ビルド/テスト手順・OS変更の安全ルールを記載する。エージェントの許可設定をすべて無効化する前提にしない。

<a id="s09-cli-tray"></a>
## S09 — CLI / トレイと診断（2026-10-15）

- [Tailscale CLI](https://tailscale.com/docs/reference/tailscale-cli)：操作体系の参考。Proxyzの動詞やdownの意味は独自仕様。
- [Tailscale Windows](https://tailscale.com/docs/install/windows/msi)：トレイで状態と操作をまとめる形の参考。
- [WinHttpOpen](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpopen)：静的既定と自動構成は別の経路。AUTOMATIC_PROXYはWindows8.1以降のAPIで、ProxyzはWindows10以上を維持。
- [WinHTTP configuration](https://learn.microsoft.com/en-us/windows/win32/winhttp/setting-wininet-proxy-configurations-in-winhttp)：WinINetの設定とWinHTTPの扱いは同一ではない。診断結果を設定同期の根拠にしない。

参照したこととnative実行済みであることは別。HTTP診断は実URLで未検証のまま記録する。
