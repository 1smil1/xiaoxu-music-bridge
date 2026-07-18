# Portable GitHub Release Design

## Goal

Provide one GitHub Release ZIP that an ordinary Windows 10/11 x64 user can extract and install from any directory without depending on the developer's machine paths. Keep source code in the repository and runtime binaries in GitHub Releases.

## Current Problems

- `xiaoxu-music-extension/host/Lyrics/LocalLyricService.cs` writes debug logs to `C:\Users\nuaa_xuzike\xiaoxu-debug.log`. That path is compiled into the executable and can make online lyric lookup fail on another computer.
- `xiaoxu-music-extension/restart-audio.bat` launches an executable from `D:\music_bridge\...`, so restart fails outside the developer installation.
- The old ZIP predates the current extension manifest and omits `reconnect-policy.js`.
- `host-src` and `xiaoxu-music-extension/host` have drifted. A release must not accidentally build an obsolete tree.
- The repository has no GitHub Release, while the toolbox currently directs users to the repository homepage.

## Source And Runtime Layout

`xiaoxu-music-extension/host` is the authoritative Host build source. `host-src` is not packaged. During implementation, duplicate source ownership will be removed or made unambiguous so the release script can only build the authoritative project.

The runtime ZIP contains only:

```text
xiaoxu-music-bridge/
  xiaoxu-music-host.exe
  required native runtime DLLs
  install.bat
  uninstall.bat
  restart-audio.bat
  EXTENSION_ID.txt
  README-install.txt
  extension/
    manifest.json
    background.js
    content.js
    injected.js
    reconnect-policy.js
    other manifest/runtime dependencies
    icons
```

Tests, source files, `host-src`, object files, and developer logs are excluded.

## Portability Fixes

- Route logs through the existing `LogPaths` helper under the current user's local application data directory.
- Logging is diagnostic and must not abort lyric retrieval when a log write fails.
- Resolve the Host executable in batch files relative to `%~dp0`, with correct quoting for spaces and non-ASCII paths.
- Keep localhost addresses, Windows registry paths, and standard Chrome installation paths because they are platform locations rather than developer-specific locations.
- Ensure install and uninstall behavior uses per-user locations and does not rely on the extraction drive letter.

## Release Automation

Add a reproducible packaging script and a GitHub Actions workflow triggered by a version tag. The workflow will:

1. Run Host and extension tests.
2. Publish a self-contained `win-x64` Host from the authoritative project.
3. Stage only the approved runtime files.
4. validate the package.
5. Create the ZIP and SHA-256 checksum.
6. Upload both files to a GitHub Release.

The stable asset name will not contain the version, allowing the toolbox to link to:

```text
https://github.com/1smil1/xiaoxu-music-bridge/releases/latest/download/xiaoxu-music-bridge-windows-x64.zip
```

GitHub's automatically generated `Source code (zip)` is not the installer and will be documented as such.

## Package Validation

Validation fails the release when:

- a manifest-referenced extension file is absent;
- a required installer or runtime file is absent;
- source/development files are present in the runtime ZIP;
- text files or executable strings contain known personal paths, usernames, repository drive paths, or build workspace paths;
- the Host cannot start from an extracted directory containing spaces and Chinese characters;
- the local health endpoint does not respond during the smoke test.

The smoke test starts the packaged executable, queries its health endpoint, and always terminates the process it created. It must not depend on an already installed Host.

## User Documentation

The repository README will lead with an ordinary-user section:

1. Download the Windows package from Releases.
2. Extract the entire ZIP.
3. Run `install.bat`.
4. Load the extracted `extension` directory in Chrome's extension page.
5. Open the toolbox website and verify Host status.

Developer build instructions remain in a separate section. The toolbox page gets a primary direct-download action, a secondary source-code link, supported Windows architecture text, and the same concise installation steps.

## Compatibility And Non-Goals

- Existing GSMTC, Win32 fallback, lyrics, cover, audio frame, Lively Wallpaper, and listen-together behavior remains unchanged.
- The release targets Windows x64 only in this change.
- The executable is not committed to Git history.
- Chrome Web Store publication and code signing are outside this change.

## Acceptance Criteria

- No developer-specific absolute path affects runtime behavior or appears in the shipped package.
- A clean machine can install from an arbitrary extraction directory using the documented steps.
- The installed Host starts, reports health, and serves current song, lyrics, cover, and audio-frame APIs.
- The extension loads with every manifest dependency present.
- The toolbox download action resolves to the latest GitHub Release runtime ZIP.
- The GitHub Release includes the ZIP, checksum, and installation notes.
