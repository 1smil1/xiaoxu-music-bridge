[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'

function Assert-FileExists {
    param([string]$RelativePath)

    $path = Join-Path $script:ResolvedPackageRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $RelativePath"
    }
}

if (-not ('ReleasePackageBinaryScanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;

public static class ReleasePackageBinaryScanner
{
    private sealed class NullBoundedShortPath
    {
        private readonly StringBuilder value = new StringBuilder(6);
        private bool hasLeftBoundary;

        public NullBoundedShortPath(bool startsAtFileBoundary) =>
            hasLeftBoundary = startsAtFileBoundary;

        public bool Process(int codeUnit)
        {
            if (codeUnit == 0)
            {
                var match = IsMatch();
                value.Clear();
                hasLeftBoundary = true;
                return match;
            }

            if (codeUnit >= 0x20 && codeUnit <= 0x7e && hasLeftBoundary)
            {
                if (value.Length < 6)
                {
                    value.Append((char)codeUnit);
                }
                else
                {
                    value.Clear();
                    hasLeftBoundary = false;
                }
            }
            else
            {
                value.Clear();
                hasLeftBoundary = false;
            }
            return false;
        }

        public bool IsMatchAtFileEnd() => hasLeftBoundary && IsMatch();

        private bool IsMatch() =>
            value.Length >= 3
            && value.Length <= 5
            && (value[0] == 'X' || value[0] == 'x')
            && value[1] == ':'
            && value[2] == '\\';
    }

    public static string FindToken(
        string path,
        string[] tokens,
        int minimumRunLength,
        string shortDrivePathToken)
    {
        var ascii = new StringBuilder();
        var littleEndian = new[] { new StringBuilder(), new StringBuilder() };
        var bigEndian = new[] { new StringBuilder(), new StringBuilder() };
        var asciiShortPath = new NullBoundedShortPath(true);
        var littleEndianShortPath = new[]
        {
            new NullBoundedShortPath(true),
            new NullBoundedShortPath(false),
        };
        var bigEndianShortPath = new[]
        {
            new NullBoundedShortPath(true),
            new NullBoundedShortPath(false),
        };
        long index = 0;
        int previous = -1;

        using (var stream = File.OpenRead(path))
        {
            int current;
            while ((current = stream.ReadByte()) >= 0)
            {
                if (shortDrivePathToken != null && asciiShortPath.Process(current))
                {
                    return shortDrivePathToken;
                }

                var match = AppendOrFlush(ascii, current >= 0x20 && current <= 0x7e ? (char?)current : null, tokens, minimumRunLength);
                if (match != null) return match;

                if (previous >= 0)
                {
                    var alignment = (int)((index - 1) & 1);
                    if (shortDrivePathToken != null
                        && (littleEndianShortPath[alignment].Process(previous | (current << 8))
                            || bigEndianShortPath[alignment].Process((previous << 8) | current)))
                    {
                        return shortDrivePathToken;
                    }

                    match = AppendOrFlush(
                        littleEndian[alignment],
                        previous >= 0x20 && previous <= 0x7e && current == 0 ? (char?)previous : null,
                        tokens,
                        minimumRunLength);
                    if (match != null) return match;

                    match = AppendOrFlush(
                        bigEndian[alignment],
                        previous == 0 && current >= 0x20 && current <= 0x7e ? (char?)current : null,
                        tokens,
                        minimumRunLength);
                    if (match != null) return match;
                }

                previous = current;
                index++;
            }
        }

        if (shortDrivePathToken != null
            && (asciiShortPath.IsMatchAtFileEnd()
                || (index % 2 == 0 && (littleEndianShortPath[0].IsMatchAtFileEnd()
                    || bigEndianShortPath[0].IsMatchAtFileEnd()))
                || (index % 2 == 1 && (littleEndianShortPath[1].IsMatchAtFileEnd()
                    || bigEndianShortPath[1].IsMatchAtFileEnd()))))
        {
            return shortDrivePathToken;
        }

        var finalMatch = Flush(ascii, tokens, minimumRunLength);
        if (finalMatch != null) return finalMatch;
        foreach (var builder in littleEndian)
        {
            finalMatch = Flush(builder, tokens, minimumRunLength);
            if (finalMatch != null) return finalMatch;
        }
        foreach (var builder in bigEndian)
        {
            finalMatch = Flush(builder, tokens, minimumRunLength);
            if (finalMatch != null) return finalMatch;
        }
        return null;
    }

    private static string AppendOrFlush(
        StringBuilder builder,
        char? value,
        string[] tokens,
        int minimumRunLength)
    {
        if (value.HasValue)
        {
            builder.Append(value.Value);
            return null;
        }
        return Flush(builder, tokens, minimumRunLength);
    }

    private static string Flush(StringBuilder builder, string[] tokens, int minimumRunLength)
    {
        string match = null;
        if (builder.Length >= minimumRunLength)
        {
            var value = builder.ToString();
            foreach (var token in tokens)
            {
                if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = token;
                    break;
                }
            }
        }
        builder.Clear();
        return match;
    }
}
'@
}

$script:TextExtensions = @(
    '.bat', '.cjs', '.config', '.cs', '.csproj', '.js', '.json', '.md',
    '.props', '.ps1', '.targets', '.txt', '.xml'
)

function Test-FileContainsToken {
    param([string]$Path, [string[]]$Tokens)

    if ($script:TextExtensions -contains [IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        $reader = New-Object System.IO.StreamReader($Path, [Text.Encoding]::UTF8, $true)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        foreach ($token in $Tokens) {
            if ($text.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $token
            }
        }
        return $null
    }

    $driveRootToken = @($Tokens | Where-Object {
        $_.Length -eq 3 -and $_[1] -eq ':' -and $_[2] -eq [char]92
    } | Select-Object -First 1)
    $shortDrivePathToken = if ($driveRootToken.Count -eq 1) { $driveRootToken[0] } else { $null }
    return [ReleasePackageBinaryScanner]::FindToken($Path, $Tokens, 6, $shortDrivePathToken)
}

function Assert-NoDeveloperTokens {
    param([System.IO.FileInfo[]]$Files)

    $tokens = @(
        (@('nuaa', '_xuzike') -join ''),
        (([char]68) + ':' + ([char]92) + 'music_bridge'),
        (([char]88) + ':' + ([char]92))
    )

    foreach ($file in $Files) {
        $match = Test-FileContainsToken -Path $file.FullName -Tokens $tokens
        if ($null -ne $match) {
            throw "Developer-specific path or identity found in release input: $($file.FullName)"
        }
    }
}

function Assert-ExtensionReference {
    param([string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Unsafe extension reference path: $RelativePath"
    }

    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($script:CanonicalExtensionRoot, $RelativePath))
    if (-not $candidate.StartsWith($script:CanonicalExtensionPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Extension reference escapes the extension directory: $RelativePath"
    }
    if (-not [IO.File]::Exists($candidate)) {
        throw "Required extension file is missing: $RelativePath"
    }
}

try {
    $script:ResolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).Path
}
catch {
    throw "Release package root does not exist: $PackageRoot"
}

if (-not (Test-Path -LiteralPath $script:ResolvedPackageRoot -PathType Container)) {
    throw "Release package root is not a directory: $script:ResolvedPackageRoot"
}

$script:CanonicalExtensionRoot = [IO.Path]::GetFullPath((Join-Path $script:ResolvedPackageRoot 'extension'))
$script:CanonicalExtensionPrefix = $script:CanonicalExtensionRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'xiaoxu-music-extension'
if (Test-Path -LiteralPath $sourceRoot -PathType Container) {
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot, $PSScriptRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|artifacts|TestResults|\.git)[\\/]'
    })
    Assert-NoDeveloperTokens -Files $sourceFiles
}

$requiredFiles = @(
    'xiaoxu-music-host.exe',
    'D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll',
    'PresentationNative_cor3.dll',
    'vcruntime140_cor3.dll',
    'wpfgfx_cor3.dll',
    'install.bat',
    'uninstall.bat',
    'restart-audio.bat',
    'EXTENSION_ID.txt',
    'README-install.txt',
    'extension\manifest.json',
    'extension\background.js',
    'extension\host-bootstrap.js',
    'extension\content.js',
    'extension\reconnect-policy.js',
    'extension\icon16.png',
    'extension\icon48.png',
    'extension\icon128.png'
)

foreach ($relativePath in $requiredFiles) {
    Assert-FileExists $relativePath
}

$forbiddenFiles = Get-ChildItem -LiteralPath $script:ResolvedPackageRoot -Recurse -File | Where-Object {
    $_.Name -like '*.cs' -or
    $_.Name -like '*.csproj' -or
    $_.Name -like '*.pdb' -or
    $_.Name -like '*.test.cjs'
}
if ($forbiddenFiles) {
    throw "Forbidden source or debug file found in package: $($forbiddenFiles[0].FullName)"
}

$forbiddenDirectories = Get-ChildItem -LiteralPath $script:ResolvedPackageRoot -Recurse -Directory | Where-Object {
    $_.Name -ieq 'host-src'
}
if ($forbiddenDirectories) {
    throw "Forbidden host-src directory found in package: $($forbiddenDirectories[0].FullName)"
}

$manifestPath = Join-Path $script:ResolvedPackageRoot 'extension\manifest.json'
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "extension/manifest.json is not valid JSON: $($_.Exception.Message)"
}

$manifestReferences = @()
if (-not $manifest.background.service_worker) {
    throw 'Manifest background.service_worker is missing.'
}
$manifestReferences += [string]$manifest.background.service_worker
foreach ($contentScript in @($manifest.content_scripts)) {
    foreach ($jsFile in @($contentScript.js)) {
        $manifestReferences += [string]$jsFile
    }
}
foreach ($iconProperty in @($manifest.icons.PSObject.Properties)) {
    $manifestReferences += [string]$iconProperty.Value
}
foreach ($reference in $manifestReferences) {
    Assert-ExtensionReference $reference
}

$backgroundPath = Join-Path $script:ResolvedPackageRoot 'extension\background.js'
$backgroundSource = Get-Content -LiteralPath $backgroundPath -Raw
$importCalls = [regex]::Matches($backgroundSource, 'importScripts\s*\((?<arguments>[^)]*)\)')
foreach ($call in $importCalls) {
    $literalArguments = [regex]::Matches($call.Groups['arguments'].Value, '[''"](?<path>[^''"]+)[''"]')
    foreach ($argument in $literalArguments) {
        Assert-ExtensionReference $argument.Groups['path'].Value
    }
}

$packageFiles = @(Get-ChildItem -LiteralPath $script:ResolvedPackageRoot -Recurse -File)
Assert-NoDeveloperTokens -Files $packageFiles

Write-Output 'PASS: release package is portable.'

if ($SmokeTest) {
    $process = $null
    $httpClient = $null
    try {
        $executable = Join-Path $script:ResolvedPackageRoot 'xiaoxu-music-host.exe'
        $reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        try {
            $reservation.Start()
            $port = ([Net.IPEndPoint]$reservation.LocalEndpoint).Port
        }
        finally {
            $reservation.Stop()
        }

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $executable
        $startInfo.Arguments = '--server'
        $startInfo.WorkingDirectory = $script:ResolvedPackageRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.Environment['XIAOXU_BRIDGE_PORT'] = [string]$port
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'Packaged host process could not be started.'
        }

        $httpClient = New-Object System.Net.Http.HttpClient
        $httpClient.Timeout = [TimeSpan]::FromSeconds(1)
        $healthUri = "http://localhost:$port/health"
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        $health = $null
        while ($null -eq $health) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "Packaged host exited before becoming healthy (exit code $($process.ExitCode))."
            }

            $remaining = $deadline - [DateTime]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) {
                break
            }
            $sleepMilliseconds = [Math]::Floor([Math]::Min(250, $remaining.TotalMilliseconds))
            if ($sleepMilliseconds -gt 0) {
                Start-Sleep -Milliseconds $sleepMilliseconds
            }

            $process.Refresh()
            if ($process.HasExited) {
                throw "Packaged host exited before becoming healthy (exit code $($process.ExitCode))."
            }

            $remaining = $deadline - [DateTime]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) {
                break
            }
            $requestBudget = [TimeSpan]::FromMilliseconds([Math]::Min(1000, $remaining.TotalMilliseconds))
            $cancellation = New-Object System.Threading.CancellationTokenSource
            $response = $null
            try {
                $cancellation.CancelAfter($requestBudget)
                $response = $httpClient.GetAsync($healthUri, $cancellation.Token).GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) {
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $candidate = $body | ConvertFrom-Json
                    if ($candidate.ok -eq $true -and
                        -not [string]::IsNullOrWhiteSpace([string]$candidate.version) -and
                        [int]$candidate.pid -eq $process.Id) {
                        $health = $candidate
                    }
                }
            }
            catch {
                # Retry until the deadline while the launched process remains alive.
            }
            finally {
                if ($null -ne $response) {
                    $response.Dispose()
                }
                $cancellation.Dispose()
            }
        }

        if ($null -eq $health) {
            throw 'Packaged host did not report healthy within 15 seconds.'
        }
        $process.Refresh()
        if ($process.HasExited) {
            throw "Packaged host exited immediately after health check (exit code $($process.ExitCode))."
        }
        Write-Output "PASS: packaged host smoke test succeeded on port $port (pid $($process.Id), version $($health.version))."
    }
    finally {
        if ($null -ne $httpClient) {
            $httpClient.Dispose()
        }
        if ($null -ne $process) {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
            }
            $process.Dispose()
        }
    }
}
