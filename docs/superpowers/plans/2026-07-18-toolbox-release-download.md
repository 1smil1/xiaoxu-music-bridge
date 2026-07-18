# Toolbox Release Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the toolbox send ordinary users directly to the verified Windows runtime package and give accurate installation guidance.

**Architecture:** Keep the GitHub Release as the single binary distribution source. The tools page uses a stable latest-release asset URL for its primary command and retains the repository only as a secondary source-code destination.

**Tech Stack:** Next.js 16, React 19, TypeScript, Tailwind CSS, Lucide React

---

### Task 1: Add direct download and correct installation copy

**Files:**
- Modify: `app/tools/page.tsx`

- [ ] **Step 1: Define stable release links**

Add module constants:

```tsx
const bridgeDownloadUrl =
  "https://github.com/1smil1/xiaoxu-music-bridge/releases/latest/download/xiaoxu-music-bridge-windows-x64.zip";
const bridgeRepositoryUrl = "https://github.com/1smil1/xiaoxu-music-bridge";
```

- [ ] **Step 2: Update the installation sequence**

The first step must say “下载 Windows 安装包” and instruct users to extract the whole ZIP. Keep `install.bat` and Chrome unpacked-extension steps. State that installation starts the Host and registers it for login; the extension reconnects to the same local Host.

- [ ] **Step 3: Replace the primary repository button**

Use Lucide `Download` for the primary command:

```tsx
<a href={bridgeDownloadUrl} className="...">
  <Download className="w-5 h-5" />
  下载 Windows 安装包
</a>
```

Place a quieter `Github` icon link beside or below it for “查看源代码”. Add visible support text `Windows 10/11 x64` and `当前版本：3.5.0`.

- [ ] **Step 4: Correct FAQ claims**

Explain that moving the extracted directory requires rerunning `install.bat`, the extension is loaded from that directory, and users must download the Release asset rather than `Source code (zip)`. Keep uninstall instructions scoped to the package's `uninstall.bat`.

- [ ] **Step 5: Run static checks**

Run:

```powershell
npm run lint
npm run build
```

Expected: both commands exit 0 and `/tools` is included in the successful Next.js build.

- [ ] **Step 6: Verify the download target**

Run:

```powershell
$response = Invoke-WebRequest -Method Head 'https://github.com/1smil1/xiaoxu-music-bridge/releases/latest/download/xiaoxu-music-bridge-windows-x64.zip'
$response.StatusCode
```

Expected: `200` after the bridge release plan is complete.

- [ ] **Step 7: Commit the toolbox update**

```powershell
git add app/tools/page.tsx
git commit -m "docs: link toolbox to bridge release"
```

### Task 2: Browser verification and deployment

**Files:**
- No additional source files expected

- [ ] **Step 1: Start the local production server**

Run: `npm run start -- --hostname 127.0.0.1 --port 3010`

Expected: Next.js reports `http://127.0.0.1:3010` ready.

- [ ] **Step 2: Inspect `/tools` at desktop and mobile widths**

Use Chrome DevTools MCP at 1440x900 and 390x844. Verify the download command is visible, text does not overlap, the primary link resolves to the Release ZIP, and the source link resolves to the repository.

- [ ] **Step 3: Push and deploy through the repository's existing production process**

Push the tested commit to its normal upstream branch, then verify `https://xiaoxu.xin/tools` shows the same download URL and installation instructions.
