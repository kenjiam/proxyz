[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
Require-Windows
Push-Location (Get-ProxyzRoot)
try {
    Require-DotNet10
    Invoke-DotNet -Arguments @('restore', 'Proxyz.slnx')
    Invoke-DotNet -Arguments @('build', 'Proxyz.slnx', '-c', $Configuration, '--no-restore')
    Write-Host 'Build command succeeded. This does not mean the Windows integration or accessibility checks passed.'
}
finally { Pop-Location }
