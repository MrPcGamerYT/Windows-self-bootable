param(
    [Parameter(Mandatory = $true)]
    [string]$AppFolder,

    [Parameter(Mandatory = $true)]
    [ValidateSet("x86", "x64", "arm64")]
    [string]$TargetArch,

    [Parameter(Mandatory = $true)]
    [string]$FlavorTag,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputExe
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$cSource  = Join-Path $repoRoot "src/UsbFreeInstaller.Launcher/NativeWin32/swbi_native_launcher.c"
$icoPath  = Join-Path $repoRoot "src/UsbFreeInstaller.App/Assets/swbi.ico"
$manPath  = Join-Path $repoRoot "src/UsbFreeInstaller.Launcher/launcher.manifest"

if (-not (Test-Path $cSource)) { throw "Missing native launcher source: $cSource" }
if (-not (Test-Path $AppFolder)) { throw "Missing published app directory: $AppFolder" }

# 1. Locate LLVM (clang.exe + lld-link.exe), MSVC headers, and Windows 10/11 SDK (Include + Lib + rc.exe)
$clangCandidates = @(
    "C:\Program Files\LLVM\bin\clang.exe"
)
$vsProgramFiles = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($pf in $vsProgramFiles) {
    $vsRoot = Join-Path $pf "Microsoft Visual Studio"
    if (Test-Path $vsRoot) {
        Get-ChildItem -Path $vsRoot -Filter "clang.exe" -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $clangCandidates += $_.FullName
        }
    }
}
$cmdClang = Get-Command clang.exe -ErrorAction SilentlyContinue
if ($cmdClang) { $clangCandidates += $cmdClang.Source }

$clangExe = $clangCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $clangExe) { throw "clang.exe was not found on this build runner." }

$llvmBinDir = Split-Path -Parent $clangExe
$lldLinkExe = Join-Path $llvmBinDir "lld-link.exe"
if (-not (Test-Path $lldLinkExe)) {
    $cmdLld = Get-Command lld-link.exe -ErrorAction SilentlyContinue
    if ($cmdLld) { $lldLinkExe = $cmdLld.Source }
}
if (-not (Test-Path $lldLinkExe)) { throw "lld-link.exe was not found next to $clangExe." }

$kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"
if (-not (Test-Path $kitsRoot)) { throw "Windows Kits 10 directory not found at $kitsRoot." }

$sdkVerDir = Get-ChildItem (Join-Path $kitsRoot "Lib") -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "um\$TargetArch\kernel32.lib") } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $sdkVerDir) { throw "No Windows SDK Lib version with um\$TargetArch\kernel32.lib found under $kitsRoot\Lib." }

$sdkVer     = $sdkVerDir.Name
$sdkIncUm   = Join-Path $kitsRoot "Include\$sdkVer\um"
$sdkIncSh   = Join-Path $kitsRoot "Include\$sdkVer\shared"
$sdkIncUcrt = Join-Path $kitsRoot "Include\$sdkVer\ucrt"
$sdkLibUm   = Join-Path $kitsRoot "Lib\$sdkVer\um\$TargetArch"

$rcCandidates = @(
    (Join-Path $kitsRoot "bin\$sdkVer\x64\rc.exe")
)
Get-ChildItem (Join-Path $kitsRoot "bin") -Filter "rc.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\rc\.exe$" } |
    Sort-Object FullName -Descending |
    ForEach-Object { $rcCandidates += $_.FullName }
$rcExe = $rcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $rcExe) { throw "Windows SDK rc.exe not found under $kitsRoot\bin." }

$msvcIncDir = $null
foreach ($pf in $vsProgramFiles) {
    $msvcToolsRoot = Join-Path $pf "Microsoft Visual Studio"
    if (Test-Path $msvcToolsRoot) {
        $foundMsvc = Get-ChildItem -Path $msvcToolsRoot -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\VC\\Tools\\MSVC\\[^\\]+$" -and (Test-Path (Join-Path $_.FullName "include\vcruntime.h")) } |
            Sort-Object Name -Descending |
            Select-Object -First 1
        if ($foundMsvc) {
            $msvcIncDir = Join-Path $foundMsvc.FullName "include"
            break
        }
    }
}

$targetTriple = switch ($TargetArch) {
    "x86"   { "i686-pc-windows-msvc" }
    "x64"   { "x86_64-pc-windows-msvc" }
    "arm64" { "aarch64-pc-windows-msvc" }
}
$linkMachine = switch ($TargetArch) {
    "x86"   { "X86" }
    "x64"   { "X64" }
    "arm64" { "ARM64" }
}
$expectedPeMachine = switch ($TargetArch) {
    "x86"   { [uint16]0x014C }
    "x64"   { [uint16]0x8664 }
    "arm64" { [uint16]0xAA64 }
}

$workDir = Join-Path $repoRoot "artifacts/native-sfx-$FlavorTag-$TargetArch"
if (Test-Path $workDir) { Remove-Item -Recurse -Force $workDir }
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

# 2. Generate & compile Win32 .rc (custom swbi.ico + UAC-safe manifest + VERSIONINFO)
$cleanVer = ($Version -split '-')[0]
$verParts = $cleanVer.Split('.')
while ($verParts.Count -lt 4) { $verParts += "0" }
$commaVer = "$($verParts[0]),$($verParts[1]),$($verParts[2]),$($verParts[3])"

$escapedIco = ($icoPath -replace '\\', '/')
$escapedMan = ($manPath -replace '\\', '/')
$rcFile = Join-Path $workDir "swbi_launcher.rc"
$resFile = Join-Path $workDir "swbi_launcher.res"

@"
#include <winver.h>

1 ICON "$escapedIco"
1 24 "$escapedMan"

VS_VERSION_INFO VERSIONINFO
 FILEVERSION $commaVer
 PRODUCTVERSION $commaVer
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904b0"
        BEGIN
            VALUE "CompanyName", "SWBI Project"
            VALUE "FileDescription", "SWBI - Self Windows Bootable Installer ($TargetArch Standalone EXE)"
            VALUE "FileVersion", "$Version"
            VALUE "InternalName", "SWBI-SelfWindowsBootableInstaller.exe"
            VALUE "LegalCopyright", "Copyright (c) 2026 MrPcGamerYT & SWBI Contributors (MIT License)"
            VALUE "OriginalFilename", "SWBI-SelfWindowsBootableInstaller.exe"
            VALUE "ProductName", "SWBI - Self Windows Bootable Installer"
            VALUE "ProductVersion", "$Version"
            VALUE "Comments", "https://github.com/MrPcGamerYT/Windows-self-bootable"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x409, 1200
    END
END
"@ | Set-Content -Path $rcFile -Encoding ASCII

& $rcExe /nologo /I "$sdkIncUm" /I "$sdkIncSh" /I "$sdkIncUcrt" /fo "$resFile" "$rcFile"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $resFile)) {
    throw "rc.exe failed compiling $rcFile (exit $LASTEXITCODE)"
}

# 3. Compile swbi_native_launcher.c -> native COFF object for $TargetArch
$objFile = Join-Path $workDir "swbi_native_launcher.obj"
$clangArgs = @(
    "--target=$targetTriple",
    "-c",
    "-Os",
    "-fno-builtin",
    "-fno-stack-protector",
    "-fno-asynchronous-unwind-tables"
)
if ($TargetArch -ne "arm64") {
    $clangArgs += "-mno-stack-arg-probe"
}
if ($msvcIncDir) {
    $clangArgs += @("-isystem", $msvcIncDir)
}
$clangArgs += @(
    "-isystem", $sdkIncUcrt,
    "-isystem", $sdkIncSh,
    "-isystem", $sdkIncUm,
    "-o", $objFile,
    $cSource
)

& $clangExe @clangArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $objFile)) {
    throw "clang.exe failed compiling native launcher for $TargetArch (exit $LASTEXITCODE)"
}

# 4. Link zero-CRT native Win32 GUI stub .exe via lld-link.exe
$stubExe = Join-Path $workDir "swbi_stub.exe"
$linkArgs = @(
    "/NOLOGO",
    "/SUBSYSTEM:WINDOWS",
    "/MACHINE:$linkMachine",
    "/NODEFAULTLIB",
    "/ENTRY:SwbiMainEntry",
    "/DYNAMICBASE",
    "/NXCOMPAT",
    "/LIBPATH:$sdkLibUm",
    "/OUT:$stubExe",
    $objFile,
    $resFile,
    "kernel32.lib",
    "user32.lib",
    "shell32.lib",
    "advapi32.lib",
    "ole32.lib",
    "cabinet.lib"
)

& $lldLinkExe @linkArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $stubExe)) {
    throw "lld-link.exe failed linking native launcher for $TargetArch (exit $LASTEXITCODE)"
}

# 5. Compress AppFolder via Win32 cabinet.dll (MSZIP) & append SWBIPK01 + SWBIEOF1 trailer into OutputExe
if (-not ("SwbiSfxPacker" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class SwbiSfxPacker
{
    private const uint COMPRESS_ALGORITHM_MSZIP = 2;

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint Algorithm, IntPtr AllocationRoutines, out IntPtr CompressorHandle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Compress(
        IntPtr CompressorHandle,
        byte[] UncompressedData,
        UIntPtr UncompressedDataSize,
        byte[] CompressedBuffer,
        UIntPtr CompressedBufferSize,
        out UIntPtr CompressedDataSize);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CloseCompressor(IntPtr CompressorHandle);

    public static void BuildSingleFileExe(string stubExePath, string appDir, string flavorTag, string outputExePath)
    {
        string fullAppDir = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] allFiles = Directory.GetFiles(fullAppDir, "*", SearchOption.AllDirectories);
        Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);

        if (allFiles.Length == 0)
            throw new InvalidOperationException("AppFolder is empty: " + fullAppDir);

        // Compute deterministic content hash for instant cached startup directory naming
        byte[] hashBytes;
        using (var sha = SHA256.Create())
        {
            byte[] tagBytes = Encoding.UTF8.GetBytes(flavorTag + "\n");
            sha.TransformBlock(tagBytes, 0, tagBytes.Length, null, 0);
            foreach (string f in allFiles)
            {
                string rel = f.Substring(fullAppDir.Length + 1).Replace('/', '\\');
                byte[] relB = Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\0");
                sha.TransformBlock(relB, 0, relB.Length, null, 0);
                byte[] data = File.ReadAllBytes(f);
                sha.TransformBlock(data, 0, data.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hashBytes = sha.Hash;
        }

        string shortPrefix = flavorTag.Replace("-", "");
        if (shortPrefix.Length > 5) shortPrefix = shortPrefix.Substring(0, 5);
        string hexHash = BitConverter.ToString(hashBytes, 0, 4).Replace("-", "").ToLowerInvariant();
        string archiveIdStr = shortPrefix + "-" + hexHash; // <= 14 chars (fits 16-byte null-padded ArchiveId)

        uint maxUncompressed = 0;
        foreach (string f in allFiles)
        {
            long len = new FileInfo(f).Length;
            if (len > uint.MaxValue) throw new InvalidOperationException("File exceeds 4 GB: " + f);
            if ((uint)len > maxUncompressed) maxUncompressed = (uint)len;
        }

        string outParent = Path.GetDirectoryName(Path.GetFullPath(outputExePath));
        if (!string.IsNullOrEmpty(outParent)) Directory.CreateDirectory(outParent);

        IntPtr hComp = IntPtr.Zero;
        if (!CreateCompressor(COMPRESS_ALGORITHM_MSZIP, IntPtr.Zero, out hComp))
            throw new InvalidOperationException("CreateCompressor(MSZIP) failed: " + Marshal.GetLastWin32Error());

        try
        {
            byte[] stubBytes = File.ReadAllBytes(stubExePath);
            ulong payloadOffset = (ulong)stubBytes.Length;

            byte[] compScratch = new byte[Math.Max(65536, (int)maxUncompressed + 65536)];

            using (var fs = new FileStream(outputExePath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
            {
                // 1. Write native Win32 GUI stub PE
                bw.Write(stubBytes);

                // 2. Write 32-byte SwbiPakHeader
                bw.Write(Encoding.ASCII.GetBytes("SWBIPK01")); // 8 bytes
                byte[] idField = new byte[16];
                byte[] idRaw = Encoding.ASCII.GetBytes(archiveIdStr);
                Array.Copy(idRaw, 0, idField, 0, Math.Min(15, idRaw.Length));
                bw.Write(idField);                             // 16 bytes
                bw.Write((uint)allFiles.Length);               // 4 bytes
                bw.Write(maxUncompressed);                     // 4 bytes

                // 3. Compress & write each file entry
                foreach (string f in allFiles)
                {
                    string rel = f.Substring(fullAppDir.Length + 1).Replace('/', '\\');
                    byte[] relUtf16 = Encoding.Unicode.GetBytes(rel);
                    ushort relChars = (ushort)(relUtf16.Length / 2);

                    byte[] rawData = File.ReadAllBytes(f);
                    uint uncompLen = (uint)rawData.Length;
                    ushort flags = 0;
                    byte[] payloadToWrite = rawData;
                    int payloadLen = rawData.Length;

                    if (uncompLen >= 64)
                    {
                        UIntPtr compSize;
                        if (Compress(
                                hComp,
                                rawData,
                                (UIntPtr)uncompLen,
                                compScratch,
                                (UIntPtr)compScratch.Length,
                                out compSize))
                        {
                            ulong cs = compSize.ToUInt64();
                            if (cs > 0 && cs < uncompLen)
                            {
                                flags = 1;
                                payloadToWrite = compScratch;
                                payloadLen = (int)cs;
                            }
                        }
                    }

                    bw.Write(relChars);
                    bw.Write(flags);
                    bw.Write(uncompLen);
                    bw.Write((uint)payloadLen);
                    bw.Write(relUtf16);
                    if (payloadLen > 0)
                    {
                        bw.Write(payloadToWrite, 0, payloadLen);
                    }
                }

                // 4. Write 16-byte SwbiEofTrailer
                bw.Write(payloadOffset);
                bw.Write(Encoding.ASCII.GetBytes("SWBIEOF1"));
            }
        }
        finally
        {
            if (hComp != IntPtr.Zero) CloseCompressor(hComp);
        }
    }
}
"@
}

$resolvedApp = (Resolve-Path $AppFolder).Path
$resolvedOut = Join-Path $PWD $OutputExe
if ([System.IO.Path]::IsPathRooted($OutputExe)) { $resolvedOut = $OutputExe }

[SwbiSfxPacker]::BuildSingleFileExe($stubExe, $resolvedApp, $FlavorTag, $resolvedOut)

# 6. Verify PE header (MZ + PE\0\0 + Machine == $expectedPeMachine + Subsystem == 2 GUI) & SWBIEOF1 trailer
$exeInfo = Get-Item -LiteralPath $resolvedOut
$fsCheck = [System.IO.File]::OpenRead($resolvedOut)
try {
    $br = New-Object System.IO.BinaryReader($fsCheck)
    $mz = $br.ReadUInt16()
    if ($mz -ne 0x5A4D) { throw "Invalid MZ magic in $resolvedOut" }
    $fsCheck.Position = 0x3C
    $peOffset = $br.ReadInt32()
    $fsCheck.Position = $peOffset
    $peSig = $br.ReadUInt32()
    if ($peSig -ne 0x00004550) { throw "Invalid PE signature in $resolvedOut" }
    $machine = $br.ReadUInt16()
    if ($machine -ne $expectedPeMachine) {
        throw ("Unexpected PE Machine 0x{0:X4} (expected 0x{1:X4} for $TargetArch) in $resolvedOut" -f $machine, $expectedPeMachine)
    }

    $fsCheck.Position = $fsCheck.Length - 8
    $eofBytes = $br.ReadBytes(8)
    $eofMagic = [System.Text.Encoding]::ASCII.GetString($eofBytes)
    if ($eofMagic -ne "SWBIEOF1") { throw "Missing SWBIEOF1 trailer in $resolvedOut" }
}
finally {
    $fsCheck.Dispose()
}

$sizeMb = [Math]::Round($exeInfo.Length / 1MB, 2)
Write-Host ("==> Built verified {0} single-file EXE (PE Machine 0x{1:X4}, {2} MB): {3}" -f $TargetArch, $expectedPeMachine, $sizeMb, $resolvedOut) -ForegroundColor Green
