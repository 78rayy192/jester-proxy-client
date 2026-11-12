$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = if ($args[0] -and $args[0] -notlike "-*") { $args[0] } else { "v1.0.0" }
$outDir = Join-Path $PSScriptRoot "release\Jester"
$zipPath = Join-Path $PSScriptRoot "release\Jester-windows-x64.zip"
$mingw = "C:\Users\PC-Admin\AppData\Local\Microsoft\WinGet\Packages\BrechtSanders.WinLibs.POSIX.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\mingw64\bin"
$hostDir = Join-Path $PSScriptRoot "rust\jester-host"
$target = Join-Path $hostDir "target"

Write-Host "Building UI..."
Push-Location (Join-Path $PSScriptRoot "ui")
npm run build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Pop-Location

Write-Host "Building host..."
Get-Process -Name jester,Jester -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$env:Path = "$mingw;" + $env:Path
$env:CARGO_TARGET_DIR = $target
Push-Location $hostDir
cargo build --release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Pop-Location

$built = Join-Path $target "release"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item (Join-Path $built "jester.exe") (Join-Path $outDir "Jester.exe")
Copy-Item (Join-Path $built "WebView2Loader.dll") (Join-Path $outDir "WebView2Loader.dll")
Copy-Item (Join-Path $PSScriptRoot "ui\dist") (Join-Path $outDir "ui") -Recurse

Write-Host "Packing zip..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Created $zipPath ($sizeMb MB)"

if ($args -contains "-Release") {
  Write-Host "Creating GitHub release $version..."
  gh release create $version $zipPath --repo 78rayy192/jester-proxy-client --title "Jester $version" --notes @"
Распакуйте **Jester-windows-x64.zip** и запустите ``Jester.exe``.

Windows 10 или 11, 64-bit. .NET ставить не нужно. Для туннеля подтвердите запрос прав администратора. При первом запуске нажмите «Загрузить ядро».
"@
}
