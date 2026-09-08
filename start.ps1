$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building UI..."
Push-Location ui
npm run build
Pop-Location

Write-Host "Building host..."
dotnet build host/Jester.Host.csproj -c Release --nologo

Write-Host "Starting Jester..."
Start-Process (Join-Path $PSScriptRoot "host\bin\Release\net10.0-windows\Jester.exe")
