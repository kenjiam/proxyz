Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Get-ProxyzRoot {
    return Split-Path -Parent $PSScriptRoot
}
function Require-Windows {
    if ($env:OS -ne 'Windows_NT') {
        throw 'This operation requires Windows. Core-only tests can be run separately.'
    }
}
function Require-DotNet10 {
    $null = Get-Command dotnet -ErrorAction Stop
    $version = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed. Install/select the .NET 10 SDK described in README.' }
    if ($version -notmatch '^10\.0\.\d+') { throw "Expected a stable .NET 10 SDK; selected: $version" }
    Write-Host "Using .NET SDK $version"
}
function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    Write-Host ('> dotnet ' + ($Arguments -join ' '))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}
