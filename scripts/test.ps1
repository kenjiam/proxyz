[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$ReadWindows,
    [switch]$CoreOnly
)
. (Join-Path $PSScriptRoot 'common.ps1')
if ($ReadWindows -and $CoreOnly) { throw 'Choose either -CoreOnly or -ReadWindows.' }
if (-not $CoreOnly) { Require-Windows }
Push-Location (Get-ProxyzRoot)
try {
    Require-DotNet10
    Invoke-DotNet -Arguments @('run', '--project', 'tests/Proxyz.Core.Tests', '-c', $Configuration)
    Invoke-DotNet -Arguments @('run', '--project', 'tests/Proxyz.Cli.Tests', '-c', $Configuration)
    if (-not $CoreOnly) {
        $argsForTest = @('run', '--project', 'tests/Proxyz.Windows.Tests', '-c', $Configuration)
        if ($ReadWindows) {
            Write-Host 'Opt-in read-only smoke: reads current-user proxy, policy, and NLM state. No OS writes or WLAN identity access.'
            $argsForTest += @('--', '--read-os')
        }
        Invoke-DotNet -Arguments $argsForTest
    }
    Write-Host 'Requested executable test suites completed successfully. Manual Windows and accessibility validation remain separate.'
}
finally { Pop-Location }
