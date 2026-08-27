# qq-handles.ps1 — Track A3 (v3.2.7):
# Enumerate kernel handles held by QQMusic.exe.
# Looks for potential player-state IPC candidates (File, Section, ALPC Port,
# FilterCommunicationPort) that the bridge host could read directly.
#
# Usage (run in PowerShell with admin if possible — handle dump needs
# SeDebugPrivilege for cross-process query):
#   .\qq-handles.ps1                       # all QQMusic PIDs, all handle types
#   .\qq-handles.ps1 -Pid 12345            # specific PID
#   .\qq-handles.ps1 -Type Section         # only Section handles
#   .\qq-handles.ps1 -OutputCsv handles.csv
#
# This script uses NtQuerySystemInformation via the .NET InteropServices
# layer — no external dependencies. Categorizes handles by type, prints a
# one-line summary per interesting handle.

[CmdletBinding()]
param(
    [int[]] $Pid = @(),
    [string[]] $Type = @('File', 'Section', 'ALPC', 'FilterComm'),
    [string] $OutputCsv = "",
    [switch] $Brief
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

public static class NtHandles
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public ulong UniqueProcessId;
        public ulong HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
        public IntPtr Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_HANDLE_INFORMATION_EX
    {
        public ulong NumberOfHandles;
        public IntPtr Handles; // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[]
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_BASIC_INFORMATION
    {
        public uint Pad1;
        public IntPtr Pad2;
        public uint Pad3;
        public uint Pad4;
        public IntPtr Pad5;
        public uint HardErrorMode;
        public IntPtr Pad6;
        public uint MaxMuMs;
        public uint MaxKvMs;
        public IntPtr Pad7;
        public uint Pad8;
        public IntPtr Pad9;
        public IntPtr Pad10;
        public IntPtr Pad11;
        public IntPtr Pad12;
        public IntPtr Pad13;
        public IntPtr Pad14;
        public uint PageSize;
        public UIntPtr MinimumUserModeAddress;
        public UIntPtr MaximumUserModeAddress;
        public IntPtr ActiveProcessorsAffinityMask;
        public byte NumberOfProcessors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int infoClass, IntPtr info, uint infoLen, out uint returnLen);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, uint infoLen, out uint returnLen);

    [DllImport("kernel32.dll")]
    public static extern bool DuplicateHandle(IntPtr sourceProcessHandle, IntPtr sourceHandle, IntPtr targetProcessHandle, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

    public const int SystemHandleInformationEx = 64;
    public const int ObjectNameInformation = 1;
    public const int ObjectTypeInformation = 2;

    public static List<Tuple<string, string, string, ulong, ulong, uint>> QueryHandlesForProcess(int targetPid)
    {
        var result = new List<Tuple<string, string, string, ulong, ulong, uint>>();

        uint len = 0;
        NtQuerySystemInformation(SystemHandleInformationEx, IntPtr.Zero, 0, out len);
        if (len == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            int status = NtQuerySystemInformation(SystemHandleInformationEx, buffer, len, out len);
            if (status != 0) return result;

            long count = Marshal.ReadInt64(buffer);
            IntPtr entriesPtr = IntPtr.Add(buffer, 8);
            int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

            IntPtr selfProc = System.Diagnostics.Process.GetCurrentProcess().Handle;
            IntPtr clientProc = (IntPtr)(-1); // current process pseudo-handle

            for (long i = 0; i < count; i++)
            {
                IntPtr entryPtr = IntPtr.Add(entriesPtr, i * entrySize);
                var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);
                if ((int)entry.UniqueProcessId != targetPid) continue;
                if (entry.HandleValue == 0 || entry.HandleValue.ToInt64() <= 4) continue;

                // Best-effort name lookup
                string name = "";
                string typeName = "";
                IntPtr dup = IntPtr.Zero;
                if (DuplicateHandle((IntPtr)entry.UniqueProcessId, (IntPtr)entry.HandleValue, selfProc, out dup, 0, false, 0x0002 /* DUPLICATE_SAME_ACCESS */))
                {
                    try
                    {
                        uint nameLen = 1024;
                        IntPtr nameBuf = Marshal.AllocHGlobal((int)nameLen);
                        try
                        {
                            status = NtQueryObject(dup, ObjectNameInformation, nameBuf, nameLen, out nameLen);
                            if (status == 0)
                            {
                                var us = Marshal.PtrToStructure<UNICODE_STRING>(nameBuf);
                                if (us.Buffer != IntPtr.Zero && us.Length > 0)
                                {
                                    byte[] bytes = new byte[us.Length];
                                    Marshal.Copy(us.Buffer, bytes, 0, us.Length);
                                    name = Encoding.Unicode.GetString(bytes);
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(nameBuf); }

                        uint typeLen = 1024;
                        IntPtr typeBuf = Marshal.AllocHGlobal((int)typeLen);
                        try
                        {
                            status = NtQueryObject(dup, ObjectTypeInformation, typeBuf, typeLen, out typeLen);
                            if (status == 0)
                            {
                                // The type name is at offset 0x60 in the public type buffer for current OS
                                IntPtr namePtr = IntPtr.Add(typeBuf, 0x60);
                                var us = Marshal.PtrToStructure<UNICODE_STRING>(namePtr);
                                if (us.Buffer != IntPtr.Zero && us.Length > 0)
                                {
                                    byte[] bytes = new byte[us.Length];
                                    Marshal.Copy(us.Buffer, bytes, 0, us.Length);
                                    typeName = Encoding.Unicode.GetString(bytes);
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(typeBuf); }
                    }
                    finally
                    {
                        // Close the duplicate handle
                        NativeMethods.CloseHandle(dup);
                    }
                }

                result.Add(Tuple.Create(typeName, name, "", entry.UniqueProcessId, entry.HandleValue, entry.GrantedAccess));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
}

public static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
}
"@

if (-not $Pid -or $Pid.Count -eq 0) {
    Write-Host "Looking up QQMusic.exe PIDs..." -ForegroundColor Cyan
    $procs = Get-Process QQMusic -ErrorAction SilentlyContinue
    if (-not $procs) {
        Write-Host "QQMusic.exe is not running. Start it and re-run this script." -ForegroundColor Yellow
        exit 0
    }
    $Pid = @($procs | Select-Object -ExpandProperty Id)
}

$rows = @()
foreach ($p in $Pid) {
    Write-Host "Querying PID $p..." -ForegroundColor Cyan
    try {
        $rows += NtHandles::QueryHandlesForProcess($p)
    } catch {
        Write-Host "Query failed: $_" -ForegroundColor Red
    }
}

if (-not $rows -or $rows.Count -eq 0) {
    Write-Host "No handles found. Try running as Administrator." -ForegroundColor Yellow
    exit 0
}

# Filter by type
$interestingTypes = @{
    "File"            = @("Pipe", "QQMusic", ".lrc", "lyric", ".mp3", ".flac", "Player")
    "Section"         = @("QQMusic", "MolePlugin", "Player", "BaseUI")
    "ALPC Port"       = @("QQMusic", "QQMusicSvr")
    "FilterComm Port" = @("QQMusic")
}

$filtered = foreach ($r in $rows) {
    if (-not $Type -or $Type.Count -eq 0) { $_; continue }
    $matches = $false
    foreach ($t in $Type) {
        $pattern = $interestingTypes[$t]
        if (-not $pattern) { $matches = $true; break }
        if ($r.Item1 -like "$t*") { $matches = $true; break }
        foreach ($p in $pattern) {
            if ($r.Item2 -like "*$p*") { $matches = $true; break }
        }
        if ($matches) { break }
    }
    if ($matches) { $r }
}

Write-Host ""
Write-Host "=== QQ Music handle scan ===" -ForegroundColor Green
Write-Host ("PID filter: {0}" -f ($Pid -join ", "))
Write-Host ("Type filter: {0}" -f ($Type -join ", "))
Write-Host ("Total handles scanned: {0}" -f $rows.Count)
Write-Host ("Interesting after filter: {0}" -f ($filtered.Count))
Write-Host ""

if ($OutputCsv) {
    $filtered | ForEach-Object {
        [PSCustomObject]@{
            TypeName = $_.Item1
            Name     = $_.Item2
            Pid      = $_.Item4
            HandleHex = ("0x{0:X}" -f [uint64]$_.Item5)
            GrantedAccess = ("0x{0:X}" -f $_.Item6)
        }
    } | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding UTF8
    Write-Host "Saved to $OutputCsv" -ForegroundColor Green
}

# Print interesting hits
$grouped = $filtered | Group-Object -Property Item1
foreach ($g in $grouped) {
    Write-Host "[$($g.Name)] $($g.Count) handle(s)" -ForegroundColor Magenta
    foreach ($r in $g.Group) {
        if ($Brief -and $r.Item2.Length -gt 100) {
            $short = $r.Item2.Substring(0, 100) + "...(truncated)"
            Write-Host ("  pid={0,-6} hwnd=0x{1:X8} name={2}" -f $r.Item4, [uint64]$r.Item5, $short)
        } else {
            Write-Host ("  pid={0,-6} hwnd=0x{1:X8} name={2}" -f $r.Item4, [uint64]$r.Item5, $r.Item2)
        }
    }
    Write-Host ""
}

Write-Host "Done. Look for:" -ForegroundColor Yellow
Write-Host "  - Section handles named 'QQMusicPlayer'/'QQMusicMiniPlayer' — could expose player state struct" -ForegroundColor Yellow
Write-Host "  - NamedPipe named '\.\pipe\QQMusic*' — could be an undiscovered IPC channel" -ForegroundColor Yellow
Write-Host "  - ALPC Port objects — uncommon but QQ Music uses them for some subsystems" -ForegroundColor Yellow
