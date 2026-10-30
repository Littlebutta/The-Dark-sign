/* dksign_testmenu: The Dark Sign's Elden Ring test menu. ModEngine2 loads it into the game
   (external_dlls). F1 opens a window over the game that gives any item, vanilla or ported, and
   adds or sets runes. It is a testing aid for single-player, offline play only.

   Game addresses are found by byte pattern when the menu first opens (the same patterns Erd-Tools
   uses), so a game update that moves them disables the buttons instead of crashing the game. */
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "items.h"

#define PAT_GAME_DATA_MAN "48 8B 05 ? ? ? ? 48 85 C0 74 05 48 8B 40 58 C3 C3"
#define PAT_MAP_ITEM_MAN "48 8B 0D ? ? ? ? C7 44 24 50 FF FF FF FF C7 45 A0 FF FF FF FF 48 85 C9 75 2E"
#define PAT_ITEM_GIVE "8B 02 83 F8 0A"
#define ITEM_GIVE_OFFSET (-0x52)
#define PLAYER_GAME_DATA 0x8 /* GameDataMan -> PlayerGameData */
#define HELD_RUNES 0x6C      /* PlayerGameData -> runes held */
#define MAX_RUNES 999999999u
#define MAX_SHOWN 4000

enum { ID_SEARCH = 100, ID_LIST, ID_QTY, ID_UPG, ID_AFF, ID_GIVE, ID_RUNES, ID_ADD_RUNES, ID_SET_RUNES, ID_STATUS };

typedef void (*ItemGive)(void *map_item_man, void *entries, void *header, int unk);

static HANDLE g_log = INVALID_HANDLE_VALUE;
static DksItem *g_items;
static int g_item_count;
static int *g_shown;
static HWND g_menu, g_search, g_list, g_qty, g_upg, g_aff, g_runes, g_status;
static int g_scanned;
static uintptr_t g_game_data_man_slot, g_map_item_man_slot;
static ItemGive g_item_give;

static const wchar_t *const AFFINITIES[] = {L"Standard", L"Heavy", L"Keen", L"Quality", L"Fire", L"Flame Art",
    L"Lightning", L"Sacred", L"Magic", L"Cold", L"Poison", L"Blood", L"Occult"};

static void log_line(const wchar_t *fmt, ...)
{
    if (g_log == INVALID_HANDLE_VALUE)
        return;
    wchar_t buf[512];
    va_list args;
    va_start(args, fmt);
    int n = _vsnwprintf(buf, 510, fmt, args);
    va_end(args);
    if (n < 0)
        n = 510;
    buf[n++] = L'\r';
    buf[n++] = L'\n';
    char utf8[1024];
    int len = WideCharToMultiByte(CP_UTF8, 0, buf, n, utf8, sizeof utf8, NULL, NULL);
    DWORD written;
    WriteFile(g_log, utf8, (DWORD)len, &written, NULL);
}

/* Reads game memory without crashing if a pointer is stale. */
static int read_ptr(uintptr_t addr, uintptr_t *out)
{
    SIZE_T got = 0;
    return addr && ReadProcessMemory(GetCurrentProcess(), (LPCVOID)addr, out, sizeof *out, &got) && got == sizeof *out;
}

static uintptr_t find_in_game(const char *pattern_text)
{
    DksPattern pattern;
    if (!dks_parse_pattern(pattern_text, &pattern))
        return 0;
    uint8_t *base = (uint8_t *)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    uint8_t *end = base + nt->OptionalHeader.SizeOfImage;
    MEMORY_BASIC_INFORMATION mbi;
    for (uint8_t *at = base; at < end && VirtualQuery(at, &mbi, sizeof mbi); at = (uint8_t *)mbi.BaseAddress + mbi.RegionSize) {
        DWORD readable = PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_READONLY | PAGE_READWRITE;
        if (mbi.State != MEM_COMMIT || !(mbi.Protect & readable) || (mbi.Protect & PAGE_GUARD))
            continue;
        long long hit = dks_find_pattern((uint8_t *)mbi.BaseAddress, mbi.RegionSize, &pattern);
        if (hit >= 0)
            return (uintptr_t)mbi.BaseAddress + (uintptr_t)hit;
    }
    return 0;
}

/* "mov reg, [rip+rel32]": the address of the global it reads. */
static uintptr_t rip_target(uintptr_t instruction)
{
    return instruction ? instruction + 7 + *(int32_t *)(instruction + 3) : 0;
}

static void scan_game(void)
{
    if (g_scanned)
        return;
    g_scanned = 1;
    g_game_data_man_slot = rip_target(find_in_game(PAT_GAME_DATA_MAN));
    g_map_item_man_slot = rip_target(find_in_game(PAT_MAP_ITEM_MAN));
    uintptr_t give = find_in_game(PAT_ITEM_GIVE);
    g_item_give = give ? (ItemGive)(give + ITEM_GIVE_OFFSET) : NULL;
    log_line(L"scan: GameDataMan %p, MapItemMan %p, ItemGive %p", (void *)g_game_data_man_slot,
             (void *)g_map_item_man_slot, (void *)g_item_give);
}

static uintptr_t player_game_data(void)
{
    uintptr_t gdm, pgd;
    if (!read_ptr(g_game_data_man_slot, &gdm) || !read_ptr(gdm + PLAYER_GAME_DATA, &pgd))
        return 0;
    return pgd;
}

static void set_status(const wchar_t *fmt, ...)
{
    wchar_t buf[256];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf(buf, 255, fmt, args);
    va_end(args);
    buf[255] = 0;
    SetWindowTextW(g_status, buf);
    log_line(L"%ls", buf);
}

static long long read_number(HWND edit)
{
    wchar_t text[32];
    GetWindowTextW(edit, text, 32);
    return _wtoi64(text);
}

static void load_items(void)
{
    wchar_t path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH))
        return;
    FILE *f = _wfopen(path, L"rb");
    if (!f) {
        log_line(L"items: cannot open %ls", path);
        return;
    }
    int cap = 4096;
    g_items = malloc(sizeof *g_items * cap);
    char line[512];
    while (g_items && fgets(line, sizeof line, f)) {
        if (g_item_count == cap) {
            DksItem *grown = realloc(g_items, sizeof *g_items * (cap *= 2));
            if (!grown)
                break;
            g_items = grown;
        }
        if (dks_parse_item_line(line, &g_items[g_item_count]))
            g_item_count++;
    }
    fclose(f);
    g_shown = malloc(sizeof *g_shown * (g_item_count ? g_item_count : 1));
    log_line(L"items: %d from %ls", g_item_count, path);
}

static void refilter(void)
{
    wchar_t wquery[96];
    char query[288];
    GetWindowTextW(g_search, wquery, 96);
    WideCharToMultiByte(CP_UTF8, 0, wquery, -1, query, sizeof query, NULL, NULL);

    SendMessageW(g_list, WM_SETREDRAW, FALSE, 0);
    SendMessageW(g_list, LB_RESETCONTENT, 0, 0);
    int matches = 0;
    for (int i = 0; i < g_item_count; i++) {
        if (!dks_name_matches(g_items[i].name, query))
            continue;
        if (matches < MAX_SHOWN) {
            wchar_t name[DKS_NAME_MAX], text[DKS_NAME_MAX + 24];
            MultiByteToWideChar(CP_UTF8, 0, g_items[i].name, -1, name, DKS_NAME_MAX);
            wchar_t kind[16];
            MultiByteToWideChar(CP_UTF8, 0, g_items[i].kind, -1, kind, 16);
            _snwprintf(text, DKS_NAME_MAX + 24, L"%ls   [%ls]", name, kind);
            text[DKS_NAME_MAX + 23] = 0;
            int row = (int)SendMessageW(g_list, LB_ADDSTRING, 0, (LPARAM)text);
            SendMessageW(g_list, LB_SETITEMDATA, row, i);
            g_shown[matches] = i;
        }
        matches++;
    }
    SendMessageW(g_list, WM_SETREDRAW, TRUE, 0);
    InvalidateRect(g_list, NULL, TRUE);
    if (!g_item_count)
        set_status(L"No item list. Start Elden Ring from The Dark Sign launcher.");
    else
        set_status(matches > MAX_SHOWN ? L"%d matches (first %d shown). Type to narrow it down." : L"%d matches.",
                   matches, MAX_SHOWN);
}

static void give_selected(void)
{
    int row = (int)SendMessageW(g_list, LB_GETCURSEL, 0, 0);
    if (row < 0) {
        set_status(L"Pick an item first.");
        return;
    }
    const DksItem *item = &g_items[SendMessageW(g_list, LB_GETITEMDATA, row, 0)];
    if (!g_item_give || !g_map_item_man_slot) {
        set_status(L"Giving items is not available in this game version.");
        return;
    }
    uintptr_t map_item_man;
    if (!player_game_data() || !read_ptr(g_map_item_man_slot, &map_item_man) || !map_item_man) {
        set_status(L"Load your character first.");
        return;
    }
    long long qty = read_number(g_qty);
    if (qty < 1)
        qty = 1;
    if (qty > 999)
        qty = 999;
    int affinity = (int)SendMessageW(g_aff, CB_GETCURSEL, 0, 0);
    uint32_t id = dks_item_give_id(item, affinity, (int)read_number(g_upg));

    /* The game's item-give request: a header, then a count and one 0x10-byte entry. */
    uint8_t request[0x40] = {0};
    *(int32_t *)(request + 0x20) = 1;
    *(uint32_t *)(request + 0x24) = id;
    *(int32_t *)(request + 0x28) = (int32_t)qty;
    *(int32_t *)(request + 0x30) = -1; /* no Ash of War */
    g_item_give((void *)map_item_man, request + 0x20, request, 0);

    wchar_t name[DKS_NAME_MAX];
    MultiByteToWideChar(CP_UTF8, 0, item->name, -1, name, DKS_NAME_MAX);
    set_status(L"Gave %lld x %ls (ID %08X).", qty, name, id);
}

static void change_runes(int add)
{
    uintptr_t pgd = player_game_data();
    if (!pgd) {
        set_status(g_game_data_man_slot ? L"Load your character first." : L"Runes are not available in this game version.");
        return;
    }
    uint32_t *held = (uint32_t *)(pgd + HELD_RUNES);
    long long amount = read_number(g_runes);
    long long total = add ? (long long)*held + amount : amount;
    if (total < 0)
        total = 0;
    if (total > MAX_RUNES)
        total = MAX_RUNES;
    *held = (uint32_t)total;
    set_status(L"Runes held: %lld.", total);
}

static HWND add_control(const wchar_t *cls, const wchar_t *text, DWORD style, int x, int y, int w, int h, int id)
{
    HWND hwnd = CreateWindowExW(cls[0] == L'E' || cls[0] == L'L' ? WS_EX_CLIENTEDGE : 0, cls, text,
                                WS_CHILD | WS_VISIBLE | style, x, y, w, h, g_menu, (HMENU)(INT_PTR)id, NULL, NULL);
    SendMessageW(hwnd, WM_SETFONT, (WPARAM)GetStockObject(DEFAULT_GUI_FONT), TRUE);
    return hwnd;
}

static LRESULT CALLBACK menu_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg) {
    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case ID_SEARCH:
            if (HIWORD(wp) == EN_CHANGE)
                refilter();
            return 0;
        case ID_LIST:
            if (HIWORD(wp) == LBN_DBLCLK)
                give_selected();
            return 0;
        case IDOK:
        case ID_GIVE:
            give_selected();
            return 0;
        case ID_ADD_RUNES:
            change_runes(1);
            return 0;
        case ID_SET_RUNES:
            change_runes(0);
            return 0;
        case IDCANCEL:
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        break;
    case WM_CLOSE:
        ShowWindow(hwnd, SW_HIDE);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static void create_menu(void)
{
    WNDCLASSW wc = {0};
    wc.lpfnWndProc = menu_proc;
    wc.hInstance = GetModuleHandleW(NULL);
    wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    wc.lpszClassName = L"DarkSignTestMenu";
    RegisterClassW(&wc);

    RECT rc = {0, 0, 480, 520};
    DWORD style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
    AdjustWindowRectEx(&rc, style, FALSE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT);
    g_menu = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT, wc.lpszClassName,
                             L"The Dark Sign test menu (F1)", style, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
                             NULL, NULL, wc.hInstance, NULL);

    add_control(L"STATIC", L"Search", 0, 12, 15, 60, 20, 0);
    g_search = add_control(L"EDIT", L"", WS_TABSTOP | ES_AUTOHSCROLL, 80, 12, 388, 22, ID_SEARCH);
    g_list = add_control(L"LISTBOX", L"", WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT, 12, 42, 456, 300, ID_LIST);

    add_control(L"STATIC", L"Quantity", 0, 12, 355, 60, 20, 0);
    g_qty = add_control(L"EDIT", L"1", WS_TABSTOP | ES_NUMBER, 80, 352, 50, 22, ID_QTY);
    add_control(L"STATIC", L"Upgrade +", 0, 142, 355, 60, 20, 0);
    g_upg = add_control(L"EDIT", L"0", WS_TABSTOP | ES_NUMBER, 205, 352, 40, 22, ID_UPG);
    add_control(L"STATIC", L"Affinity", 0, 257, 355, 50, 20, 0);
    g_aff = add_control(L"COMBOBOX", L"", WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL, 310, 352, 158, 300, ID_AFF);
    for (int i = 0; i < 13; i++)
        SendMessageW(g_aff, CB_ADDSTRING, 0, (LPARAM)AFFINITIES[i]);
    SendMessageW(g_aff, CB_SETCURSEL, 0, 0);
    add_control(L"BUTTON", L"Give item", WS_TABSTOP | BS_DEFPUSHBUTTON, 12, 384, 456, 30, ID_GIVE);

    add_control(L"STATIC", L"Runes", 0, 12, 433, 60, 20, 0);
    g_runes = add_control(L"EDIT", L"1000000", WS_TABSTOP | ES_NUMBER, 80, 430, 150, 22, ID_RUNES);
    add_control(L"BUTTON", L"Add runes", WS_TABSTOP, 240, 428, 110, 26, ID_ADD_RUNES);
    add_control(L"BUTTON", L"Set runes", WS_TABSTOP, 358, 428, 110, 26, ID_SET_RUNES);

    g_status = add_control(L"STATIC", L"", SS_LEFTNOWORDWRAP, 12, 468, 456, 20, ID_STATUS);
    add_control(L"STATIC", L"F1 or Esc closes this menu. Weapons: upgrade and affinity apply when the weapon allows them.",
                0, 12, 492, 456, 20, 0);
    refilter();
}

static int is_our_process(HWND hwnd)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    return hwnd && pid == GetCurrentProcessId();
}

static void toggle_menu(void)
{
    if (IsWindowVisible(g_menu)) {
        ShowWindow(g_menu, SW_HIDE);
        return;
    }
    scan_game();
    HWND game = GetForegroundWindow();
    RECT g, m;
    GetWindowRect(game, &g);
    GetWindowRect(g_menu, &m);
    int w = m.right - m.left, h = m.bottom - m.top;
    SetWindowPos(g_menu, HWND_TOPMOST, g.left + (g.right - g.left - w) / 2, g.top + (g.bottom - g.top - h) / 2, 0, 0,
                 SWP_NOSIZE | SWP_SHOWWINDOW);
    ClipCursor(NULL);
    while (ShowCursor(TRUE) < 0)
        ;
    SetForegroundWindow(g_menu);
    SetFocus(g_search);
    SendMessageW(g_search, EM_SETSEL, 0, -1);
}

static DWORD WINAPI menu_thread(LPVOID unused)
{
    load_items();
    create_menu();
    int was_down = 0;
    for (;;) {
        MSG msg;
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
            if (!IsDialogMessageW(g_menu, &msg)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        /* F1 counts only while the game or this menu has focus, so other programs keep their F1. */
        int down = (GetAsyncKeyState(VK_F1) & 0x8000) != 0;
        if (down && !was_down && is_our_process(GetForegroundWindow()))
            toggle_menu();
        was_down = down;
        MsgWaitForMultipleObjects(0, NULL, FALSE, 30, QS_ALLINPUT);
    }
    return 0;
}

static int is_elden_ring(void)
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    const wchar_t *name = wcsrchr(exe, L'\\');
    return _wcsicmp(name ? name + 1 : exe, L"eldenring.exe") == 0;
}

static void open_log(void)
{
    wchar_t dir[MAX_PATH], path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_SAVE_DIR", dir, MAX_PATH))
        return;
    _snwprintf(path, MAX_PATH, L"%ls\\dksign_testmenu.log", dir);
    path[MAX_PATH - 1] = 0;
    g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

/* Lets the launcher recognise this DLL. */
__declspec(dllexport) const char *DkSignTestMenuVersion(void) { return "dksign_testmenu 1"; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        if (!is_elden_ring())
            return TRUE;
        open_log();
        log_line(L"test menu loaded; press F1 in game");
        HANDLE thread = CreateThread(NULL, 0, menu_thread, NULL, 0, NULL);
        if (thread)
            CloseHandle(thread);
    }
    return TRUE;
}
