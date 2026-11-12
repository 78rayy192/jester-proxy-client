param([switch]$NativeCore)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
if ($dotnet -eq $localSdk) {
    $env:DOTNET_ROOT = Split-Path $localSdk
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location (Join-Path $PSScriptRoot 'ui')
try {
    npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw 'UI regression checks failed.' }
    npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
} finally { Pop-Location }
& $dotnet build tests/Jester.Regression/Jester.Regression.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
if ($NativeCore) {
    $env:JESTER_NATIVE_CORE = Join-Path $env:APPDATA 'Jester\cores\mihomo.exe'
    $env:JESTER_NATIVE_GEOIP = Join-Path $env:APPDATA 'Jester\run\geoip.metadb'
}
& $dotnet tests/Jester.Regression/bin/Release/net10.0-windows/Jester.Regression.dll
if ($LASTEXITCODE -ne 0) { throw 'Host regression checks failed.' }
