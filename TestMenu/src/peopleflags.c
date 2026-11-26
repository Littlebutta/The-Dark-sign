/* In-game log of the placed DS3 people's event flags (RULE 4; thread "Stop Firelink characters attacking the player").
   DS3 turns a person hostile only through saved flags: quest-state ranges set inside their events, and the hostile
   flag DS3's common hostility event (20006000) turns on when they're hit or a talk flag is on. On 1.0.640 Sirris and
   Eygon at Firelink were on DS3's friendly team and still attacked, and a save's flags can't be read from outside the
   game, so this asks the game: once a character is loaded, every flag in Mods\ER\darksign_people_flags.txt (written
   by port-maps or "map-diag people-flags", "<ER flag>\t<who, event, line, argument>") is logged once if it is on,
   then every change after that, with the seconds since the load:
     people: at load, flag 1030290269 ON (c0000_9000 Sirris (45000780), m45_00_00_00 event ...)
     people: 13.2 s, flag 1030290269 off -> ON (...)
   A return to the title screen and a new load log the states again. The watch only reads.

   2026-11-26, Devin's save on 1.0.667: the hostile flags of Eygon, Irina, Cornyx and Karla and the "angry" flags of
   the Fire Keeper, Ludleth, Andre and Leonhard were all on in the save, and some one-of quest ranges had two flags
   on. No DS3 or ported script sets several of those: the save's bits were from before the flag table was renumbered
   (PR #285; see dks_ds3flags_reset_all). So Eygon kept attacking on DS3's friendly team. dks_peopleflags_reset (F1
   menu, DS3 run tab: "Reset DS3 people") turns every state flag of the list off (third column "state": the people's
   friendly/hostile/dead and quest-step ranges and the hostile/dead/talk flags passed to DS3's shared people events);
   the scripts then set each person up as on a new game at the next load. It is also the stand-in for DS3's
   absolution, which a test save can't reach.
   The save's flags arrive a few seconds after the character does (all of them in one read), so the first read says
   "0 on" and a burst follows; a burst is logged with a line saying so. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "peopleflags.h"
#include "game.h"

#define MAX_FLAGS 2048
#define WHAT_LEN 120

static uint32_t flags[MAX_FLAGS];
static char what[MAX_FLAGS][WHAT_LEN];
static signed char state[MAX_FLAGS];
static unsigned char is_state[MAX_FLAGS];
static volatile LONG g_count;

static DWORD WINAPI run(LPVOID unused)
{
    (void)unused;
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_MOD_DIR", path, MAX_PATH);
    if (!n || n + 32 >= MAX_PATH)
        return 0;
    wcscat(path, L"\\darksign_people_flags.txt");
    FILE *f = _wfopen(path, L"rb");
    if (!f) {
        dks_log("people: no darksign_people_flags.txt (port-maps or map-diag people-flags hasn't run); the people flag log is off");
        return 0;
    }
    int count = 0, skipped = 0;
    char line[512];
    while (fgets(line, sizeof line, f)) {
        if (line[0] < '0' || line[0] > '9')
            continue;
        if (count == MAX_FLAGS) {
            skipped++;
            continue;
        }
        char *tab = strchr(line, '\t');
        flags[count] = (uint32_t)strtoul(line, NULL, 10);
        snprintf(what[count], WHAT_LEN, "%s", tab ? tab + 1 : "");
        what[count][strcspn(what[count], "\r\n")] = 0;
        /* A third column "state" marks a flag the reset turns off; it isn't part of the description. */
        char *tag = tab ? strchr(tab + 1, '\t') : NULL;
        is_state[count] = tag && !strncmp(tag + 1, "state", 5);
        char *cut = strchr(what[count], '\t');
        if (cut)
            *cut = 0;
        if (flags[count])
            count++;
    }
    fclose(f);
    dks_log("people: watching %d quest/hostility flags of the placed people%s", count, skipped ? " (list longer than the log holds; the rest aren't watched)" : "");
    if (!count)
        return 0;
    InterlockedExchange(&g_count, count);

    for (;;) {
        /* Wait for a loaded character: the flag manager only holds a save's flags then. */
        while (dks_get_runes() < 0)
            Sleep(2000);
        Sleep(3000); /* let the load finish */
        if (dks_get_event_flag(flags[0]) < 0) {
            dks_log("people: the game's flag functions weren't found; the people flag log is off");
            return 0;
        }
        DWORD loaded = GetTickCount();
        int on = 0;
        for (int i = 0; i < count; i++) {
            state[i] = (signed char)dks_get_event_flag(flags[i]);
            if (state[i] == 1) {
                dks_log("people: at load, flag %u ON (%s)", flags[i], what[i]);
                on++;
            }
        }
        dks_log("people: at load, %d of %d people flags on (the rest off); changes follow", on, count);
        while (dks_get_runes() >= 0) {
            Sleep(250);
            int changed = 0;
            for (int i = 0; i < count; i++) {
                int now = dks_get_event_flag(flags[i]);
                if (now < 0 || now == state[i])
                    continue;
                dks_log("people: %.1f s, flag %u %s (%s)", (GetTickCount() - loaded) / 1000.0, flags[i],
                        now ? "off -> ON" : "ON -> off", what[i]);
                state[i] = (signed char)now;
                changed++;
            }
            if (changed >= 10)
                dks_log("people: %d flags changed in one read: the save's flags arriving, a map load, or the F1 reset", changed);
        }
        dks_log("people: character unloaded; flags are logged again at the next load");
    }
}

int dks_peopleflags_reset(void)
{
    int count = (int)g_count, cleared = 0, listed = 0;
    if (!count || dks_get_runes() < 0)
        return -1;
    for (int i = 0; i < count; i++) {
        if (!is_state[i])
            continue;
        listed++;
        if (dks_get_event_flag(flags[i]) != 1)
            continue;
        dks_set_event_flag(flags[i], 0);
        dks_log("people: reset turned flag %u off (%s)", flags[i], what[i]);
        cleared++;
    }
    dks_log("people: reset: %d of %d state flags were on and are off now; the scripts set the people up again at the next load", cleared, listed);
    return listed ? cleared : -1;
}

/* Every DS3 flag of the port's table (Mods\ER\darksign_flags.tsv, "flag\t<DS3>\t<Elden Ring>") off, and the
   once-per-save flags of DS3's new-game setup events (Ds3Setup.FirstDoneFlag, 7 of them) so those run again.
   Why: the table hands out Elden Ring numbers in order of first appearance, and PR #285 (24 Nov) put ranges first,
   which renumbered it; a save from before kept its bits, which then meant other DS3 flags (Fire Keeper and Andre
   "angry", Eygon hostile, two flags on in one-of quest ranges, Andre and the Handmaid switched off). Flags a map keeps
   in its own slot block (boss defeated...) aren't in the table and stay. */
int dks_ds3flags_reset_all(void)
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_MOD_DIR", path, MAX_PATH);
    if (!n || n + 32 >= MAX_PATH || dks_get_runes() < 0)
        return -1;
    wcscat(path, L"\\darksign_flags.tsv");
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return -1;
    int cleared = 0, listed = 0;
    char line[256];
    while (fgets(line, sizeof line, f)) {
        if (strncmp(line, "flag\t", 5))
            continue;
        char *er = strchr(line + 5, '\t');
        uint32_t flag = er ? (uint32_t)strtoul(er + 1, NULL, 10) : 0;
        if (!flag)
            continue;
        listed++;
        if (dks_get_event_flag(flag) == 1) {
            dks_set_event_flag(flag, 0);
            cleared++;
        }
    }
    fclose(f);
    for (uint32_t done = 1039290150u; done < 1039290157u; done++)
        if (dks_get_event_flag(done) == 1)
            dks_set_event_flag(done, 0);
    dks_log("people: reset all: %d of the table's %d DS3 flags were on and are off now, and DS3's new-game setup runs again at the next load", cleared, listed);
    return cleared;
}

void dks_peopleflags_start(void)
{
    HANDLE thread = CreateThread(NULL, 0, run, NULL, 0, NULL);
    if (thread)
        CloseHandle(thread);
}
