#Requires -RunAsAdministrator
<#
.SYNOPSIS
    xiaoxu-music-extension 安装脚本
.DESCRIPTION
    编译 native host → 复制到安装目录 → 注册到 Chrome 注册表 → 复制扩展文件
    运行后只需在 Chrome 中加载已解压的扩展即可。
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

# Copy native host files
Copy-Item "$PublishDir\*" $InstallDir -Force

# Copy lyrics directory (preserve user's lyrics if already exists)
$LyricsDir = Join-Path $ProjectRoot "lyrics"
$InstallLyricsDir = Join-Path $InstallDir "lyrics"
if (!(Test-Path $InstallLyricsDir)) {
    Copy-Item $LyricsDir $InstallLyricsDir -Recurse -Force
}

Write-Host "  -> OK" -ForegroundColor Green

# ── 3. Create native host manifest ────────────────────────────────
Write-Host "[3/5] Registering native messaging host..." -ForegroundColor Yellow

$exePath = Join-Path $InstallDir "xiaoxu-music-host.exe"
$manifestPath = Join-Path $InstallDir "xiaoxu_music_host.json"

$manifest = @{
    name           = "xiaoxu_music_host"
    description    = "xiaoxu-music-bridge native messaging host"
    path           = $exePath
    type           = "stdio"
    allowed_origins = @("chrome-extension://PLACEHOLDER_EXTENSION_ID/")
} | ConvertTo-Json -Depth 5

Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

# Register in Chrome registry (current user)
$regPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host"
if (!(Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}
Set-ItemProperty -Path $regPath -Name "(Default)" -Value $manifestPath -Type String

Write-Host "  -> OK" -ForegroundColor Green

# ── 4. Copy Chrome extension ──────────────────────────────────────
Write-Host "[4/5] Copying Chrome extension..." -ForegroundColor Yellow

$InstallExtDir = Join-Path $InstallDir "extension"
if (Test-Path $InstallExtDir) {
    Remove-Item $InstallExtDir -Recurse -Force
}
Copy-Item $ExtensionDir $InstallExtDir -Recurse -Force

Write-Host "  -> OK" -ForegroundColor Green

# ── 5. Summary ────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Installation complete! ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Open Chrome and go to: chrome://extensions" -ForegroundColor White
Write-Host "  2. Enable 'Developer mode' (top right)" -ForegroundColor White
Write-Host "  3. Click 'Load unpacked' and select: $InstallExtDir" -ForegroundColor White
Write-Host "  4. Note the extension ID shown on the extension card" -ForegroundColor White
Write-Host "  5. Run this command to register the extension ID:" -ForegroundColor White
Write-Host ""
Write-Host "     powershell -Command `"@{name='xiaoxu_music_host';description='xiaoxu-music-bridge';path='$exePath';type='stdio';allowed_origins=@('chrome-extension://YOUR_EXTENSION_ID/')} | ConvertTo-Json | Set-Content '$manifestPath'`"" -ForegroundColor DarkYellow
Write-Host ""
Write-Host "  6. Restart Chrome" -ForegroundColor White
Write-Host ""
Write-Host "After that, just open xiaoxu.xin and the music bridge works automatically!" -ForegroundColor Green
Write-Host ""
Write-Host "Files installed to: $InstallDir" -ForegroundColor Gray
