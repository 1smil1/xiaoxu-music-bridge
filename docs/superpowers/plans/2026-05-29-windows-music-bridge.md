# Windows Music Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows local HTTP bridge that exposes current QQ Music media status and playback controls to `xiaoxu.xin`.

**Architecture:** A .NET 9 Minimal API hosts on `127.0.0.1:17888`. The API depends on a focused media-session abstraction so endpoint behavior can be tested without Windows media sessions, while the production implementation uses Global System Media Transport Controls.

**Tech Stack:** C# 13, .NET 9, ASP.NET Core Minimal API, MSTest, Windows Media Control WinRT APIs.

---

### Task 1: Repository and Project Setup

**Files:**
- Create: `.gitignore`
- Create: `xiaoxu-music-bridge/xiaoxu-music-bridge.csproj`
- Create: `xiaoxu-music-bridge.Tests/xiaoxu-music-bridge.Tests.csproj`
- Create: `xiaoxu-music-bridge.sln`

- [ ] Install .NET SDK 9 if `dotnet --info` reports no SDK.
- [ ] Create a web project for the bridge.
- [ ] Create an MSTest project for unit tests.
- [ ] Add both projects to a solution.
- [ ] Commit setup files.

### Task 2: API Contract Tests

**Files:**
- Create: `xiaoxu-music-bridge.Tests/HealthEndpointTests.cs`
- Create: `xiaoxu-music-bridge.Tests/StatusEndpointTests.cs`
- Create: `xiaoxu-music-bridge.Tests/ControlEndpointTests.cs`
- Modify: `xiaoxu-music-bridge/Program.cs`

- [ ] Write failing endpoint tests for `/health`, `/status`, and control routes using `WebApplicationFactory`.
- [ ] Run tests and verify failures are caused by missing API behavior.
- [ ] Implement minimal API endpoints against injectable services.
- [ ] Run tests and verify they pass.
- [ ] Commit endpoint behavior.

### Task 3: Media Abstractions

**Files:**
- Create: `xiaoxu-music-bridge/Media/MediaStatus.cs`
- Create: `xiaoxu-music-bridge/Media/IMediaSessionService.cs`
- Create: `xiaoxu-music-bridge/Media/ControlResult.cs`
- Modify: API tests as needed.

- [ ] Write tests that define the JSON shape and control result behavior.
- [ ] Add records and interfaces used by the API.
- [ ] Verify all tests pass.
- [ ] Commit media contracts.

### Task 4: Windows Media Session Implementation

**Files:**
- Create: `xiaoxu-music-bridge/Media/WindowsMediaSessionService.cs`
- Modify: `xiaoxu-music-bridge/xiaoxu-music-bridge.csproj`
- Modify: `xiaoxu-music-bridge/Program.cs`

- [ ] Add Windows SDK contract package.
- [ ] Implement status lookup using `GlobalSystemMediaTransportControlsSessionManager`.
- [ ] Prefer QQ Music sessions when identifiable, otherwise choose the current active session.
- [ ] Implement play/pause, next, and previous controls.
- [ ] Return a disconnected/no-media status when no session is available.
- [ ] Commit Windows media implementation.

### Task 5: Cover Endpoint and CORS

**Files:**
- Modify: `xiaoxu-music-bridge/Media/MediaStatus.cs`
- Modify: `xiaoxu-music-bridge/Media/WindowsMediaSessionService.cs`
- Modify: `xiaoxu-music-bridge/Program.cs`

- [ ] Add a cover provider abstraction or method to return current thumbnail bytes.
- [ ] Implement `/cover/current` to return image bytes or 404.
- [ ] Restrict CORS to `http://xiaoxu.xin`, `https://xiaoxu.xin`, `http://localhost:5173`, and `http://127.0.0.1:5173`.
- [ ] Add tests for CORS policy shape where practical.
- [ ] Commit cover and CORS behavior.

### Task 6: Build, Run, and Publish

**Files:**
- Modify: `README.md`

- [ ] Run `dotnet test`.
- [ ] Run the bridge and verify `/health` responds locally.
- [ ] Publish a single-file win-x64 executable.
- [ ] Document how to run and test it.
- [ ] Commit verification and docs.

