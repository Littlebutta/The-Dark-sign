/* End-to-end check of the hooked file APIs: built as eldenring.exe (the DLL only acts in a game exe), it loads
   dksign_saveredirect.dll with DKSIGN_SAVE_DIR set and does what a game does to its save folder through each API
   variant. Run under Windows or wine: `make -C SaveRedirect test-hooks` (needs mingw and wine). */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

static int failures;
#define CHECK(cond) do { if (!(cond)) { failures++; printf("FAIL %s:%d %s (error %lu)\n", __FILE__, __LINE__, #cond, GetLastError()); } } while (0)

static wchar_t vanilla[MAX_PATH], saves[MAX_PATH];

static void path(wchar_t *out, const wchar_t *dir, const wchar_t *name)
{
    _snwprintf(out, MAX_PATH, L"%ls\\%ls", dir, name);
}

static int exists(const wchar_t *dir, const wchar_t *name)
{
    wchar_t p[MAX_PATH];
    path(p, dir, name);
    /* Saves-folder probes go through the hook too, but saves isn't under the vanilla folder, so nothing changes. */
    return GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES;
}

static void write_file(const wchar_t *p)
{
    HANDLE h = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    CHECK(h != INVALID_HANDLE_VALUE);
    DWORD n;
    WriteFile(h, "save", 4, &n, NULL);
    CloseHandle(h);
}

int main(void)
{
    wchar_t appdata[MAX_PATH], temp[MAX_PATH];
    GetEnvironmentVariableW(L"APPDATA", appdata, MAX_PATH);
    GetTempPathW(MAX_PATH, temp);
    _snwprintf(vanilla, MAX_PATH, L"%ls\\EldenRing\\76561190000000000", appdata);
    _snwprintf(saves, MAX_PATH, L"%lsdksign_test_saves", temp);
    SetEnvironmentVariableW(L"DKSIGN_SAVE_DIR", saves);
    SetEnvironmentVariableW(L"DKSIGN_SAVE_EXT", L".dksign");
    SetEnvironmentVariableW(L"DKSIGN_COOP", L"1"); /* leave sockets alone in the test */

    HMODULE dll = LoadLibraryW(L"dksign_saveredirect.dll");
    CHECK(dll != NULL);
    if (!dll)
        return 1;

    wchar_t p[MAX_PATH], q[MAX_PATH], saves_sub[MAX_PATH];
    _snwprintf(saves_sub, MAX_PATH, L"%ls\\76561190000000000", saves);

    /* The steam-id folder and the save, created through the vanilla path. */
    CHECK(CreateDirectoryW(vanilla, NULL) || GetLastError() == ERROR_ALREADY_EXISTS);
    path(p, vanilla, L"ER0000.sl2");
    write_file(p);
    CHECK(exists(saves_sub, L"ER0000.dksign"));
    CHECK(GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES); /* the game still sees "its" file */

    /* The long-path prefix and forward slashes. */
    _snwprintf(q, MAX_PATH, L"\\\\?\\%ls\\ER0000.sl2", vanilla);
    CHECK(GetFileAttributesW(q) != INVALID_FILE_ATTRIBUTES);

    /* Copy, move and replace variants for the .bak and temp files. */
    path(q, vanilla, L"ER0000.sl2.bak");
    CHECK(CopyFileW(p, q, FALSE));
    CHECK(exists(saves_sub, L"ER0000.dksign.bak"));
    path(q, vanilla, L"ER0000.sl2.tmp1");
    CHECK(CopyFileExW(p, q, NULL, NULL, NULL, 0));
    CHECK(exists(saves_sub, L"ER0000.dksign.tmp1"));
    wchar_t r[MAX_PATH];
    path(r, vanilla, L"ER0000.sl2.tmp2");
    CHECK(MoveFileW(q, r));
    CHECK(exists(saves_sub, L"ER0000.dksign.tmp2"));
    path(q, vanilla, L"ER0000.sl2.tmp3");
    CHECK(MoveFileExW(r, q, MOVEFILE_REPLACE_EXISTING));
    CHECK(exists(saves_sub, L"ER0000.dksign.tmp3"));
    CHECK(MoveFileWithProgressW(q, r, NULL, NULL, MOVEFILE_REPLACE_EXISTING));
    CHECK(exists(saves_sub, L"ER0000.dksign.tmp2"));
    CHECK(DeleteFileW(r));
    CHECK(!exists(saves_sub, L"ER0000.dksign.tmp2"));

    /* Seamless Co-op's save keeps its name. */
    path(q, vanilla, L"ER0000.co2");
    write_file(q);
    CHECK(exists(saves_sub, L"ER0000.co2"));

    /* Listing the folder shows the vanilla names, through both find functions. */
    for (int ex = 0; ex < 2; ex++) {
        WIN32_FIND_DATAW fd;
        path(q, vanilla, L"*.sl2");
        HANDLE h = ex ? FindFirstFileExW(q, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, 0) : FindFirstFileW(q, &fd);
        CHECK(h != INVALID_HANDLE_VALUE);
        if (h != INVALID_HANDLE_VALUE) {
            int seen = 0;
            do
                seen += wcscmp(fd.cFileName, L"ER0000.sl2") == 0;
            while (FindNextFileW(h, &fd));
            FindClose(h);
            CHECK(seen == 1);
        }
    }

    /* (The make target checks that nothing reached the real vanilla folder.) */
    CHECK(exists(saves, L"dksign_saveredirect.log"));

    printf(failures ? "%d failed\n" : "hooks: all passed\n", failures);
    return failures != 0;
}
