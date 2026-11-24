/* "Enemies ignore the player" (see hide.h). */
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "game.h"
#include "hide.h"

/* Hexinton's table: aobscanmodule(invisone, eldenring.exe, 74 ? 44 88 b9 ? ? ? ? e9), then "nop 2" at the match.
   In the game: cmp [rcx+0BA],r15b / je +0C / mov [rcx+0BA],r15b / jmp <end>; with the je gone the update always takes
   the early exit. The longer pattern below pins the cmp in front as well (one match in the exe, checked 2026-11-24). */
#define PAT_HIDE "44 38 B9 BA 00 00 00 74 ?? 44 88 B9 BA 00 00 00 E9"
#define HIDE_PATCH_OFFSET 7

static volatile LONG g_hide = -1; /* -1: DKSIGN_HIDE not read yet */
static SRWLOCK g_lock = SRWLOCK_INIT;
static uintptr_t g_site;          /* address of the je, 0 = not looked up / not found */
static int g_looked_up;
static uint8_t g_original[2];     /* the je as found */
static int g_applied;             /* 1 = our NOPs are in */
static char g_status[160] = "Not used yet.";

int dks_hide_on(void)
{
    if (g_hide < 0) {
        wchar_t v[8];
        DWORD n = GetEnvironmentVariableW(L"DKSIGN_HIDE", v, 8);
        if (InterlockedCompareExchange(&g_hide, n > 0 && n < 8 && v[0] == L'1', -1) == -1 && g_hide > 0)
            dks_log("enemies ignore player: on at launch (DKSIGN_HIDE=1)");
    }
    return g_hide > 0;
}

void dks_hide_set(int on)
{
    dks_hide_on();
    InterlockedExchange(&g_hide, on ? 1 : 0);
    dks_log("enemies ignore player: %s", on ? "on" : "off");
}

const char *dks_hide_status(void) { return g_status; }

static int write_code(uintptr_t at, const uint8_t *bytes, size_t n)
{
    DWORD old = 0;
    if (!VirtualProtect((void *)at, n, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    memcpy((void *)at, bytes, n);
    DWORD ignored;
    VirtualProtect((void *)at, n, old, &ignored);
    FlushInstructionCache(GetCurrentProcess(), (void *)at, n);
    return 1;
}

static void set_status(const char *text)
{
    lstrcpynA(g_status, text, sizeof g_status);
}

/* Caller holds g_lock. */
static void look_up(void)
{
    g_looked_up = 1;
    uintptr_t hit = dks_find_in_game(PAT_HIDE);
    if (!hit) {
        set_status("Not available: the game code it patches wasn't found (another game version?).");
        dks_log("enemies ignore player: pattern not found, toggle does nothing");
        return;
    }
    uintptr_t site = hit + HIDE_PATCH_OFFSET;
    memcpy(g_original, (void *)site, 2);
    if (g_original[0] != 0x74) {
        set_status("Not available: unexpected bytes at the patch site (left alone).");
        dks_log("enemies ignore player: bytes at +%llx are %02x %02x, not a short je; left alone",
                (unsigned long long)(site - (uintptr_t)GetModuleHandleW(NULL)), g_original[0], g_original[1]);
        return;
    }
    g_site = site;
    dks_log("enemies ignore player: patch site at eldenring.exe+%llx (%02x %02x)",
            (unsigned long long)(site - (uintptr_t)GetModuleHandleW(NULL)), g_original[0], g_original[1]);
}

void dks_hide_update(void)
{
    int on = dks_hide_on();
    if (on == g_applied)
        return;
    AcquireSRWLockExclusive(&g_lock);
    if (on != g_applied) {
        if (!g_looked_up)
            look_up();
        if (g_site) {
            static const uint8_t nops[2] = {0x90, 0x90};
            const uint8_t *want = on ? nops : g_original;
            if (write_code(g_site, want, 2)) {
                g_applied = on;
                set_status(on ? "On: enemies don't notice you." : "Off.");
                dks_log("enemies ignore player: %s (bytes %02x %02x)", on ? "applied" : "removed", want[0], want[1]);
            } else {
                set_status("Couldn't write the game code (VirtualProtect failed).");
                dks_log("enemies ignore player: VirtualProtect failed (%lu)", GetLastError());
                InterlockedExchange(&g_hide, g_applied); /* don't retry every frame */
            }
        } else {
            InterlockedExchange(&g_hide, 0); /* not available: show the checkbox as off */
        }
    }
    ReleaseSRWLockExclusive(&g_lock);
}
