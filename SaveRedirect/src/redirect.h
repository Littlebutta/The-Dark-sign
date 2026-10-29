/* Path rewriting for dksign_saveredirect: maps a game's save folder onto the isolated
   TheDarkSign\Saves folder and swaps the .sl2 extension for .dksign. No Windows APIs here,
   so the logic can be tested on any platform. */
#ifndef DKSIGN_REDIRECT_H
#define DKSIGN_REDIRECT_H

#include <stddef.h>
#include <wchar.h>

#define DKS_MAX_PATH 1024

typedef struct {
    wchar_t vanilla_dir[DKS_MAX_PATH]; /* e.g. C:\Users\x\AppData\Roaming\DarkSoulsIII */
    wchar_t save_dir[DKS_MAX_PATH];    /* e.g. D:\The Dark sign\TheDarkSign\Saves\Default\DS3 */
    wchar_t save_ext[32];              /* ".dksign" */
} dks_config;

/* Writes the redirected path to `out` and returns 1 when `in` lies in the vanilla save folder.
   Returns 0 (and leaves `out` untouched) for any other path. */
int dks_redirect(const dks_config *cfg, const wchar_t *in, wchar_t *out, size_t out_len);

/* Turns a redirected file name back into the vanilla one ("DS30000.dksign" -> "DS30000.sl2"),
   in place, so a game listing its save folder sees the names it expects. */
void dks_restore_name(const dks_config *cfg, wchar_t *name, size_t name_len);

/* The game's short name (DSR, DS3, ER) from its exe file name, or NULL for any other exe. */
const wchar_t *dks_game_from_exe(const wchar_t *exe_path);

#endif
