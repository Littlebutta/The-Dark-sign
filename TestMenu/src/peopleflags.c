/* In-game log of the placed DS3 people's event flags (RULE 4; thread "Stop Firelink characters attacking the player").
   DS3 turns a person hostile only through saved flags: quest-state ranges set inside their events, and the hostile
   flag DS3's common hostility event (20006000) turns on when they're hit or a talk flag is on. On 1.0.640 Sirris and
   Eygon at Firelink were on DS3's friendly team and still attacked, and a save's flags can't be read from outside the
   game, so this asks the game: once a character is loaded, every flag in Mods\ER\darksign_people_flags.txt (written
   by port-maps or "map-diag people-flags", "<ER flag>\t<who, event, line, argument>") is logged once if it is on,
   then every change after that, with the seconds since the load:
     people: at load, flag 1030290269 ON (c0000_9000 Sirris (45000780), m45_00_00_00 event ...)
     people: 13.2 s, flag 1030290269 off -> ON (...)
   A return to the title screen and a new load log the states again. Reads only; never sets a flag. */
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
        if (flags[count])
            count++;
    }
    fclose(f);
    dks_log("people: watching %d quest/hostility flags of the placed people%s", count, skipped ? " (list longer than the log holds; the rest aren't watched)" : "");
    if (!count)
        return 0;

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
            for (int i = 0; i < count; i++) {
                int now = dks_get_event_flag(flags[i]);
                if (now < 0 || now == state[i])
                    continue;
                dks_log("people: %.1f s, flag %u %s (%s)", (GetTickCount() - loaded) / 1000.0, flags[i],
                        now ? "off -> ON" : "ON -> off", what[i]);
                state[i] = (signed char)now;
            }
        }
        dks_log("people: character unloaded; flags are logged again at the next load");
    }
}

void dks_peopleflags_start(void)
{
    HANDLE thread = CreateThread(NULL, 0, run, NULL, 0, NULL);
    if (thread)
        CloseHandle(thread);
}
