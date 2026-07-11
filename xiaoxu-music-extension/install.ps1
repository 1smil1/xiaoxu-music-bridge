#Requires -RunAsAdministrator
<#
.SYNOPSIS
    xiaoxu-music-extension 安装脚本
.DESCRIPTION
    编译 native host → 复制到安装目录 → 注册到 Chrome 注册表 → 复制扩展文件
    运行后只需在 Chrome 中加载已解压的扩展即可。
    扩展 ID 已通过 manifest.json 的 key 字段固定，无需手动注册。
#>

param(
    [string]$InstallDir = "$env:LOCALAPPDATA\xiaoxu-music-extension"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$HostProject = Join-Path $ProjectRoot "host"
$ExtensionDir = Join-Path $ProjectRoot "extension"
$PublishDir = Join-Path $HostProject "publish"

Write-Host "=== xiaoxu-music-extension Installer ===" -ForegroundColor Cyan
Write-Host ""

# ── 1. Build native host ──────────────────────────────────────────
Write-Host "[1/5] Building native host..." -ForegroundColor Yellow
Push-Location $HostProject
try {
    dotnet publish -c Release -r win-x64 --self-contained false -o $PublishDir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit code $LASTEXITCODE)"
    }
    Write-Host "  -> OK" -ForegroundColor Green
}
finally {
    Pop-Location
}

# ── 2. Create install directory ────────────────────────────────────
Write-Host "[2/5] Installing to $InstallDir..." -ForegroundColor Yellow
if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Kill any running host (and legacy bridge.exe that may hold port 17888)
Get-Process -Name "xiaoxu-music-host","xiaoxu-music-bridge" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Copy native host files
Copy-Item "$PublishDir\*" $InstallDir -Force

# Copy lyrics directory (preserve user's lyrics if already exists)
$LyricsDir = Join-Path $ProjectRoot "lyrics"
$InstallLyricsDir = Join-Path $InstallDir "lyrics"
if (!(Test-Path $InstallLyricsDir)) {
    Copy-Item $LyricsDir $InstallLyricsDir -Recurse -Force
}

Write-Host "  -> OK" -ForegroundColor Green

# ── 3. Read fixed extension ID from EXTENSION_ID.txt ──────────────
$ExtensionIdFile = Join-Path $ProjectRoot "EXTENSION_ID.txt"
if (!(Test-Path $ExtensionIdFile)) {
    throw "EXTENSION_ID.txt not found at $ExtensionIdFile"
}
$ExtensionId = (Get-Content $ExtensionIdFile -Raw).Trim()
if (-not $ExtensionId) {
    throw "EXTENSION_ID.txt is empty"
}
Write-Host "[3/5] Fixed extension ID: $ExtensionId" -ForegroundColor Yellow

# ── 4. Create native host manifest with fixed ID ──────────────────
$exePath = Join-Path $InstallDir "xiaoxu-music-host.exe"
$manifestPath = Join-Path $InstallDir "xiaoxu_music_host.json"

$manifest = @{
    name           = "xiaoxu_music_host"
    description    = "xiaoxu-music-bridge native messaging host"
    path           = $exePath
    type           = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 5

Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

# Register in Chrome registry (current user + machine)
$regPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host"
if (!(Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}
Set-ItemProperty -Path $regPath -Name "(Default)" -Value $manifestPath -Type String

$regPathHklm = "HKLM:\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host"
if (Test-Path "HKLM:\Software\Google\Chrome") {
    if (!(Test-Path $regPathHklm)) {
        New-Item -Path $regPathHklm -Force | Out-Null
    }
    Set-ItemProperty -Path $regPathHklm -Name "(Default)" -Value $manifestPath -Type String -ErrorAction SilentlyContinue
}

Write-Host "  -> OK" -ForegroundColor Green


# Sync JSON to Chrome User Data NativeMessagingHosts directory.
# Some older installs left a stale JSON there with the WRONG extension ID;
# Chrome does not always re-read HKCU on hot-update, so we ALSO overwrite
# the User Data JSON to make sure both lookup paths return the correct ID.
# Idempotent - safe to run repeatedly.
$userDataDir = Join-Path $env:LOCALAPPDATA "Google\Chrome\User Data"
$userDataNmDir = Join-Path $userDataDir "NativeMessagingHosts"
if (!(Test-Path $userDataNmDir)) {
    New-Item -ItemType Directory -Path $userDataNmDir -Force | Out-Null
}
$userDataJson = Join-Path $userDataNmDir "xiaoxu_music_host.json"
try {
    Copy-Item -Path $manifestPath -Destination $userDataJson -Force
    Write-Host "  -> Synced to Chrome User Data JSON" -ForegroundColor Green
} catch {
    Write-Host "  -> WARN: Could not sync to Chrome User Data JSON: $_" -ForegroundColor Yellow
}
# ── 5. Copy Chrome extension ──────────────────────────────────────
Write-Host "[4/5] Copying Chrome extension..." -ForegroundColor Yellow

$InstallExtDir = Join-Path $InstallDir "extension"
if (Test-Path $InstallExtDir) {
    Remove-Item $InstallExtDir -Recurse -Force
}
Copy-Item $ExtensionDir $InstallExtDir -Recurse -Force

Write-Host "  -> OK" -ForegroundColor Green

# ── 6. Summary ────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Installation complete! ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Open Chrome and go to: chrome://extensions" -ForegroundColor White
Write-Host "  2. Enable 'Developer mode' (top right)" -ForegroundColor White
Write-Host "  3. Click 'Load unpacked' and select: $InstallExtDir" -ForegroundColor White
Write-Host "  4. The extension ID should be: $ExtensionId" -ForegroundColor White
Write-Host "     (fixed via manifest.json key field — same on every PC)" -ForegroundColor Gray
Write-Host "  5. Restart Chrome" -ForegroundColor White
Write-Host ""
Write-Host "After that, just open xiaoxu.xin and the music bridge works automatically!" -ForegroundColor Green
Write-Host ""
Write-Host "Files installed to: $InstallDir" -ForegroundColor Gray
