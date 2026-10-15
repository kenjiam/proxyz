# CLI / トレイの操作性

2026-10-15の利用者指示によりメインGUIをアーカイブした。以前のWPF仕様・A01〜A14チェック表は [archive/Proxyz.Wpf/ACCESSIBILITY.md](../archive/Proxyz.Wpf/ACCESSIBILITY.md) に保存している。過去のGUI検証を新しいトレイの合格とはしない。

CLIは全機能の操作経路を提供し、トレイが使えなくても設定・切替・復元・終了ができる。stdout/stderrと終了コードを分け、確認の既定は取消。状態は色だけでなくOK/Paused/Warning/Error/Recoveryと理由で表示する。

トレイと補助ダイアログは標準WinForms。確認ダイアログは取消へフォーカスし、Enter/Escで誤って適用しない。詳細値は読取り専用TextBoxからコピーでき、長文はスクロール可能。システム色・DPIを使う。

新しいトレイのNarrator・キーボード操作・コントラスト・混在DPI・文字倍率の人による受け入れは未実施。CLI/デモテストの成功から合格と推測しない。実施結果は [TEST_RESULTS.md](TEST_RESULTS.md) へ記録する。
