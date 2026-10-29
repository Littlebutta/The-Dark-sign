/* dksign_saveredirect: loaded by the game as dinput8.dll. It forwards DirectInput to the
   system DLL and hooks the file APIs so the game's save folder (AppData or Documents) is
   served from TheDarkSign\Saves with .dksign files. Vanilla saves are never opened. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <unknwn.h>
#include <stdio.h>

#include "MinHook.h"
#include "offline.h"
#include "redirect.h"

static dks_config g_cfg;
static int g_active;
static HANDLE g_log = INVALID_HANDLE_VALUE;
static int g_log_lines;

/* Search handles opened in the redirected folder, whose results get their names restored. */
#define MAX_FINDS 64
static HANDLE g_finds[MAX_FINDS];
static SRWLOCK g_finds_lock = SRWLOCK_INIT;

typedef HANDLE (WINAPI *CreateFileW_t)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HANDLE (WINAPI *CreateFile2_t)(LPCWSTR, DWORD, DWORD, DWORD, LPCREATEFILE2_EXTENDED_PARAMETERS);
typedef DWORD (WINAPI *GetFileAttributesW_t)(LPCWSTR);
typedef BOOL (WINAPI *GetFileAttributesExW_t)(LPCWSTR, GET_FILEEX_INFO_LEVELS, LPVOID);
typedef BOOL (WINAPI *DeleteFileW_t)(LPCWSTR);
typedef BOOL (WINAPI *CreateDirectoryW_t)(LPCWSTR, LPSECURITY_ATTRIBUTES);
typedef BOOL (WINAPI *RemoveDirectoryW_t)(LPCWSTR);
typedef BOOL (WINAPI *MoveFileExW_t)(LPCWSTR, LPCWSTR, DWORD);
typedef BOOL (WINAPI *CopyFileW_t)(LPCWSTR, LPCWSTR, BOOL);
typedef BOOL (WINAPI *ReplaceFileW_t)(LPCWSTR, LPCWSTR, LPCWSTR, DWORD, LPVOID, LPVOID);
typedef HANDLE (WINAPI *FindFirstFileExW_t)(LPCWSTR, FINDEX_INFO_LEVELS, LPVOID, FINDEX_SEARCH_OPS, LPVOID, DWORD);
typedef BOOL (WINAPI *FindNextFileW_t)(HANDLE, LPWIN32_FIND_DATAW);
typedef BOOL (WINAPI *FindClose_t)(HANDLE);

static CreateFileW_t o_CreateFileW;
static CreateFile2_t o_CreateFile2;
static GetFileAttributesW_t o_GetFileAttributesW;
static GetFileAttributesExW_t o_GetFileAttributesExW;
static DeleteFileW_t o_DeleteFileW;
static CreateDirectoryW_t o_CreateDirectoryW;
static RemoveDirectoryW_t o_RemoveDirectoryW;
static MoveFileExW_t o_MoveFileExW;
static CopyFileW_t o_CopyFileW;
static ReplaceFileW_t o_ReplaceFileW;
static FindFirstFileExW_t o_FindFirstFileExW;
static FindNextFileW_t o_FindNextFileW;
static FindClose_t o_FindClose;

static void log_line(const wchar_t *fmt, ...)
{
    if (g_log == INVALID_HANDLE_VALUE || g_log_lines >= 500)
        return;
    wchar_t wide[2 * DKS_MAX_PATH + 64];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf(wide, sizeof wide / sizeof wide[0] - 2, fmt, args);
    va_end(args);
    wide[sizeof wide / sizeof wide[0] - 3] = 0;
    wcscat(wide, L"\r\n");

    char utf8[4 * DKS_MAX_PATH + 128];
    int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, utf8, sizeof utf8, NULL, NULL);
    DWORD written;
    if (n > 1)
        WriteFile(g_log, utf8, (DWORD)(n - 1), &written, NULL);
    g_log_lines++;
}

/* Returns the path to use: the redirected one in `buf`, or the original. */
static LPCWSTR map(LPCWSTR path, wchar_t *buf, const wchar_t *what)
{
    if (!g_active || !path || !dks_redirect(&g_cfg, path, buf, DKS_MAX_PATH))
        return path;
    log_line(L"%ls %ls -> %ls", what, path, buf);
    return buf;
}

static HANDLE WINAPI h_CreateFileW(LPCWSTR p, DWORD a, DWORD s, LPSECURITY_ATTRIBUTES sa, DWORD c, DWORD f, HANDLE t)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_CreateFileW(map(p, buf, L"open"), a, s, sa, c, f, t);
}

static HANDLE WINAPI h_CreateFile2(LPCWSTR p, DWORD a, DWORD s, DWORD c, LPCREATEFILE2_EXTENDED_PARAMETERS x)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_CreateFile2(map(p, buf, L"open"), a, s, c, x);
}

static DWORD WINAPI h_GetFileAttributesW(LPCWSTR p)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_GetFileAttributesW(map(p, buf, L"stat"));
}

static BOOL WINAPI h_GetFileAttributesExW(LPCWSTR p, GET_FILEEX_INFO_LEVELS l, LPVOID i)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_GetFileAttributesExW(map(p, buf, L"stat"), l, i);
}

static BOOL WINAPI h_DeleteFileW(LPCWSTR p)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_DeleteFileW(map(p, buf, L"delete"));
}

static BOOL WINAPI h_CreateDirectoryW(LPCWSTR p, LPSECURITY_ATTRIBUTES sa)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_CreateDirectoryW(map(p, buf, L"mkdir"), sa);
}

static BOOL WINAPI h_RemoveDirectoryW(LPCWSTR p)
{
    wchar_t buf[DKS_MAX_PATH];
    return o_RemoveDirectoryW(map(p, buf, L"rmdir"));
}

static BOOL WINAPI h_MoveFileExW(LPCWSTR from, LPCWSTR to, DWORD flags)
{
    wchar_t b1[DKS_MAX_PATH], b2[DKS_MAX_PATH];
    return o_MoveFileExW(map(from, b1, L"move"), map(to, b2, L"move to"), flags);
}

static BOOL WINAPI h_CopyFileW(LPCWSTR from, LPCWSTR to, BOOL fail)
{
    wchar_t b1[DKS_MAX_PATH], b2[DKS_MAX_PATH];
    return o_CopyFileW(map(from, b1, L"copy"), map(to, b2, L"copy to"), fail);
}

static BOOL WINAPI h_ReplaceFileW(LPCWSTR dst, LPCWSTR src, LPCWSTR bak, DWORD f, LPVOID e, LPVOID r)
{
    wchar_t b1[DKS_MAX_PATH], b2[DKS_MAX_PATH], b3[DKS_MAX_PATH];
    return o_ReplaceFileW(map(dst, b1, L"replace"), map(src, b2, L"replace with"), map(bak, b3, L"backup"), f, e, r);
}

static void track_find(HANDLE h, int add)
{
    AcquireSRWLockExclusive(&g_finds_lock);
    for (int i = 0; i < MAX_FINDS; i++) {
        if (add ? g_finds[i] == NULL : g_finds[i] == h) {
            g_finds[i] = add ? h : NULL;
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_finds_lock);
}

static int is_tracked(HANDLE h)
{
    int found = 0;
    AcquireSRWLockShared(&g_finds_lock);
    for (int i = 0; i < MAX_FINDS && !found; i++)
        found = g_finds[i] == h;
    ReleaseSRWLockShared(&g_finds_lock);
    return found;
}

static void restore_find_name(FINDEX_INFO_LEVELS level, LPVOID data)
{
    if (level == FindExInfoStandard || level == FindExInfoBasic) {
        WIN32_FIND_DATAW *fd = data;
        dks_restore_name(&g_cfg, fd->cFileName, MAX_PATH);
    }
}

static HANDLE WINAPI h_FindFirstFileExW(LPCWSTR p, FINDEX_INFO_LEVELS l, LPVOID d, FINDEX_SEARCH_OPS o, LPVOID f, DWORD fl)
{
    wchar_t buf[DKS_MAX_PATH];
    LPCWSTR mapped = map(p, buf, L"list");
    HANDLE h = o_FindFirstFileExW(mapped, l, d, o, f, fl);
    if (mapped != p && h != INVALID_HANDLE_VALUE) {
        restore_find_name(l, d);
        track_find(h, 1);
    }
    return h;
}

static BOOL WINAPI h_FindNextFileW(HANDLE h, LPWIN32_FIND_DATAW d)
{
    BOOL ok = o_FindNextFileW(h, d);
    if (ok && is_tracked(h))
        dks_restore_name(&g_cfg, d->cFileName, MAX_PATH);
    return ok;
}

static BOOL WINAPI h_FindClose(HANDLE h)
{
    track_find(h, 0);
    return o_FindClose(h);
}

/* Hooks the KernelBase implementation, which kernel32's exports forward to, so calls made
   through either DLL are caught. */
static void hook(const char *name, LPVOID detour, LPVOID *original)
{
    HMODULE base = GetModuleHandleW(L"kernelbase.dll");
    FARPROC target = base ? GetProcAddress(base, name) : NULL;
    if (!target)
        target = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name);
    if (!target) {
        log_line(L"hook: %hs not found", name);
        return;
    }
    MH_STATUS status = MH_CreateHook((LPVOID)target, detour, original);
    if (status == MH_OK)
        status = MH_EnableHook((LPVOID)target);
    if (status != MH_OK)
        log_line(L"hook: %hs failed (%hs)", name, MH_StatusToString(status));
}

static void dirname_inplace(wchar_t *path)
{
    wchar_t *slash = wcsrchr(path, L'\\');
    if (slash)
        *slash = 0;
}

static int dir_exists(const wchar_t *path)
{
    DWORD attr = GetFileAttributesW(path);
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY);
}

/* Without the launcher's environment (the exe started by hand), saves still go to the
   project: the root is the first folder above the game that holds "TheDarkSign" or "Launcher". */
static int default_save_dir(const wchar_t *game, wchar_t *out)
{
    wchar_t dir[DKS_MAX_PATH], probe[DKS_MAX_PATH];
    if (!GetModuleFileNameW(NULL, dir, DKS_MAX_PATH))
        return 0;
    dirname_inplace(dir);
    for (int depth = 0; depth < 6 && wcschr(dir, L'\\'); depth++) {
        dirname_inplace(dir);
        _snwprintf(probe, DKS_MAX_PATH, L"%ls\\TheDarkSign", dir);
        int found = dir_exists(probe);
        if (!found) {
            _snwprintf(probe, DKS_MAX_PATH, L"%ls\\Launcher", dir);
            found = dir_exists(probe);
        }
        if (found) {
            _snwprintf(out, DKS_MAX_PATH, L"%ls\\TheDarkSign\\Saves\\Default\\%ls", dir, game);
            return 1;
        }
    }
    return 0;
}

/* DllMain runs under the loader lock, so shell32 and COM are off limits here: the folders
   come from the environment and the registry instead. */
static int vanilla_dir(const wchar_t *game, wchar_t *out)
{
    wchar_t base[DKS_MAX_PATH];
    if (wcscmp(game, L"DSR") == 0) {
        wchar_t raw[DKS_MAX_PATH];
        DWORD size = sizeof raw;
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders",
                         L"Personal", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, NULL, raw, &size) != ERROR_SUCCESS)
            return 0;
        if (!ExpandEnvironmentStringsW(raw, base, DKS_MAX_PATH))
            return 0;
        _snwprintf(out, DKS_MAX_PATH, L"%ls\\NBGI\\DARK SOULS REMASTERED", base);
    } else {
        if (!GetEnvironmentVariableW(L"APPDATA", base, DKS_MAX_PATH))
            return 0;
        _snwprintf(out, DKS_MAX_PATH, L"%ls\\%ls", base, wcscmp(game, L"DS3") == 0 ? L"DarkSoulsIII" : L"EldenRing");
    }
    out[DKS_MAX_PATH - 1] = 0;
    return 1;
}

static void make_dirs(const wchar_t *path)
{
    wchar_t buf[DKS_MAX_PATH];
    wcsncpy(buf, path, DKS_MAX_PATH - 1);
    buf[DKS_MAX_PATH - 1] = 0;
    for (wchar_t *c = buf + 3; *c; c++) { /* skip "D:\" */
        if (*c == L'\\') {
            *c = 0;
            CreateDirectoryW(buf, NULL);
            *c = L'\\';
        }
    }
    CreateDirectoryW(buf, NULL);
}

static void init(void)
{
    wchar_t exe[DKS_MAX_PATH];
    GetModuleFileNameW(NULL, exe, DKS_MAX_PATH);
    const wchar_t *game = dks_game_from_exe(exe);
    if (!game)
        return; /* loaded by some other program: only act as dinput8 */

    if (!GetEnvironmentVariableW(L"DKSIGN_SAVE_DIR", g_cfg.save_dir, DKS_MAX_PATH) && !default_save_dir(game, g_cfg.save_dir))
        goto fail;
    if (!GetEnvironmentVariableW(L"DKSIGN_SAVE_EXT", g_cfg.save_ext, 32))
        wcscpy(g_cfg.save_ext, L".dksign");
    if (!vanilla_dir(game, g_cfg.vanilla_dir))
        goto fail;

    make_dirs(g_cfg.save_dir);
    wchar_t log_path[DKS_MAX_PATH];
    _snwprintf(log_path, DKS_MAX_PATH, L"%ls\\dksign_saveredirect.log", g_cfg.save_dir);
    g_log = CreateFileW(log_path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    log_line(L"game %ls: %ls -> %ls (*%ls)", game, g_cfg.vanilla_dir, g_cfg.save_dir, g_cfg.save_ext);

    if (MH_Initialize() != MH_OK)
        goto fail;
    hook("CreateFileW", h_CreateFileW, (LPVOID *)&o_CreateFileW);
    hook("CreateFile2", h_CreateFile2, (LPVOID *)&o_CreateFile2);
    hook("GetFileAttributesW", h_GetFileAttributesW, (LPVOID *)&o_GetFileAttributesW);
    hook("GetFileAttributesExW", h_GetFileAttributesExW, (LPVOID *)&o_GetFileAttributesExW);
    hook("DeleteFileW", h_DeleteFileW, (LPVOID *)&o_DeleteFileW);
    hook("CreateDirectoryW", h_CreateDirectoryW, (LPVOID *)&o_CreateDirectoryW);
    hook("RemoveDirectoryW", h_RemoveDirectoryW, (LPVOID *)&o_RemoveDirectoryW);
    hook("MoveFileExW", h_MoveFileExW, (LPVOID *)&o_MoveFileExW);
    hook("CopyFileW", h_CopyFileW, (LPVOID *)&o_CopyFileW);
    hook("ReplaceFileW", h_ReplaceFileW, (LPVOID *)&o_ReplaceFileW);
    hook("FindFirstFileExW", h_FindFirstFileExW, (LPVOID *)&o_FindFirstFileExW);
    hook("FindNextFileW", h_FindNextFileW, (LPVOID *)&o_FindNextFileW);
    hook("FindClose", h_FindClose, (LPVOID *)&o_FindClose);
    g_active = o_CreateFileW != NULL;
    if (!g_active)
        goto fail;

    /* Single-player only: the game never reaches FromSoftware's servers. */
    if (!dks_block_online(log_line)) {
        MessageBoxW(NULL, L"The Dark Sign could not block this game's online connection, so it will close.",
                    L"The Dark Sign", MB_ICONERROR);
        TerminateProcess(GetCurrentProcess(), 1);
    }
    return;

fail:
    /* Better no game than a game that writes over the vanilla saves. */
    MessageBoxW(NULL, L"The Dark Sign could not redirect this game's saves, so it will close "
                      L"to keep your normal saves safe.", L"The Dark Sign", MB_ICONERROR);
    TerminateProcess(GetCurrentProcess(), 1);
}

/* ---- dinput8 proxy ---- */

typedef HRESULT (WINAPI *DirectInput8Create_t)(HINSTANCE, DWORD, REFIID, LPVOID *, IUnknown *);

static FARPROC real_dinput8(const char *name)
{
    static HMODULE real;
    if (!real) {
        wchar_t path[MAX_PATH];
        GetSystemDirectoryW(path, MAX_PATH);
        wcscat(path, L"\\dinput8.dll");
        real = LoadLibraryW(path);
    }
    return real ? GetProcAddress(real, name) : NULL;
}

__declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE inst, DWORD version, REFIID iid, LPVOID *out, IUnknown * outer)
{
    DirectInput8Create_t fn = (DirectInput8Create_t)real_dinput8("DirectInput8Create");
    return fn ? fn(inst, version, iid, out, outer) : E_FAIL;
}

/* Lets the launcher recognise its own DLL among other dinput8.dll mods. */
__declspec(dllexport) const char *DkSignSaveRedirectVersion(void) { return "dksign_saveredirect 1"; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        init();
    }
    return TRUE;
}
