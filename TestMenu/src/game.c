#include "game.h"

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#define PAT_GAME_DATA_MAN "48 8B 05 ? ? ? ? 48 85 C0 74 05 48 8B 40 58 C3 C3"
#define PAT_MAP_ITEM_MAN "48 8B 0D ? ? ? ? C7 44 24 50 FF FF FF FF C7 45 A0 FF FF FF FF 48 85 C9 75 2E"
#define PAT_ITEM_GIVE "8B 02 83 F8 0A"
#define ITEM_GIVE_OFFSET (-0x52)
#define PLAYER_GAME_DATA 0x8 /* GameDataMan -> PlayerGameData */
#define HELD_RUNES 0x6C      /* PlayerGameData -> runes held */
#define ATTRIBUTES 0x3C      /* PlayerGameData -> Vigor, then the other seven attributes as int32 */
#define LEVEL 0x68           /* PlayerGameData -> character level */
#define MAX_RUNES 999999999LL

typedef void (*ItemGive)(void *map_item_man, void *entries, void *header, int unk);

static HANDLE g_log = INVALID_HANDLE_VALUE;
static int g_scanned;
static uintptr_t g_game_data_man_slot, g_map_item_man_slot;
static ItemGive g_item_give;

void dks_open_log(void)
{
    wchar_t dir[MAX_PATH], path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_SAVE_DIR", dir, MAX_PATH))
        return;
    _snwprintf(path, MAX_PATH, L"%ls\\dksign_testmenu.log", dir);
    path[MAX_PATH - 1] = 0;
    g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

void dks_log(const char *fmt, ...)
{
    if (g_log == INVALID_HANDLE_VALUE)
        return;
    char buf[512];
    va_list args;
    va_start(args, fmt);
    int n = vsnprintf(buf, sizeof buf - 2, fmt, args);
    va_end(args);
    if (n < 0 || n > (int)sizeof buf - 3)
        n = (int)sizeof buf - 3;
    buf[n++] = '\r';
    buf[n++] = '\n';
    DWORD written;
    WriteFile(g_log, buf, (DWORD)n, &written, NULL);
}

int dks_load_items(DksItem **out)
{
    *out = NULL;
    wchar_t path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH))
        return 0;
    FILE *f = _wfopen(path, L"rb");
    if (!f) {
        dks_log("items: cannot open the item list");
        return 0;
    }
    int count = 0, cap = 4096;
    DksItem *items = malloc(sizeof *items * cap);
    char line[512];
    while (items && fgets(line, sizeof line, f)) {
        if (count == cap) {
            DksItem *grown = realloc(items, sizeof *items * (cap *= 2));
            if (!grown)
                break;
            items = grown;
        }
        if (dks_parse_item_line(line, &items[count]))
            count++;
    }
    fclose(f);
    *out = items;
    dks_log("items: %d", count);
    return count;
}

/* Reads game memory without crashing if a pointer is stale. */
static int read_ptr(uintptr_t addr, uintptr_t *out)
{
    SIZE_T got = 0;
    return addr && ReadProcessMemory(GetCurrentProcess(), (LPCVOID)addr, out, sizeof *out, &got) && got == sizeof *out;
}

/* Scans every readable region of the game's image. The regions are found through their allocation base rather
   than the PE header, which the game's protection may have altered in memory. */
uintptr_t dks_find_in_game(const char *pattern_text)
{
    DksPattern pattern;
    if (!dks_parse_pattern(pattern_text, &pattern))
        return 0;
    uint8_t *base = (uint8_t *)GetModuleHandleW(NULL);
    MEMORY_BASIC_INFORMATION mbi;
    size_t scanned = 0;
    for (uint8_t *at = base; VirtualQuery(at, &mbi, sizeof mbi) && mbi.AllocationBase == base;
         at = (uint8_t *)mbi.BaseAddress + mbi.RegionSize) {
        DWORD readable = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                         PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        if (mbi.State != MEM_COMMIT || !(mbi.Protect & readable) || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
            continue;
        scanned += mbi.RegionSize;
        long long hit = dks_find_pattern((uint8_t *)mbi.BaseAddress, mbi.RegionSize, &pattern);
        if (hit >= 0)
            return (uintptr_t)mbi.BaseAddress + (uintptr_t)hit;
    }
    dks_log("scan: pattern not found in %zu bytes: %s", scanned, pattern_text);
    return 0;
}

/* "mov reg, [rip+rel32]": the address of the global it reads. */
static uintptr_t rip_target(uintptr_t instruction)
{
    return instruction ? instruction + 7 + *(int32_t *)(instruction + 3) : 0;
}

void dks_scan_game(void)
{
    if (g_scanned)
        return;
    g_scanned = 1;
    uintptr_t base = (uintptr_t)GetModuleHandleW(NULL);
    g_game_data_man_slot = rip_target(dks_find_in_game(PAT_GAME_DATA_MAN));
    g_map_item_man_slot = rip_target(dks_find_in_game(PAT_MAP_ITEM_MAN));
    uintptr_t give = dks_find_in_game(PAT_ITEM_GIVE);
    g_item_give = give ? (ItemGive)(give + ITEM_GIVE_OFFSET) : NULL;
    dks_log("scan: base %p, GameDataMan +%llx, MapItemMan +%llx, ItemGive +%llx", (void *)base,
            g_game_data_man_slot ? (unsigned long long)(g_game_data_man_slot - base) : 0ULL,
            g_map_item_man_slot ? (unsigned long long)(g_map_item_man_slot - base) : 0ULL,
            g_item_give ? (unsigned long long)((uintptr_t)g_item_give - base) : 0ULL);
}

static uintptr_t player_game_data(void)
{
    uintptr_t gdm, pgd;
    if (!read_ptr(g_game_data_man_slot, &gdm) || !read_ptr(gdm + PLAYER_GAME_DATA, &pgd))
        return 0;
    return pgd;
}

DksResult dks_give_item(uint32_t id, int quantity)
{
    dks_scan_game();
    if (!g_item_give || !g_map_item_man_slot)
        return DKS_NOT_FOUND;
    uintptr_t map_item_man;
    if (!player_game_data() || !read_ptr(g_map_item_man_slot, &map_item_man) || !map_item_man)
        return DKS_NOT_LOADED;

    /* The game's item-give request: a header, then a count and one 0x10-byte entry. */
    uint8_t request[0x40] = {0};
    *(int32_t *)(request + 0x20) = 1;
    *(uint32_t *)(request + 0x24) = id;
    *(int32_t *)(request + 0x28) = quantity;
    *(int32_t *)(request + 0x30) = -1; /* no Ash of War */
    g_item_give((void *)map_item_man, request + 0x20, request, 0);
    dks_log("give: %08X x%d", id, quantity);
    return DKS_OK;
}

long long dks_get_runes(void)
{
    dks_scan_game();
    uintptr_t pgd = player_game_data();
    uintptr_t runes;
    if (!pgd || !read_ptr(pgd + HELD_RUNES, &runes))
        return -1;
    return (long long)(uint32_t)runes;
}

DksResult dks_set_runes(long long runes)
{
    dks_scan_game();
    if (!g_game_data_man_slot)
        return DKS_NOT_FOUND;
    uintptr_t pgd = player_game_data();
    if (!pgd)
        return DKS_NOT_LOADED;
    if (runes < 0)
        runes = 0;
    if (runes > MAX_RUNES)
        runes = MAX_RUNES;
    *(uint32_t *)(pgd + HELD_RUNES) = (uint32_t)runes;
    dks_log("runes: %lld", runes);
    return DKS_OK;
}

/* The attributes and level as the game holds them; 0 when no character is loaded or they don't add up. */
static uintptr_t read_attributes(int attributes[DKS_ATTRIBUTES], int *level)
{
    dks_scan_game();
    uintptr_t pgd = player_game_data();
    int32_t block[(LEVEL + 4 - ATTRIBUTES) / 4];
    SIZE_T got = 0;
    if (!pgd || !ReadProcessMemory(GetCurrentProcess(), (LPCVOID)(pgd + ATTRIBUTES), block, sizeof block, &got) || got != sizeof block)
        return 0;
    for (int i = 0; i < DKS_ATTRIBUTES; i++)
        attributes[i] = block[i];
    *level = block[(LEVEL - ATTRIBUTES) / 4];
    return pgd;
}

DksResult dks_get_attributes(int attributes[DKS_ATTRIBUTES], int *level)
{
    if (!g_game_data_man_slot && (dks_scan_game(), !g_game_data_man_slot))
        return DKS_NOT_FOUND;
    return read_attributes(attributes, level) ? DKS_OK : DKS_NOT_LOADED;
}

DksResult dks_set_attributes(const int attributes[DKS_ATTRIBUTES])
{
    int current[DKS_ATTRIBUTES], level;
    if (!g_game_data_man_slot && (dks_scan_game(), !g_game_data_man_slot))
        return DKS_NOT_FOUND;
    uintptr_t pgd = read_attributes(current, &level);
    if (!pgd)
        return DKS_NOT_LOADED;
    if (dks_level_for(current) != level) {
        dks_log("attributes: refused, level %d but attributes %d %d %d %d %d %d %d %d", level, current[0], current[1],
                current[2], current[3], current[4], current[5], current[6], current[7]);
        return DKS_NOT_FOUND;
    }
    for (int i = 0; i < DKS_ATTRIBUTES; i++) {
        int v = attributes[i] < 1 ? 1 : attributes[i] > DKS_ATTRIBUTE_MAX ? DKS_ATTRIBUTE_MAX : attributes[i];
        *(int32_t *)(pgd + ATTRIBUTES + 4 * i) = v;
    }
    *(int32_t *)(pgd + LEVEL) = dks_level_for(attributes);
    dks_log("attributes: level %d -> %d", level, dks_level_for(attributes));
    return DKS_OK;
}
