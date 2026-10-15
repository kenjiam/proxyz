[CmdletBinding()]
param([switch]$ExerciseRecovery)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$scope = [System.Windows.Automation.TreeScope]::Descendants
$trueCondition = [System.Windows.Automation.Condition]::TrueCondition
$nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
$title = 'Proxyz — デモ（Windows 設定は変更しません）'
$condition = [System.Windows.Automation.PropertyCondition]::new($nameProperty, $title)
$window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if ($null -eq $window) { throw 'The demo window must already be running and visible.' }
$demoProcessId = $window.Current.ProcessId
$window.SetFocus()
Start-Sleep -Milliseconds 200
function Find-Name($parent, [string]$name) {
    $items = $parent.FindAll($scope, [System.Windows.Automation.PropertyCondition]::new($nameProperty, $name))
    $item = $items | Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text } | Select-Object -First 1
    if ($null -eq $item) { throw "Missing UIA element: $name" }
    return $item
}
function Invoke-Element($element) {
    if ($element.Current.IsKeyboardFocusable) { $element.SetFocus() }
    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 500
}
foreach ($tabName in @('ホーム', 'プロファイル', '自動切替ルール', '設定')) {
    $tab = Find-Name $window $tabName
    ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 200
    Write-Output "PASS demo tab: $tabName"
    foreach ($element in $window.FindAll($scope, $trueCondition)) {
        $current = $element.Current
        if ($current.AutomationId -in @('OperationStatus','AutomationState','ProfileChoice','ProfileList','RuleList','SavePreferences')) {
            Write-Output ("UIA {0}: name={1}; role={2}; enabled={3}; offscreen={4}" -f $current.AutomationId, $current.Name, $current.ControlType.ProgrammaticName, $current.IsEnabled, $current.IsOffscreen)
        }
    }
}
$homeTab = Find-Name $window 'ホーム'
([System.Windows.Automation.SelectionItemPattern]$homeTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Invoke-Element (Find-Name $window '直接接続にする(D)…')
$processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $demoProcessId)
$dialogs = [System.Windows.Automation.AutomationElement]::RootElement.FindAll($scope, $processCondition)
$confirm = $dialogs | Where-Object { $_.Current.Name -eq '変更内容の確認 — Proxyz' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
if ($null -eq $confirm) { throw 'Missing confirmation dialog.' }
$cancel = Find-Name $confirm 'キャンセル(C)'
if (-not $cancel.Current.HasKeyboardFocus) { throw 'Confirmation did not focus Cancel.' }
Write-Output 'PASS confirmation initial focus: Cancel'
Invoke-Element $cancel
Write-Output 'PASS confirmation cancellation'
function Find-Dialog([string]$name) {
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll($scope, $processCondition)
    $dialog = $elements | Where-Object { $_.Current.Name -eq $name -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($null -eq $dialog) { throw "Missing demo dialog: $name" }
    return $dialog
}
function Confirm-Demo([string]$button) {
    $dialog = Find-Dialog '変更内容の確認 — Proxyz'
    if (-not (Find-Name $dialog 'キャンセル(C)').Current.HasKeyboardFocus) { throw 'Confirmation did not focus Cancel.' }
    Invoke-Element (Find-Name $dialog $button)
    Start-Sleep -Milliseconds 500
}
function Check-Current([string]$expected) {
    $box = Find-Name $window '現在のプロキシ設定の詳細。選択してコピーできます。'
    $value = ([System.Windows.Automation.ValuePattern]$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($value -notlike "*$expected*") { throw "Demo proxy description did not contain: $expected" }
}
if ($ExerciseRecovery) {
    Invoke-Element (Find-Name $window '自動切替を再開(A)…')
    Confirm-Demo '再開する(A)'
    Check-Current 'proxy.example.invalid:8080'
    Write-Output 'PASS demo automation applied the fictional campus profile'
    Invoke-Element (Find-Name $window '直接接続にする(D)…')
    Confirm-Demo '変更する(A)'
    Check-Current '直接接続'
    $state = $window.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AutomationState'))
    if ($state.Current.Name -ne '自動切替：一時停止') { throw 'Manual apply did not pause automation.' }
    Write-Output 'PASS demo manual direct apply paused automation'
    Invoke-Element (Find-Name $window '直前の設定に戻す(U)…')
    Confirm-Demo '復元する(R)'
    Check-Current 'proxy.example.invalid:8080'
    Write-Output 'PASS demo previous-state restore'
    Invoke-Element (Find-Name $window '初回変更前に戻す(B)…')
    Confirm-Demo '復元する(R)'
    Check-Current '直接接続'
    Write-Output 'PASS demo initial-state restore'
}

