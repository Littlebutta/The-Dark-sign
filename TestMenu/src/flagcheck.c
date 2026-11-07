/* In-game check of the event flag blocks the port uses (audit 2026-11-05, open risk "ER flag allocation").
   Elden Ring only keeps a flag when its 1000-flag block exists; one that doesn't is silently never set. Which blocks
   exist can't be read reliably from the game files (the eventflagalloclist format is undocumented), so the game is
   asked directly: once a character is loaded, every probe flag in Mods\ER\darksign_flag_blocks.txt (written by
   port-maps, "<probe flag>\t<what uses the block>") is read, and if it is off, set, read back and cleared again.
   The result goes to dksign_testmenu.log as "flagcheck:" lines, which the launcher's pre-flight check shows.
   Probe flags are the last number of each block (block * 1000 + 999), which nothing the port writes uses. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flagcheck.h"
#include "game.h"

#define MAX_PROBES 64

static DWORD WINAPI run(LPVOID unused)
{
    (void)unused;
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_MOD_DIR", path, MAX_PATH);
    if (!n || n + 32 >= MAX_PATH)
        return 0;
    wcscat(path, L"\\darksign_flag_blocks.txt");
    FILE *f = _wfopen(path, L"rb");
    if (!f) {
        dks_log("flagcheck: no darksign_flag_blocks.txt (port-maps hasn't run); skipped");
        return 0;
    }
    uint32_t probes[MAX_PROBES];
    char what[MAX_PROBES][96];
    int count = 0;
    char line[256];
    while (count < MAX_PROBES && fgets(line, sizeof line, f)) {
        if (line[0] == '#' || line[0] < '0' || line[0] > '9')
            continue;
        char *tab = strchr(line, '\t');
        probes[count] = (uint32_t)strtoul(line, NULL, 10);
        snprintf(what[count], sizeof what[count], "%s", tab ? tab + 1 : "");
        what[count][strcspn(what[count], "\r\n")] = 0;
        if (probes[count])
            count++;
    }
    fclose(f);

    /* Wait (up to an hour) for a loaded character: the flag manager only holds a save's blocks then. */
    for (int i = 0; i < 720 && dks_get_runes() < 0; i++)
        Sleep(5000);
    if (dks_get_runes() < 0) {
        dks_log("flagcheck: no character loaded within an hour; skipped");
        return 0;
    }
    Sleep(5000); /* let the load finish */
    int ok = 0, missing = 0;
    for (int i = 0; i < count; i++) {
        int before = dks_get_event_flag(probes[i]);
        if (before < 0) {
            dks_log("flagcheck: the game's flag functions weren't found; can't check");
            return 0;
        }
        int kept = before == 1;
        if (!kept) {
            dks_set_event_flag(probes[i], 1);
            kept = dks_get_event_flag(probes[i]) == 1;
            dks_set_event_flag(probes[i], 0);
        }
        dks_log("flagcheck: block %u (%s): %s", probes[i] / 1000, what[i],
                kept ? "ok, the game keeps flags here" : "MISSING, the game has no such block");
        kept ? ok++ : missing++;
    }
    dks_log("flagcheck: %d blocks ok, %d missing", ok, missing);
    return 0;
}

void dks_flagcheck_start(void)
{
    HANDLE thread = CreateThread(NULL, 0, run, NULL, 0, NULL);
    if (thread)
        CloseHandle(thread);
}
