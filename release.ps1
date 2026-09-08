$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = if ($args[0]) { $args[0] } else { "v0.1.0" }
$outDir = Join-Path $PSScriptRoot "release\Jester"
$zipPath = Join-Path $PSScriptRoot "release\Jester-windows-x64.zip"

Write-Host "Building UI..."
Push-Location ui
npm run build
Pop-Location

Write-Host "Publishing self-contained win-x64..."
Get-Process -Name Jester -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
dotnet publish host/Jester.Host.csproj -c Release -r win-x64 --self-contained true -o $outDir --nologo `
  -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $outDir -Recurse -Include *.pdb,*.xml | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $outDir "Jester.exe.WebView2") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $outDir "createdump.exe") -ErrorAction SilentlyContinue

Write-Host "Packing zip..."
New-Item -ItemType Directory -Force -Path (Split-Path $zipPath) | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Created $zipPath ($sizeMb MB)"

if ($args -contains "-Release") {
  Write-Host "Creating GitHub release $version..."
  gh release create $version $zipPath `
    --title "Jester $version" `
    --notes @"
## Скачать

Распакуй **Jester-windows-x64.zip** и запусти ``Jester.exe``.

- Windows 10/11 x64
- .NET ставить не нужно (сборка self-contained)
- Нужен **WebView2** (обычно уже есть в Windows 11)
- Для TUN — запуск от администратора
- При первом запуске нажми **Скачать ядро** (mihomo + wintun)

## Что внутри

Клиент Jester с UI: прокси, правила, щит, оформление.
"@
}
