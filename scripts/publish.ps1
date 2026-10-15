[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SelfContained
)
. (Join-Path $PSScriptRoot 'common.ps1')
Require-Windows
$root = Get-ProxyzRoot
Push-Location $root
try {
    Require-DotNet10
    $output = Join-Path $root ("artifacts/publish/" + $Runtime)
    if ((Test-Path $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
        throw "Output is not empty: $output. Preserve any needed build and remove only this generated folder before republishing."
    }
    $contained = 'false'
    if ($SelfContained) { $contained = 'true' }
    Invoke-DotNet -Arguments @('publish', 'src/Proxyz.App/Proxyz.App.csproj', '-c', 'Release', '-r', $Runtime,
        '--self-contained', $contained, '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-o', $output)
    Write-Host "Published to $output. The executable was not launched and no Windows settings were changed."
    Write-Host 'Validate this exact output on the target Windows version and architecture before distribution.'
}
finally { Pop-Location }
