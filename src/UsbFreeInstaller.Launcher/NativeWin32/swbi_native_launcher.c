/*
 * SWBI — Self Windows Bootable Installer
 * Universal Zero-CRT Native Win32 Single-File SFX Bootstrapper (x86, x64, ARM64)
 *
 * Compiles directly to native machine code for win-x86 (32-bit), win-x64 (64-bit),
 * and win-arm64 (ARM64) with /NODEFAULTLIB — importing ONLY core Windows OS DLLs
 * present in C:\Windows\System32 on every Windows 10 & Windows 11 installation:
 *   kernel32.dll, user32.dll, shell32.dll, advapi32.dll, ole32.dll, cabinet.dll
 *
 * Immune to:
 *   - Broken 8.3 %TEMP% paths (e.g. 'C:\Users\ZLAG~1\AppData\Local\Temp')
 *   - Corrupted or disabled UAC / Appinfo service on stripped/modded Windows builds
 *   - Missing Visual C++ Redistributable or missing .NET runtime on host OS
 */

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <compressapi.h>

/* Compiler-required freestanding memory primitives under /NODEFAULTLIB */
#if defined(__clang__) || defined(__GNUC__)
__attribute__((used, noinline))
#endif
void* __cdecl memset(void* dst, int val, size_t count)
{
    volatile unsigned char* p = (volatile unsigned char*)dst;
    unsigned char b = (unsigned char)val;
    while (count-- > 0)
    {
        *p++ = b;
    }
    return dst;
}

#if defined(__clang__) || defined(__GNUC__)
__attribute__((used, noinline))
#endif
void* __cdecl memcpy(void* dst, const void* src, size_t count)
{
    volatile unsigned char* d = (volatile unsigned char*)dst;
    const volatile unsigned char* s = (const volatile unsigned char*)src;
    while (count-- > 0)
    {
        *d++ = *s++;
    }
    return dst;
}

/* KnownFolder GUIDs: ProgramData & LocalAppData */
static const GUID SWBI_FOLDERID_ProgramData =
    { 0x62AB5D82, 0xFDC1, 0x4DC3, { 0xA9, 0xDD, 0x07, 0x0D, 0x1D, 0x49, 0x5D, 0x97 } };
static const GUID SWBI_FOLDERID_LocalAppData =
    { 0xF1B32785, 0x6FBA, 0x4FCF, { 0x9D, 0x55, 0x7B, 0x8E, 0x7F, 0x15, 0x70, 0x91 } };

#define MAX_LONG_PATH_CHARS 1024
#define CMDLINE_CAP_CHARS   8192

#pragma pack(push, 1)
typedef struct _SwbiEofTrailer
{
    ULONGLONG PayloadOffset;
    unsigned char Magic[8]; /* "SWBIEOF1" */
} SwbiEofTrailer;

typedef struct _SwbiPakHeader
{
    unsigned char Magic[8];      /* "SWBIPK01" */
    char ArchiveId[16];          /* e.g. "x64-9a8b7c6d5e\0\0" */
    DWORD FileCount;
    DWORD MaxUncompressedBytes;
} SwbiPakHeader;

typedef struct _SwbiPakEntryHeader
{
    WORD RelPathChars;
    WORD Flags; /* 0 = raw stored, 1 = MSZIP compressed */
    DWORD UncompressedSize;
    DWORD StoredSize;
} SwbiPakEntryHeader;
#pragma pack(pop)

typedef struct _LauncherWorkBuffers
{
    WCHAR SelfExePath[MAX_LONG_PATH_CHARS];
    WCHAR SafeBaseDir[MAX_LONG_PATH_CHARS];
    WCHAR SafeTempDir[MAX_LONG_PATH_CHARS];
    WCHAR SafeBundleDir[MAX_LONG_PATH_CHARS];
    WCHAR TargetAppDir[MAX_LONG_PATH_CHARS];
    WCHAR TargetExePath[MAX_LONG_PATH_CHARS];
    WCHAR ReadyMarkerPath[MAX_LONG_PATH_CHARS];
    WCHAR CandidateDir[MAX_LONG_PATH_CHARS];
    WCHAR ProbePath[MAX_LONG_PATH_CHARS];
    WCHAR RelPathBuf[MAX_LONG_PATH_CHARS];
    WCHAR DestFilePath[MAX_LONG_PATH_CHARS];
    WCHAR ParentDirPath[MAX_LONG_PATH_CHARS];
    WCHAR ForwardedArgs[CMDLINE_CAP_CHARS];
    WCHAR ElevatedArgs[CMDLINE_CAP_CHARS];
    WCHAR ChildCmdLine[CMDLINE_CAP_CHARS + MAX_LONG_PATH_CHARS + 8];
} LauncherWorkBuffers;

static size_t WStrLen(const WCHAR* s)
{
    size_t n = 0;
    if (!s) return 0;
    while (s[n] != 0) n++;
    return n;
}

static BOOL WStrCopy(WCHAR* dst, size_t cap, const WCHAR* src)
{
    size_t i = 0;
    if (!dst || cap == 0) return FALSE;
    if (!src) { dst[0] = 0; return TRUE; }
    while (src[i] != 0)
    {
        if (i + 1 >= cap) { dst[0] = 0; return FALSE; }
        dst[i] = src[i];
        i++;
    }
    dst[i] = 0;
    return TRUE;
}

static BOOL WStrAppend(WCHAR* dst, size_t cap, const WCHAR* suffix)
{
    size_t baseLen = WStrLen(dst);
    size_t i = 0;
    if (!suffix) return TRUE;
    while (suffix[i] != 0)
    {
        if (baseLen + i + 1 >= cap) return FALSE;
        dst[baseLen + i] = suffix[i];
        i++;
    }
    dst[baseLen + i] = 0;
    return TRUE;
}

static BOOL PathCombine2(WCHAR* out, size_t cap, const WCHAR* a, const WCHAR* b)
{
    size_t len;
    if (!WStrCopy(out, cap, a)) return FALSE;
    len = WStrLen(out);
    while (len > 0 && (out[len - 1] == L'\\' || out[len - 1] == L'/'))
    {
        out[--len] = 0;
    }
    if (!WStrAppend(out, cap, L"\\")) return FALSE;
    while (*b == L'\\' || *b == L'/') b++;
    return WStrAppend(out, cap, b);
}

static BOOL HasTildeChar(const WCHAR* s)
{
    size_t i = 0;
    while (s[i] != 0)
    {
        if (s[i] == L'~') return TRUE;
        i++;
    }
    return FALSE;
}

static BOOL ContainsDotDotSegment(const WCHAR* relPath)
{
    size_t i = 0;
    while (relPath[i] != 0)
    {
        if (relPath[i] == L'.' && relPath[i + 1] == L'.' &&
            (i == 0 || relPath[i - 1] == L'\\' || relPath[i - 1] == L'/') &&
            (relPath[i + 2] == 0 || relPath[i + 2] == L'\\' || relPath[i + 2] == L'/'))
        {
            return TRUE;
        }
        i++;
    }
    return FALSE;
}

static BOOL AppendQuotedArg(WCHAR* dst, size_t cap, const WCHAR* arg)
{
    size_t d = WStrLen(dst);
    BOOL needQuotes = (arg[0] == 0);
    size_t i;

    for (i = 0; arg[i] != 0; i++)
    {
        if (arg[i] == L' ' || arg[i] == L'\t' || arg[i] == L'"')
        {
            needQuotes = TRUE;
            break;
        }
    }

    if (d > 0)
    {
        if (d + 1 >= cap) return FALSE;
        dst[d++] = L' ';
        dst[d] = 0;
    }

    if (!needQuotes)
    {
        return WStrAppend(dst, cap, arg);
    }

    if (d + 1 >= cap) return FALSE;
    dst[d++] = L'"';

    for (i = 0; arg[i] != 0; )
    {
        size_t backslashes = 0;
        while (arg[i] == L'\\')
        {
            backslashes++;
            i++;
        }

        if (arg[i] == 0)
        {
            size_t k;
            for (k = 0; k < backslashes * 2; k++)
            {
                if (d + 1 >= cap) return FALSE;
                dst[d++] = L'\\';
            }
            break;
        }
        else if (arg[i] == L'"')
        {
            size_t k;
            for (k = 0; k < backslashes * 2 + 1; k++)
            {
                if (d + 1 >= cap) return FALSE;
                dst[d++] = L'\\';
            }
            if (d + 1 >= cap) return FALSE;
            dst[d++] = L'"';
            i++;
        }
        else
        {
            size_t k;
            for (k = 0; k < backslashes; k++)
            {
                if (d + 1 >= cap) return FALSE;
                dst[d++] = L'\\';
            }
            if (d + 1 >= cap) return FALSE;
            dst[d++] = arg[i++];
        }
    }

    if (d + 2 > cap) return FALSE;
    dst[d++] = L'"';
    dst[d] = 0;
    return TRUE;
}

static BOOL IsCurrentProcessElevated(void)
{
    HANDLE hToken = NULL;
    TOKEN_ELEVATION elev;
    DWORD cbSize = sizeof(elev);
    BOOL result = FALSE;

    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))
    {
        elev.TokenIsElevated = 0;
        if (GetTokenInformation(hToken, TokenElevation, &elev, sizeof(elev), &cbSize))
        {
            result = (elev.TokenIsElevated != 0);
        }
        CloseHandle(hToken);
    }
    return result;
}

static void EnsureDirectoryRecursiveW(const WCHAR* dirPath)
{
    HANDLE hHeap = GetProcessHeap();
    WCHAR* tmp = (WCHAR*)HeapAlloc(hHeap, 0, MAX_LONG_PATH_CHARS * sizeof(WCHAR));
    size_t len, i;
    DWORD attrs;

    if (!tmp) return;
    if (!WStrCopy(tmp, MAX_LONG_PATH_CHARS, dirPath))
    {
        HeapFree(hHeap, 0, tmp);
        return;
    }
    len = WStrLen(tmp);
    while (len > 3 && (tmp[len - 1] == L'\\' || tmp[len - 1] == L'/'))
    {
        tmp[--len] = 0;
    }

    attrs = GetFileAttributesW(tmp);
    if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY))
    {
        HeapFree(hHeap, 0, tmp);
        return;
    }

    for (i = 3; i < len; i++)
    {
        if (tmp[i] == L'\\' || tmp[i] == L'/')
        {
            WCHAR saved = tmp[i];
            tmp[i] = 0;
            CreateDirectoryW(tmp, NULL);
            tmp[i] = saved;
        }
    }
    CreateDirectoryW(tmp, NULL);
    HeapFree(hHeap, 0, tmp);
}

static BOOL VerifyWritableDirectory(const WCHAR* candidate, WCHAR* probeBuf, size_t probeCap)
{
    WCHAR leaf[64];
    HANDLE hProbe;

    if (!candidate || candidate[0] == 0 || HasTildeChar(candidate))
    {
        return FALSE;
    }

    EnsureDirectoryRecursiveW(candidate);
    wsprintfW(leaf, L".swbi-probe-%lu", GetTickCount());
    if (!PathCombine2(probeBuf, probeCap, candidate, leaf))
    {
        return FALSE;
    }

    hProbe = CreateFileW(
        probeBuf,
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_TEMPORARY,
        NULL);
    if (hProbe == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    CloseHandle(hProbe);
    DeleteFileW(probeBuf);
    return TRUE;
}

static BOOL ResolveSafeRuntimeDirectory(LauncherWorkBuffers* wb)
{
    PWSTR knownPath = NULL;
    WCHAR sysDrive[16];
    size_t i, lastSlash;

    /* 1. C:\ProgramData\SWBI\Runtime */
    if (SUCCEEDED(SHGetKnownFolderPath(&SWBI_FOLDERID_ProgramData, 0, NULL, &knownPath)) && knownPath)
    {
        BOOL ok = PathCombine2(wb->CandidateDir, MAX_LONG_PATH_CHARS, knownPath, L"SWBI\\Runtime");
        CoTaskMemFree(knownPath);
        knownPath = NULL;
        if (ok && VerifyWritableDirectory(wb->CandidateDir, wb->ProbePath, MAX_LONG_PATH_CHARS))
        {
            return WStrCopy(wb->SafeBaseDir, MAX_LONG_PATH_CHARS, wb->CandidateDir);
        }
    }

    /* 2. C:\Users\<LongName>\AppData\Local\SWBI\Runtime */
    if (SUCCEEDED(SHGetKnownFolderPath(&SWBI_FOLDERID_LocalAppData, 0, NULL, &knownPath)) && knownPath)
    {
        BOOL ok = PathCombine2(wb->CandidateDir, MAX_LONG_PATH_CHARS, knownPath, L"SWBI\\Runtime");
        CoTaskMemFree(knownPath);
        knownPath = NULL;
        if (ok && VerifyWritableDirectory(wb->CandidateDir, wb->ProbePath, MAX_LONG_PATH_CHARS))
        {
            return WStrCopy(wb->SafeBaseDir, MAX_LONG_PATH_CHARS, wb->CandidateDir);
        }
    }

    /* 3. C:\SWBI\Runtime */
    sysDrive[0] = 0;
    if (GetEnvironmentVariableW(L"SystemDrive", sysDrive, 16) == 0 || sysDrive[0] == 0)
    {
        WStrCopy(sysDrive, 16, L"C:");
    }
    if (PathCombine2(wb->CandidateDir, MAX_LONG_PATH_CHARS, sysDrive, L"SWBI\\Runtime") &&
        VerifyWritableDirectory(wb->CandidateDir, wb->ProbePath, MAX_LONG_PATH_CHARS))
    {
        return WStrCopy(wb->SafeBaseDir, MAX_LONG_PATH_CHARS, wb->CandidateDir);
    }

    /* 4. Next to the executable itself */
    lastSlash = 0;
    for (i = 0; wb->SelfExePath[i] != 0; i++)
    {
        if (wb->SelfExePath[i] == L'\\' || wb->SelfExePath[i] == L'/') lastSlash = i;
    }
    if (lastSlash > 0)
    {
        WStrCopy(wb->CandidateDir, MAX_LONG_PATH_CHARS, wb->SelfExePath);
        wb->CandidateDir[lastSlash] = 0;
        if (WStrAppend(wb->CandidateDir, MAX_LONG_PATH_CHARS, L"\\.swbi-runtime") &&
            VerifyWritableDirectory(wb->CandidateDir, wb->ProbePath, MAX_LONG_PATH_CHARS))
        {
            return WStrCopy(wb->SafeBaseDir, MAX_LONG_PATH_CHARS, wb->CandidateDir);
        }
    }

    return FALSE;
}

static BOOL ReadExact(HANDLE hFile, void* buf, DWORD bytesToRead)
{
    unsigned char* p = (unsigned char*)buf;
    DWORD remaining = bytesToRead;
    while (remaining > 0)
    {
        DWORD got = 0;
        if (!ReadFile(hFile, p, remaining, &got, NULL) || got == 0)
        {
            return FALSE;
        }
        p += got;
        remaining -= got;
    }
    return TRUE;
}

static BOOL WriteExact(HANDLE hFile, const void* buf, DWORD bytesToWrite)
{
    const unsigned char* p = (const unsigned char*)buf;
    DWORD remaining = bytesToWrite;
    while (remaining > 0)
    {
        DWORD written = 0;
        if (!WriteFile(hFile, p, remaining, &written, NULL) || written == 0)
        {
            return FALSE;
        }
        p += written;
        remaining -= written;
    }
    return TRUE;
}

static BOOL BytesEqual8(const unsigned char* a, const char* b8)
{
    int i;
    for (i = 0; i < 8; i++)
    {
        if (a[i] != (unsigned char)b8[i]) return FALSE;
    }
    return TRUE;
}

typedef struct _FindWorkFrame
{
    WCHAR Pattern[MAX_LONG_PATH_CHARS];
    WCHAR Child[MAX_LONG_PATH_CHARS];
    WIN32_FIND_DATAW Fd;
} FindWorkFrame;

static void DeleteDirectoryRecursiveBestEffort(const WCHAR* dirPath)
{
    HANDLE hHeap = GetProcessHeap();
    FindWorkFrame* fw = (FindWorkFrame*)HeapAlloc(hHeap, HEAP_ZERO_MEMORY, sizeof(FindWorkFrame));
    HANDLE hFind;

    if (!fw) return;
    if (!PathCombine2(fw->Pattern, MAX_LONG_PATH_CHARS, dirPath, L"*"))
    {
        HeapFree(hHeap, 0, fw);
        return;
    }
    hFind = FindFirstFileW(fw->Pattern, &fw->Fd);
    if (hFind == INVALID_HANDLE_VALUE)
    {
        HeapFree(hHeap, 0, fw);
        return;
    }

    do
    {
        if (lstrcmpW(fw->Fd.cFileName, L".") == 0 || lstrcmpW(fw->Fd.cFileName, L"..") == 0)
        {
            continue;
        }
        if (PathCombine2(fw->Child, MAX_LONG_PATH_CHARS, dirPath, fw->Fd.cFileName))
        {
            if (fw->Fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                DeleteDirectoryRecursiveBestEffort(fw->Child);
            }
            else
            {
                SetFileAttributesW(fw->Child, FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(fw->Child);
            }
        }
    } while (FindNextFileW(hFind, &fw->Fd));

    FindClose(hFind);
    HeapFree(hHeap, 0, fw);
    RemoveDirectoryW(dirPath);
}

static void CleanupOlderSameFlavorCaches(const WCHAR* safeBaseDir, const WCHAR* currentFolderLeaf, const WCHAR* flavorPrefix)
{
    HANDLE hHeap = GetProcessHeap();
    FindWorkFrame* fw = (FindWorkFrame*)HeapAlloc(hHeap, HEAP_ZERO_MEMORY, sizeof(FindWorkFrame));
    HANDLE hFind;
    size_t prefixLen = WStrLen(flavorPrefix);

    if (!fw) return;
    if (!PathCombine2(fw->Pattern, MAX_LONG_PATH_CHARS, safeBaseDir, L"app-*"))
    {
        HeapFree(hHeap, 0, fw);
        return;
    }
    hFind = FindFirstFileW(fw->Pattern, &fw->Fd);
    if (hFind == INVALID_HANDLE_VALUE)
    {
        HeapFree(hHeap, 0, fw);
        return;
    }

    do
    {
        if ((fw->Fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
            lstrcmpiW(fw->Fd.cFileName, currentFolderLeaf) != 0)
        {
            size_t i;
            BOOL sameFlavor = TRUE;
            for (i = 0; i < prefixLen; i++)
            {
                if (fw->Fd.cFileName[i] != flavorPrefix[i]) { sameFlavor = FALSE; break; }
            }
            if (sameFlavor && PathCombine2(fw->Child, MAX_LONG_PATH_CHARS, safeBaseDir, fw->Fd.cFileName))
            {
                DeleteDirectoryRecursiveBestEffort(fw->Child);
            }
        }
    } while (FindNextFileW(hFind, &fw->Fd));

    FindClose(hFind);
    HeapFree(hHeap, 0, fw);
}

/* A cache folder only counts as ready when the extracted EXE is a plausible PE image.
   Byte-for-byte ACL/hash pinning of the extracted tree is tracked separately; this at least stops
   truncated, quarantined or half-written payloads from being launched and reported as a startup crash. */
static BOOL VerifyCachedExecutable(const WCHAR* exePath)
{
    HANDLE hFile;
    LARGE_INTEGER size;
    unsigned char hdr[2];
    BOOL ok = FALSE;

    hFile = CreateFileW(
        exePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (hFile == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    if (GetFileSizeEx(hFile, &size) && size.QuadPart > 4096 &&
        ReadExact(hFile, hdr, 2) && hdr[0] == 'M' && hdr[1] == 'Z')
    {
        ok = TRUE;
    }

    CloseHandle(hFile);
    return ok;
}

static BOOL ExtractOrResolveEmbeddedPayload(LauncherWorkBuffers* wb)
{
    HANDLE hSelf;
    LARGE_INTEGER seekPos;
    SwbiEofTrailer trailer;
    SwbiPakHeader hdr;
    WCHAR folderLeaf[48];
    WCHAR flavorPrefix[24];
    int idx, dashPos;
    DWORD exeAttrs, markerAttrs;
    DECOMPRESSOR_HANDLE hDecomp = NULL;
    unsigned char* compBuf = NULL;
    DWORD compBufCap = 0;
    unsigned char* decompBuf = NULL;
    DWORD fileIdx;
    BOOL ok = FALSE;

    hSelf = CreateFileW(
        wb->SelfExePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (hSelf == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    seekPos.QuadPart = -((LONGLONG)sizeof(SwbiEofTrailer));
    if (!SetFilePointerEx(hSelf, seekPos, NULL, FILE_END) ||
        !ReadExact(hSelf, &trailer, sizeof(trailer)) ||
        !BytesEqual8(trailer.Magic, "SWBIEOF1"))
    {
        CloseHandle(hSelf);
        return FALSE;
    }

    seekPos.QuadPart = (LONGLONG)trailer.PayloadOffset;
    if (!SetFilePointerEx(hSelf, seekPos, NULL, FILE_BEGIN) ||
        !ReadExact(hSelf, &hdr, sizeof(hdr)) ||
        !BytesEqual8(hdr.Magic, "SWBIPK01") ||
        hdr.FileCount == 0 || hdr.FileCount > 10000)
    {
        CloseHandle(hSelf);
        return FALSE;
    }

    /* Build folderLeaf = "app-<ArchiveId>" and flavorPrefix = "app-<arch>-" */
    WStrCopy(folderLeaf, 48, L"app-");
    dashPos = -1;
    for (idx = 0; idx < 15 && hdr.ArchiveId[idx] != 0; idx++)
    {
        char ch = hdr.ArchiveId[idx];
        WCHAR wch = (ch >= 32 && ch <= 126) ? (WCHAR)ch : L'_';
        size_t cur = WStrLen(folderLeaf);
        if (cur + 1 < 48)
        {
            folderLeaf[cur] = wch;
            folderLeaf[cur + 1] = 0;
        }
        if (ch == '-' && dashPos < 0) dashPos = (int)cur + 1;
    }

    WStrCopy(flavorPrefix, 24, folderLeaf);
    if (dashPos > 4 && dashPos < 23)
    {
        flavorPrefix[dashPos] = 0;
    }

    if (!PathCombine2(wb->TargetAppDir, MAX_LONG_PATH_CHARS, wb->SafeBaseDir, folderLeaf) ||
        !PathCombine2(wb->TargetExePath, MAX_LONG_PATH_CHARS, wb->TargetAppDir, L"SWBI-SelfWindowsBootableInstaller.exe") ||
        !PathCombine2(wb->ReadyMarkerPath, MAX_LONG_PATH_CHARS, wb->TargetAppDir, L".swbi-ready"))
    {
        CloseHandle(hSelf);
        return FALSE;
    }

    markerAttrs = GetFileAttributesW(wb->ReadyMarkerPath);
    exeAttrs = GetFileAttributesW(wb->TargetExePath);
    if (markerAttrs != INVALID_FILE_ATTRIBUTES && !(markerAttrs & FILE_ATTRIBUTE_DIRECTORY) &&
        exeAttrs != INVALID_FILE_ATTRIBUTES && !(exeAttrs & FILE_ATTRIBUTE_DIRECTORY) &&
        VerifyCachedExecutable(wb->TargetExePath))
    {
        CloseHandle(hSelf);
        return TRUE;
    }

    /* A stale, truncated or quarantined cache is replaced instead of being launched. */
    if (markerAttrs != INVALID_FILE_ATTRIBUTES)
    {
        DeleteFileW(wb->ReadyMarkerPath);
    }

    EnsureDirectoryRecursiveW(wb->TargetAppDir);

    if (!CreateDecompressor(COMPRESS_ALGORITHM_MSZIP, NULL, &hDecomp))
    {
        CloseHandle(hSelf);
        return FALSE;
    }

    compBufCap = 4 * 1024 * 1024;
    compBuf = (unsigned char*)VirtualAlloc(NULL, compBufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    decompBuf = (unsigned char*)VirtualAlloc(
        NULL,
        (hdr.MaxUncompressedBytes > 65536 ? hdr.MaxUncompressedBytes : 65536) + 64,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_READWRITE);

    if (!compBuf || !decompBuf)
    {
        goto cleanup;
    }

    for (fileIdx = 0; fileIdx < hdr.FileCount; fileIdx++)
    {
        SwbiPakEntryHeader eh;
        DWORD pathBytes;
        size_t pi, lastSep;
        HANDLE hOut;
        const void* writePtr;
        DWORD writeLen;

        if (!ReadExact(hSelf, &eh, sizeof(eh))) goto cleanup;
        if (eh.RelPathChars == 0 || eh.RelPathChars >= MAX_LONG_PATH_CHARS - 2) goto cleanup;
        if (eh.UncompressedSize > hdr.MaxUncompressedBytes) goto cleanup;

        pathBytes = (DWORD)eh.RelPathChars * sizeof(WCHAR);
        if (!ReadExact(hSelf, wb->RelPathBuf, pathBytes)) goto cleanup;
        wb->RelPathBuf[eh.RelPathChars] = 0;

        for (pi = 0; pi < eh.RelPathChars; pi++)
        {
            if (wb->RelPathBuf[pi] == L'/') wb->RelPathBuf[pi] = L'\\';
        }
        if (ContainsDotDotSegment(wb->RelPathBuf)) goto cleanup;

        if (!PathCombine2(wb->DestFilePath, MAX_LONG_PATH_CHARS, wb->TargetAppDir, wb->RelPathBuf))
        {
            goto cleanup;
        }

        /* Ensure parent directory exists */
        lastSep = 0;
        for (pi = 0; wb->DestFilePath[pi] != 0; pi++)
        {
            if (wb->DestFilePath[pi] == L'\\') lastSep = pi;
        }
        if (lastSep > 3)
        {
            WStrCopy(wb->ParentDirPath, MAX_LONG_PATH_CHARS, wb->DestFilePath);
            wb->ParentDirPath[lastSep] = 0;
            EnsureDirectoryRecursiveW(wb->ParentDirPath);
        }

        if (eh.StoredSize > compBufCap)
        {
            VirtualFree(compBuf, 0, MEM_RELEASE);
            compBufCap = eh.StoredSize + 65536;
            compBuf = (unsigned char*)VirtualAlloc(NULL, compBufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!compBuf) goto cleanup;
        }

        if (eh.StoredSize > 0 && !ReadExact(hSelf, compBuf, eh.StoredSize))
        {
            goto cleanup;
        }

        if (eh.Flags == 1)
        {
            SIZE_T actualUncomp = 0;
            if (!Decompress(hDecomp, compBuf, eh.StoredSize, decompBuf, eh.UncompressedSize, &actualUncomp) ||
                actualUncomp != (SIZE_T)eh.UncompressedSize)
            {
                goto cleanup;
            }
            writePtr = decompBuf;
            writeLen = eh.UncompressedSize;
        }
        else
        {
            writePtr = compBuf;
            writeLen = eh.StoredSize;
        }

        SetFileAttributesW(wb->DestFilePath, FILE_ATTRIBUTE_NORMAL);
        hOut = CreateFileW(
            wb->DestFilePath,
            GENERIC_WRITE,
            FILE_SHARE_READ,
            NULL,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (hOut == INVALID_HANDLE_VALUE) goto cleanup;

        if (writeLen > 0 && !WriteExact(hOut, writePtr, writeLen))
        {
            CloseHandle(hOut);
            goto cleanup;
        }
        CloseHandle(hOut);
    }

    if (GetFileAttributesW(wb->TargetExePath) == INVALID_FILE_ATTRIBUTES)
    {
        goto cleanup;
    }

    {
        HANDLE hMarker = CreateFileW(
            wb->ReadyMarkerPath,
            GENERIC_WRITE,
            FILE_SHARE_READ,
            NULL,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (hMarker != INVALID_HANDLE_VALUE)
        {
            WriteExact(hMarker, "OK", 2);
            CloseHandle(hMarker);
        }
    }

    CleanupOlderSameFlavorCaches(wb->SafeBaseDir, folderLeaf, flavorPrefix);
    ok = TRUE;

cleanup:
    if (decompBuf) VirtualFree(decompBuf, 0, MEM_RELEASE);
    if (compBuf) VirtualFree(compBuf, 0, MEM_RELEASE);
    if (hDecomp) CloseDecompressor(hDecomp);
    CloseHandle(hSelf);

    /* Extraction can fail because a second SWBI instance is writing the same cache folder. If that instance
       finished, its verified payload is usable; otherwise report the failure honestly. */
    if (!ok &&
        GetFileAttributesW(wb->ReadyMarkerPath) != INVALID_FILE_ATTRIBUTES &&
        VerifyCachedExecutable(wb->TargetExePath))
    {
        ok = TRUE;
    }

    return ok;
}

static int RunLauncher(void)
{
    HANDLE hHeap = GetProcessHeap();
    LauncherWorkBuffers* wb = (LauncherWorkBuffers*)HeapAlloc(hHeap, HEAP_ZERO_MEMORY, sizeof(LauncherWorkBuffers));
    int argc = 0;
    LPWSTR* argv = NULL;
    BOOL isSmokeTest = FALSE;
    BOOL waitUntilExit = FALSE;
    BOOL skipElevation = FALSE;
    int i;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    DWORD exitCode = 0;

    if (!wb) return 3;

    if (GetModuleFileNameW(NULL, wb->SelfExePath, MAX_LONG_PATH_CHARS) == 0)
    {
        HeapFree(hHeap, 0, wb);
        return 3;
    }

    argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv && argc > 1)
    {
        for (i = 1; i < argc; i++)
        {
            if (lstrcmpiW(argv[i], L"--smoke-test") == 0 || lstrcmpiW(argv[i], L"/smoke-test") == 0)
            {
                isSmokeTest = TRUE;
                waitUntilExit = TRUE;
                skipElevation = TRUE;
            }
            else if (lstrcmpiW(argv[i], L"--wait") == 0)
            {
                waitUntilExit = TRUE;
            }
            else if (lstrcmpiW(argv[i], L"--no-elevate") == 0 || lstrcmpiW(argv[i], L"--elevated-relaunch") == 0)
            {
                skipElevation = TRUE;
            }

            if (lstrcmpiW(argv[i], L"--elevated-relaunch") != 0)
            {
                AppendQuotedArg(wb->ForwardedArgs, CMDLINE_CAP_CHARS, argv[i]);
            }
        }
    }
    if (argv) LocalFree(argv);

    /* 1. Smart UAC Self-Elevation (falls back cleanly to Dry-Run if UAC/Appinfo is broken) */
    if (!skipElevation && !IsCurrentProcessElevated())
    {
        SHELLEXECUTEINFOW sei;
        WStrCopy(wb->ElevatedArgs, CMDLINE_CAP_CHARS, wb->ForwardedArgs);
        AppendQuotedArg(wb->ElevatedArgs, CMDLINE_CAP_CHARS, L"--elevated-relaunch");

        memset(&sei, 0, sizeof(sei));
        sei.cbSize = sizeof(sei);
        sei.lpVerb = L"runas";
        sei.lpFile = wb->SelfExePath;
        sei.lpParameters = wb->ElevatedArgs;
        sei.nShow = SW_SHOWNORMAL;

        if (ShellExecuteExW(&sei))
        {
            HeapFree(hHeap, 0, wb);
            return 0;
        }
    }

    /* 2. Resolve 100% %TEMP%-immune (ZLAG~1-immune) safe runtime folder */
    if (!ResolveSafeRuntimeDirectory(wb))
    {
        if (!isSmokeTest)
        {
            MessageBoxW(
                NULL,
                L"SWBI could not find a writable runtime directory on this PC.",
                L"SWBI \x2014 Self Windows Bootable Installer",
                MB_OK | MB_ICONERROR);
        }
        HeapFree(hHeap, 0, wb);
        return 3;
    }

    PathCombine2(wb->SafeTempDir, MAX_LONG_PATH_CHARS, wb->SafeBaseDir, L"temp");
    PathCombine2(wb->SafeBundleDir, MAX_LONG_PATH_CHARS, wb->SafeBaseDir, L"bundle");
    EnsureDirectoryRecursiveW(wb->SafeTempDir);
    EnsureDirectoryRecursiveW(wb->SafeBundleDir);

    /* 3. Repair TEMP/TMP/DOTNET_BUNDLE_EXTRACT_BASE_DIR before .NET starts */
    SetEnvironmentVariableW(L"TEMP", wb->SafeTempDir);
    SetEnvironmentVariableW(L"TMP", wb->SafeTempDir);
    SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", wb->SafeBundleDir);

    /* 4. Extract or reuse cached embedded SWBI WPF runtime */
    if (!ExtractOrResolveEmbeddedPayload(wb))
    {
        if (!isSmokeTest)
        {
            MessageBoxW(
                NULL,
                L"SWBI could not extract its embedded application runtime.\n\nPlease re-download the SWBI standalone .exe.",
                L"SWBI \x2014 Self Windows Bootable Installer",
                MB_OK | MB_ICONERROR);
        }
        HeapFree(hHeap, 0, wb);
        return 3;
    }

    /* Tell the extracted child which native SFX was actually launched. It must not
       mistake its cached apphost for the user-visible standalone EXE when updating. */
    SetEnvironmentVariableW(L"SWBI_STANDALONE_LAUNCHER", wb->SelfExePath);

    /* 5. Launch SWBI WPF UI forwarding user arguments */
    wb->ChildCmdLine[0] = 0;
    AppendQuotedArg(wb->ChildCmdLine, CMDLINE_CAP_CHARS + MAX_LONG_PATH_CHARS, wb->TargetExePath);
    if (wb->ForwardedArgs[0] != 0)
    {
        WStrAppend(wb->ChildCmdLine, CMDLINE_CAP_CHARS + MAX_LONG_PATH_CHARS, L" ");
        WStrAppend(wb->ChildCmdLine, CMDLINE_CAP_CHARS + MAX_LONG_PATH_CHARS, wb->ForwardedArgs);
    }

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    memset(&pi, 0, sizeof(pi));

    if (!CreateProcessW(
            wb->TargetExePath,
            wb->ChildCmdLine,
            NULL,
            NULL,
            FALSE,
            0,
            NULL,
            wb->TargetAppDir,
            &si,
            &pi))
    {
        if (!isSmokeTest)
        {
            MessageBoxW(
                NULL,
                L"Windows failed to start the extracted SWBI user interface.",
                L"SWBI \x2014 Self Windows Bootable Installer",
                MB_OK | MB_ICONERROR);
        }
        HeapFree(hHeap, 0, wb);
        return 4;
    }

    if (waitUntilExit)
    {
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, &exitCode);
    }
    else if (WaitForSingleObject(pi.hProcess, 2500) == WAIT_OBJECT_0)
    {
        GetExitCodeProcess(pi.hProcess, &exitCode);
        if (exitCode != 0)
        {
            WCHAR errMsg[256];
            wsprintfW(
                errMsg,
                L"SWBI exited unexpectedly during startup (code %lu).\n\nCheck logs under:\nC:\\ProgramData\\SWBI\\Work\\logs",
                exitCode);
            MessageBoxW(NULL, errMsg, L"SWBI \x2014 Self Windows Bootable Installer", MB_OK | MB_ICONERROR);
        }
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    HeapFree(hHeap, 0, wb);
    return (int)exitCode;
}

int __cdecl SwbiMainEntry(void)
{
    int code = RunLauncher();
    ExitProcess((UINT)code);
    return code;
}
