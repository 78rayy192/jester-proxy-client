$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building UI..."
Push-Location ui
npm run build
if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать интерфейс." }
Pop-Location

Write-Host "Building host..."
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
if ($dotnet -eq $localSdk) {
    $env:DOTNET_ROOT = Split-Path $localSdk
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
}
& $dotnet build host/Jester.Host.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать приложение." }

Write-Host "Starting Jester..."
Start-Process (Join-Path $PSScriptRoot "host\bin\Release\net10.0-windows\Jester.exe")
