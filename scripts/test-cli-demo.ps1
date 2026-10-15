[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
$demoDll = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Proxyz.App/bin/Release/net10.0-windows/Proxyz.dll'
if (-not (Test-Path -LiteralPath $demoDll)) { throw 'Build Release first.' }
$env:DOTNET_ROOT = Split-Path (Get-Command dotnet).Source
$demoDotnet = (Get-Command dotnet).Source
$demoPassed = 0
function Invoke-Demo([string[]]$Command, [int]$Expected = 0) {
    $output = & $demoDotnet $demoDll --demo @Command
    $actual = $LASTEXITCODE
    if ($actual -ne $Expected) { throw "Demo command $($Command -join ' ') returned $actual; expected $Expected. $output" }
    return ($output -join "`n")
}
function Assert-Demo([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:demoPassed++
    Write-Host "PASS $Description"
}
# Start only the isolated demo. Never stop an unrelated/existing application.
$process = Start-Process -FilePath $demoDotnet -ArgumentList @(('"' + $demoDll + '"'), '--demo', 'tray', '--host', '--idle') -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 3
    if ($process.HasExited) { throw 'Demo host did not start (an existing demo may own its data lock).' }
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.State -eq 'Paused') 'initial demo paused'
    $null = Invoke-Demo @('profile','add','CLI-Test','--server','test.example.invalid:8081')
    $profile = Invoke-Demo @('profile','show','CLI-Test') | ConvertFrom-Json
    Assert-Demo ($profile.Settings.ProxyServer -eq 'test.example.invalid:8081') 'profile persisted in demo host memory across CLI processes'
    $null = Invoke-Demo @('switch','CLI-Test','--dry-run')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.Current.Flags -eq 1) 'dry-run did not apply'
    $null = Invoke-Demo @('switch','CLI-Test','--yes')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.Current.ProxyServer -eq 'test.example.invalid:8081' -and -not $status.AutomationEnabled) 'confirmed switch applied and paused'
    $null = Invoke-Demo @('direct') 2
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.Current.ProxyServer -eq 'test.example.invalid:8081') 'noninteractive write without yes rejected'
    $null = Invoke-Demo @('restore','--original','--yes')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.Current.Flags -eq 1) 'original restore'
    $null = Invoke-Demo @('up','--yes')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.AutomationEnabled -and $status.Current.ProxyServer -eq 'proxy.example.invalid:8080') 'up applied fictional rule through resident host'
    $null = Invoke-Demo @('down')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo (-not $status.AutomationEnabled -and $status.Current.ProxyServer -eq 'proxy.example.invalid:8080') 'down kept proxy'
    $null = Invoke-Demo @('config','set','unknown-network','direct')
    Assert-Demo ((Invoke-Demo @('config','get','unknown-network')) -eq 'direct') 'config get/set'
    $null = Invoke-Demo @('rule','add','CLI-Rule','--profile','CLI-Test','--current-network','--current-ssid')
    $rule = Invoke-Demo @('rule','show','CLI-Rule') | ConvertFrom-Json
    Assert-Demo ($null -ne $rule.NetworkId -and $null -ne $rule.SsidHex) 'rule captured ID and raw SSID'
    $null = Invoke-Demo @('rule','disable','CLI-Rule')
    $rule = Invoke-Demo @('rule','show','CLI-Rule') | ConvertFrom-Json
    Assert-Demo (-not $rule.Enabled) 'rule disable'
    $null = Invoke-Demo @('rule','remove','CLI-Rule','--yes')
    $null = Invoke-Demo @('profile','remove','CLI-Test','--yes')
    $null = Invoke-Demo @('check','--url','https://example.invalid/','--yes')
    Assert-Demo $true 'demo check completed without native diagnostics or traffic'
    $null = Invoke-Demo @('quit','--yes')
    Assert-Demo ($process.WaitForExit(10000)) 'quit stopped owned demo host gracefully'
    Assert-Demo ($process.ExitCode -eq 0) 'demo host exit code zero'
    $null = Invoke-Demo @('up','--yes')
    $status = Invoke-Demo @('status','--json') | ConvertFrom-Json
    Assert-Demo ($status.AutomationEnabled -and $status.Current.ProxyServer -eq 'proxy.example.invalid:8080') 'up launched an isolated resident host automatically'
    $null = Invoke-Demo @('quit','--yes')
    Start-Sleep -Milliseconds 500
    Write-Host "$demoPassed demo integration assertions passed. No real proxy/startup/WLAN writes or external probes."
}
finally {
    if (-not $process.HasExited) {
        Write-Host 'Owned demo is still running; requesting normal exit, not killing it.'
        $null = Invoke-Demo @('quit','--yes')
        $null = $process.WaitForExit(10000)
    }
    $process.Dispose()
}
