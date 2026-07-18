# Portable GitHub Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce and publish a path-independent Windows x64 runtime ZIP containing the current Host and complete Chrome extension.

**Architecture:** Build only `xiaoxu-music-extension/host`, stage an allowlisted runtime tree, and reject packages containing missing dependencies or developer-specific paths. GitHub Actions reproduces the same local packaging command and uploads a stable-named ZIP plus SHA-256 checksum on version tags.

**Tech Stack:** .NET 9, C#, PowerShell 7, Windows batch, Chrome Manifest V3, GitHub Actions, GitHub CLI

---

### Task 1: Make lyric diagnostics path-independent

**Files:**
- Modify: `xiaoxu-music-extension/host/Lyrics/LocalLyricService.cs`
- Modify: `xiaoxu-music-extension/host.Tests/Program.cs`

- [ ] **Step 1: Add a failing policy test for the debug path**

Add this test beside the existing log rotation test:

```csharp
Run("debug log stays under current local application data", () =>
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    Equal(true, Path.GetFullPath(LogPaths.DebugLog).StartsWith(Path.GetFullPath(localAppData), StringComparison.OrdinalIgnoreCase));
    Equal(false, LogPaths.DebugLog.Contains("nuaa_xuzike", StringComparison.OrdinalIgnoreCase));
});
```

- [ ] **Step 2: Run the Host policy tests**

Run: `dotnet run --project .\xiaoxu-music-extension\host.Tests\xiaoxu-music-host.Tests.csproj -c Release`

Expected: the existing test suite passes; the source portability scan added in Task 3 will initially fail on `LocalLyricService.cs`.

- [ ] **Step 3: Replace direct log writes with the safe logger**

Add `using xiaoxu_music_bridge.Common;`, remove the literal `logPath`, and replace all three writes with:

```csharp
LogPaths.SafeAppend(
    LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] Lyrics: QQ result={(onlineLyrics is not null ? "FOUND" : "null")}\n");
```

Apply the same call for Netease and LRCLIB. Diagnostic failures are then swallowed by the existing `SafeAppend` implementation and cannot abort lyric lookup.

- [ ] **Step 4: Run Host tests and inspect the source**

Run:

```powershell
dotnet run --project .\xiaoxu-music-extension\host.Tests\xiaoxu-music-host.Tests.csproj -c Release
rg -n "nuaa_xuzike|C:\\Users\\|File\.AppendAllText" .\xiaoxu-music-extension\host
```

Expected: tests print `PASS`; `rg` returns no matches for the removed unsafe lyric logging.

- [ ] **Step 5: Commit the logging fix**

```powershell
git add xiaoxu-music-extension/host/Lyrics/LocalLyricService.cs xiaoxu-music-extension/host.Tests/Program.cs
git commit -m "fix: make lyric diagnostics portable"
```

### Task 2: Make recovery and installer files relocation-safe

**Files:**
- Modify: `xiaoxu-music-extension/restart-audio.bat`
- Modify: `xiaoxu-music-extension/install.bat`
- Modify: `xiaoxu-music-extension/uninstall.bat`
- Delete: `xiaoxu-music-extension/install.ps1`
- Delete: `host-src/`

- [ ] **Step 1: Add required-file checks to `install.bat`**

Before registry changes, verify the complete runtime set:

```bat
for %%F in (background.js content.js reconnect-policy.js manifest.json icon16.png icon48.png icon128.png) do (
    if not exist "%INSTALL_DIR%\extension\%%F" (
        echo  [ERROR] extension\%%F not found
        pause
        exit /b 1
    )
)
```

- [ ] **Step 2: Resolve recovery Host relative to the script**

Replace the hardcoded restart block with:

```bat
set "HOST_EXE=%~dp0xiaoxu-music-host.exe"
if exist "%HOST_EXE%" (
  start "" "%HOST_EXE%" --server
  echo         host relaunched
) else (
  echo         [WARN] xiaoxu-music-host.exe not found beside this script
)
```

- [ ] **Step 3: Keep uninstall scoped to known package files**

Update `uninstall.bat` to remove `restart-audio.bat`, runtime DLLs, generated native manifest, and the extension directory by explicit names. Do not recursively delete an arbitrary extraction parent.

- [ ] **Step 4: Remove obsolete build/install ownership**

Delete `host-src` and `install.ps1`. The former is an incomplete source mirror; the latter compiles on the end user's machine and conflicts with the prebuilt runtime package. `xiaoxu-music-extension/host` remains the only build source and `install.bat` remains the runtime installer.

- [ ] **Step 5: Scan scripts for developer paths**

Run:

```powershell
rg -n "nuaa_xuzike|D:\\music_bridge|X:\\|C:\\Users\\" xiaoxu-music-extension -g '*.bat' -g '*.ps1'
```

Expected: no developer-specific path matches. Standard Chrome paths under `C:\Program Files` remain.

- [ ] **Step 6: Commit installer portability**

```powershell
git add -A host-src xiaoxu-music-extension/install.bat xiaoxu-music-extension/install.ps1 xiaoxu-music-extension/restart-audio.bat xiaoxu-music-extension/uninstall.bat
git commit -m "fix: make runtime scripts relocation-safe"
```

### Task 3: Add deterministic package construction and validation

**Files:**
- Create: `scripts/Test-ReleasePackage.ps1`
- Create: `scripts/Build-Release.ps1`
- Create: `xiaoxu-music-extension/README-install.txt`
- Modify: `.gitignore`

- [ ] **Step 1: Write the failing package validator**

Create `Test-ReleasePackage.ps1` with mandatory `PackageRoot`, an optional `-SmokeTest`, and these checks:

```powershell
$required = @(
  'xiaoxu-music-host.exe', 'install.bat', 'uninstall.bat',
  'restart-audio.bat', 'EXTENSION_ID.txt', 'README-install.txt',
  'extension/manifest.json', 'extension/background.js',
  'extension/content.js', 'extension/reconnect-policy.js',
  'extension/icon16.png', 'extension/icon48.png', 'extension/icon128.png'
)
$forbidden = @('nuaa_xuzike', 'D:\music_bridge', 'X:\')
```

Parse `manifest.json`, verify its service worker, content scripts, and icon paths exist, reject `*.cs`, `*.csproj`, `*.pdb`, `host-src`, and `*.test.cjs`, and scan all text plus ASCII/UTF-16 representations of the EXE for forbidden strings. Throw on any violation and print `PASS: release package is portable` only after all checks pass.

- [ ] **Step 2: Run the validator against the source folder to prove failure**

Run: `pwsh -File .\scripts\Test-ReleasePackage.ps1 -PackageRoot .\xiaoxu-music-extension`

Expected: FAIL because no packaged EXE exists and source/test files are present.

- [ ] **Step 3: Implement the packaging script**

`Build-Release.ps1` accepts `-Configuration Release`, `-Runtime win-x64`, and `-SmokeTest`. It must:

```powershell
dotnet publish $hostProject -c $Configuration -r $Runtime --self-contained true `
  /p:PublishSingleFile=true /p:DebugType=None /p:DebugSymbols=false -o $publishDir
```

Then create `artifacts/xiaoxu-music-bridge`, copy publish output excluding PDB files, copy only the approved scripts/text files, and copy extension runtime files selected by the manifest. Invoke `Test-ReleasePackage.ps1`, create `artifacts/xiaoxu-music-bridge-windows-x64.zip`, and write `artifacts/xiaoxu-music-bridge-windows-x64.zip.sha256` with `Get-FileHash`.

- [ ] **Step 4: Add end-user installation text**

`README-install.txt` states: fully extract the ZIP, run `install.bat`, load the `extension` folder from `chrome://extensions`, do not move/delete the folder without reinstalling, and use `uninstall.bat` before deletion.

- [ ] **Step 5: Ignore generated artifacts**

Add `artifacts/` to `.gitignore`; do not ignore scripts or the installation text.

- [ ] **Step 6: Build and validate from a Unicode path**

Run:

```powershell
pwsh -File .\scripts\Build-Release.ps1
$testRoot = Join-Path $env:TEMP '小续 音乐桥接测试'
Expand-Archive .\artifacts\xiaoxu-music-bridge-windows-x64.zip $testRoot -Force
pwsh -File .\scripts\Test-ReleasePackage.ps1 -PackageRoot "$testRoot\xiaoxu-music-bridge"
```

Expected: the build exits 0 and both validator runs print `PASS: release package is portable`.

- [ ] **Step 7: Commit release tooling**

```powershell
git add .gitignore scripts xiaoxu-music-extension/README-install.txt
git commit -m "build: add portable Windows release package"
```

### Task 4: Add release metadata and automation

**Files:**
- Modify: `xiaoxu-music-extension/extension/manifest.json`
- Modify: `xiaoxu-music-extension/host/Bridge/BridgeHttpServer.cs`
- Modify: `README.md`
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Set version 3.5.0 consistently**

Change the manifest version and `HostVersion` constant to `3.5.0`. Verify:

```powershell
rg -n '3\.4\.0|3\.5\.0' xiaoxu-music-extension
```

Expected: runtime version locations contain `3.5.0` and no runtime location contains `3.4.0`.

- [ ] **Step 2: Replace README with user-first instructions**

Lead with the stable Release asset URL, explicitly warn that GitHub's `Source code (zip)` has no EXE, list the five installation steps, supported Windows x64 requirements, runtime APIs, developer test/build commands, and release command. Point developer builds only at `xiaoxu-music-extension/host/xiaoxu-music-host.csproj`.

- [ ] **Step 3: Add the tag release workflow**

Create a Windows workflow triggered by `v*` tags with `contents: write`. Its release steps are:

```yaml
- uses: actions/checkout@v4
- uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '9.0.x'
- shell: pwsh
  run: dotnet run --project .\xiaoxu-music-extension\host.Tests\xiaoxu-music-host.Tests.csproj -c Release
- shell: pwsh
  run: .\scripts\Build-Release.ps1 -SmokeTest
- env:
    GH_TOKEN: ${{ github.token }}
  shell: pwsh
  run: gh release create $env:GITHUB_REF_NAME --generate-notes .\artifacts\xiaoxu-music-bridge-windows-x64.zip .\artifacts\xiaoxu-music-bridge-windows-x64.zip.sha256
```

- [ ] **Step 4: Validate workflow and all tests**

Run:

```powershell
dotnet run --project .\xiaoxu-music-extension\host.Tests\xiaoxu-music-host.Tests.csproj -c Release
node .\xiaoxu-music-extension\extension\bridge-binary.test.cjs
node .\xiaoxu-music-extension\extension\host-bootstrap.test.cjs
node .\xiaoxu-music-extension\extension\reconnect-policy.test.cjs
pwsh -File .\scripts\Build-Release.ps1 -SmokeTest
git diff --check
```

Expected: every test exits 0, health smoke test succeeds, package validation passes, and `git diff --check` is silent.

- [ ] **Step 5: Commit release automation**

```powershell
git add README.md .github/workflows/release.yml xiaoxu-music-extension/extension/manifest.json xiaoxu-music-extension/host/Bridge/BridgeHttpServer.cs
git commit -m "ci: publish Windows runtime releases"
```

### Task 5: Publish and verify GitHub Release

**Files:**
- No source files created

- [ ] **Step 1: Push the implementation branch and merge it to `main`**

Run the repository's normal non-interactive merge/push flow after reviewing the complete diff and confirming all checks pass.

- [ ] **Step 2: Create and push the release tag**

```powershell
git tag -a v3.5.0 -m "xiaoxu-music-bridge v3.5.0"
git push origin v3.5.0
```

- [ ] **Step 3: Wait for the release workflow**

Run: `gh run watch --exit-status`

Expected: the tagged release workflow completes successfully.

- [ ] **Step 4: Verify public assets**

Run:

```powershell
gh release view v3.5.0
Invoke-WebRequest 'https://github.com/1smil1/xiaoxu-music-bridge/releases/latest/download/xiaoxu-music-bridge-windows-x64.zip' -OutFile "$env:TEMP\xiaoxu-release.zip"
Get-FileHash "$env:TEMP\xiaoxu-release.zip" -Algorithm SHA256
```

Expected: Release lists the ZIP and checksum; the stable URL returns HTTP 200; the local hash equals the published checksum.
