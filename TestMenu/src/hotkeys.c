/* Keyboard shortcuts that work without the F1 overlay: the fallback when the overlay doesn't draw or crashes the
   game (launcher Fallbacks window, "F1 test menu" B turns the overlay off and leaves these). They only act while
   the game window has focus, and beep once when the game took the request (twice when it couldn't):
     Ctrl+Shift+F1        start the DS3 run (Cemetery of Ash)
     Ctrl+Shift+F2        leave the DS3 run (back to Roundtable Hold)
     Ctrl+Shift+1 ... 9   warp to ported map 1-9 (the order of darksign_maps.tsv, the F1 tab's list)
     Ctrl+Shift+F3        give every ported Dark Souls III weapon (+0, no affinity)
   The flags are the ones the F1 menu sets (game.h, RunEvents.cs). */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "game.h"
#include "hotkeys.h"

#define MAX_WARPS 9

static uint32_t g_warp_flags[MAX_WARPS];
static int g_warp_count;

static void load_warps(void)
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH);
    wchar_t *slash = n && n < MAX_PATH ? wcsrchr(path, L'\\') : NULL;
    if (!slash || (size_t)(slash - path) + 20 >= MAX_PATH)
        return;
    wcscpy(slash + 1, L"darksign_maps.tsv");
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return;
    char line[512];
    while (g_warp_count < MAX_WARPS && fgets(line, sizeof line, f)) {
        if (line[0] == '#')
            continue;
        /* DS3 map, Elden Ring slot, name, warp flag */
        char *field = line;
        for (int i = 0; i < 3 && field; i++)
            field = strchr(field, '\t') ? strchr(field, '\t') + 1 : NULL;
        if (field)
            g_warp_flags[g_warp_count++] = (uint32_t)strtoul(field, NULL, 10);
    }
    fclose(f);
}

static int game_has_focus(void)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &pid);
    return pid == GetCurrentProcessId();
}

static int down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

static void report(int ok)
{
    MessageBeep(ok ? MB_OK : MB_ICONHAND);
    if (!ok) {
        Sleep(150);
        MessageBeep(MB_ICONHAND);
    }
}

static void request(uint32_t flag, const char *what)
{
    DksResult r = dks_set_event_flag(flag, 1);
    dks_log("hotkey: %s (flag %u): %s", what, flag, r == DKS_OK ? "set" : "not available");
    report(r == DKS_OK);
}

static void give_ds3_weapons(void)
{
    DksItem *items = NULL;
    int count = dks_load_items(&items), given = 0, failed = 0;
    for (int i = 0; i < count; i++) {
        if (strcmp(items[i].game, "DS3") || strcmp(items[i].kind, "Weapon"))
            continue;
        if (dks_give_item(dks_item_give_id(&items[i], 0, 0), 1) == DKS_OK)
            given++;
        else
            failed++;
    }
    free(items);
    dks_log("hotkey: gave %d Dark Souls III weapons (%d failed)", given, failed);
    report(given > 0);
}

static DWORD WINAPI watch(LPVOID unused)
{
    (void)unused;
    load_warps();
    dks_log("hotkeys: on (Ctrl+Shift+F1 start DS3 run, F2 leave, 1-%d warp, F3 give DS3 weapons)", g_warp_count);
    int was[16] = {0};
    for (;;) {
        Sleep(50);
        int combo = game_has_focus() && down(VK_CONTROL) && down(VK_SHIFT);
        int keys[] = {VK_F1, VK_F2, VK_F3, '1', '2', '3', '4', '5', '6', '7', '8', '9'};
        for (int k = 0; k < (int)(sizeof keys / sizeof keys[0]); k++) {
            int now = combo && down(keys[k]);
            if (now && !was[k]) {
                if (keys[k] == VK_F1)
                    request(DKS_FLAG_DS3_START_REQUEST, "start DS3 run");
                else if (keys[k] == VK_F2)
                    request(DKS_FLAG_DS3_LEAVE_REQUEST, "leave DS3 run");
                else if (keys[k] == VK_F3)
                    give_ds3_weapons();
                else if (keys[k] - '1' < g_warp_count && g_warp_flags[keys[k] - '1'])
                    request(g_warp_flags[keys[k] - '1'], "warp to a ported map");
                else
                    report(0);
            }
            was[k] = now;
        }
    }
    return 0;
}

void dks_hotkeys_start(void)
{
    /* The launcher sets this when the DLL is only loaded for another job (DLL sounds) and the test menu is off. */
    wchar_t off[8];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_NO_HOTKEYS", off, 8);
    if (n > 0 && n < 8 && off[0] == L'1')
        return;
    HANDLE thread = CreateThread(NULL, 0, watch, NULL, 0, NULL);
    if (thread)
        CloseHandle(thread);
}
