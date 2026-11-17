# Runs the tests, then publishes the app.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#       -> dist\             one exe + web folder. Needs the .NET 10 Desktop Runtime on the PC.
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Standalone
#       -> dist-standalone\  runs on any Windows 10/11 x64 PC without installing .NET (much bigger).
#   -SkipTests               publish without running the tests first.
#
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without a BOM.)
param(
    [switch]$Standalone,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root $(if ($Standalone) { 'dist-standalone' } else { 'dist' })

$exe = Join-Path $out 'SpotifyWallpaper.exe'
function Get-RunningApp {
    Get-Process SpotifyWallpaper -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) }
}

if (-not $SkipTests) {
    dotnet test $root -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host 'Tests failed - not publishing.' -ForegroundColor Red; exit 1 }
}

# The exe cannot be replaced while it is running: ask it to quit cleanly (same as tray > Exit),
# and start it again once the new build is in place.
$wasRunning = [bool](Get-RunningApp)
if ($wasRunning) {
    Write-Host 'Spotify Wallpaper is running - asking it to exit...'
    & $exe --exit
    for ($i = 0; $i -lt 40 -and (Get-RunningApp); $i++) { Start-Sleep -Milliseconds 250 }
    if (Get-RunningApp) {
        Write-Host "It did not exit. Close it from the tray icon, then run this again." -ForegroundColor Yellow
        exit 1
    }
}

# Start from an empty folder so files from older builds do not linger.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$publish = @(
    'publish', (Join-Path $root 'host'),
    '-c', 'Release', '-r', 'win-x64', '-o', $out, '-nologo', '-v', 'q',
    '-p:PublishSingleFile=true',
    '-p:DebugType=embedded'
)
if ($Standalone) { $publish += '--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true' }
else { $publish += '--self-contained', 'false' }

dotnet @publish
if ($LASTEXITCODE -ne 0) { Write-Host 'Publish failed.' -ForegroundColor Red; exit 1 }

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published to {0}  ({1:N1} MB)" -f $out, $size) -ForegroundColor Green

if ($wasRunning) {
    Start-Process $exe
    Write-Host 'Started the new build.'
}
