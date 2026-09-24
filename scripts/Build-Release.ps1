[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'xiaoxu-music-extension'
$projectPath = Join-Path $sourceRoot 'host\xiaoxu-music-host.csproj'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish'
$stageRoot = Join-Path $artifactsRoot 'xiaoxu-music-bridge'
$zipPath = Join-Path $artifactsRoot 'xiaoxu-music-bridge-windows-x64.zip'
$hashPath = "$zipPath.sha256"
$validatorPath = Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1'

# The validator scans repository release inputs before checking package layout.
try {
    & $validatorPath -PackageRoot $sourceRoot
    throw 'Source-folder preflight unexpectedly passed package validation.'
}
catch {
    if ($_.Exception.Message -notlike '*Required release file is missing: xiaoxu-music-host.exe*') {
        throw
    }
}
Write-Output 'PASS: release source inputs contain no developer-specific literals.'

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
foreach ($path in @($publishRoot, $stageRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path | Out-Null
}
foreach ($path in @($zipPath, $hashPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$publishArguments = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '/p:PublishSingleFile=true',
    '/p:DebugType=None',
    '/p:DebugSymbols=false',
    '-o', $publishRoot
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object Extension -ne '.pdb') {
    $relativePath = $file.FullName.Substring($publishRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $stageRoot $relativePath
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

$rootFiles = @('install.bat', 'uninstall.bat', 'restart-audio.bat', 'EXTENSION_ID.txt', 'README-install.txt')
foreach ($fileName in $rootFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $fileName) -Destination (Join-Path $stageRoot $fileName)
}

$extensionFiles = @(
    'manifest.json',
    'background.js',
    'host-bootstrap.js',
    'content.js',
    'reconnect-policy.js',
    'icon16.png',
    'icon48.png',
    'icon128.png',
    # web_accessible_resources entry referenced by manifest.json — leaving
    # it out of the stage folder ships a broken extension where the dashboard
    # fetch interceptor never loads. Reproduced 2026-09-24 after manual copy
    # to D:\music_bridge\ did not include injected.js.
    'injected.js',
    # Used by chrome://extensions "Pack extension" flow to keep the same
    # extension ID across reinstalls. Drop the file if you want a fresh ID.
    'key.pem'
)
$stageExtension = Join-Path $stageRoot 'extension'
New-Item -ItemType Directory -Path $stageExtension | Out-Null
foreach ($fileName in $extensionFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot "extension\$fileName") -Destination (Join-Path $stageExtension $fileName)
}

$validatorArguments = @{ PackageRoot = $stageRoot }
if ($SmokeTest) {
    $validatorArguments.SmokeTest = $true
}
& $validatorPath @validatorArguments

Add-Type -AssemblyName System.IO.Compression
$relativePaths = [string[]]@(Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object {
    $_.FullName.Substring($stageRoot.Length).TrimStart('\', '/')
})
[Array]::Sort($relativePaths, [StringComparer]::Ordinal)

$archiveStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$archive = $null
try {
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    foreach ($relativePath in $relativePaths) {
        $entryName = 'xiaoxu-music-bridge/' + $relativePath.Replace('\', '/')
        $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $sourceStream = [IO.File]::OpenRead((Join-Path $stageRoot $relativePath))
        $entryStream = $null
        try {
            $entryStream = $entry.Open()
            $sourceStream.CopyTo($entryStream)
        }
        finally {
            if ($null -ne $entryStream) {
                $entryStream.Dispose()
            }
            $sourceStream.Dispose()
        }
    }
}
finally {
    if ($null -ne $archive) {
        $archive.Dispose()
    }
    else {
        $archiveStream.Dispose()
    }
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
$assetName = Split-Path -Leaf $zipPath
Set-Content -LiteralPath $hashPath -Value "$hash  $assetName" -Encoding ASCII

$extractionRoot = Join-Path ([IO.Path]::GetTempPath()) ("小许 音乐发布 " + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $extractionRoot | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractionRoot
    $extractedPackage = Join-Path $extractionRoot 'xiaoxu-music-bridge'
    $extractedValidatorArguments = @{ PackageRoot = $extractedPackage }
    if ($SmokeTest) {
        $extractedValidatorArguments.SmokeTest = $true
    }
    & $validatorPath @extractedValidatorArguments
}
finally {
    if (Test-Path -LiteralPath $extractionRoot) {
        Remove-Item -LiteralPath $extractionRoot -Recurse -Force
    }
}

Write-Output "Release archive: $zipPath"
Write-Output "SHA256: $hash"
