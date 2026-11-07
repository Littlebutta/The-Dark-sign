/* Calls into Elden Ring for the test menu: find the game's item-give function and rune counter, give items,
   change runes. Addresses are found by byte pattern (the ones Erd-Tools uses) the first time they're needed, so a
   game update that moves them makes the calls report "not available" instead of crashing the game. */
#ifndef DKSIGN_GAME_H
#define DKSIGN_GAME_H

#include <stdint.h>

#include "items.h"

#ifdef __cplusplus
extern "C" {
#endif

/* DKS_NOT_KEPT: an event flag was set but reads back unchanged (the game has no block for that flag ID). */
typedef enum { DKS_OK, DKS_NOT_FOUND, DKS_NOT_LOADED, DKS_NOT_KEPT } DksResult;

void dks_log(const char *fmt, ...);
void dks_open_log(void);

/* Items from the launcher's list (DKSIGN_ITEMS); returns the count. */
int dks_load_items(DksItem **out);

/* First match of a byte pattern ("48 8B ? ...") in the game's image, or 0. */
uintptr_t dks_find_in_game(const char *pattern_text);

/* Like dks_find_in_game, but goes on to the next match until accept(hit, ctx) returns non-zero. */
uintptr_t dks_find_in_game_where(const char *pattern_text, int (*accept)(uintptr_t hit, void *ctx), void *ctx);

/* Looks up the game addresses once; later calls return straight away. Safe to call from any thread. */
void dks_scan_game(void);

/* Scans again for addresses the first scan missed (at most a few times in all), for the background thread. */
void dks_rescan_missing(void);

/* Waits until the game has a visible top-level window (its code is then unpacked and initialised); 0 on timeout. */
int dks_wait_for_game_window(unsigned long timeout_ms);

/* Gives quantity x id (an ID from dks_item_give_id). */
DksResult dks_give_item(uint32_t id, int quantity);

/* Runes the character holds, or -1 when no character is loaded. */
long long dks_get_runes(void);
DksResult dks_set_runes(long long runes);

/* The character's attributes (Vigor, Mind, Endurance, Strength, Dexterity, Intelligence, Faith, Arcane) and level. */
DksResult dks_get_attributes(int attributes[DKS_ATTRIBUTES], int *level);

/* Sets the attributes (clamped to 1-99) and the level that goes with them. Refuses with DKS_NOT_FOUND if the
   numbers read from the game don't add up to its level, which would mean the offsets are wrong for this version. */
DksResult dks_set_attributes(const int attributes[DKS_ATTRIBUTES]);

/* Event flags, read and set through the game's own event flag manager (CSFD4VirtualMemoryFlag) and the functions
   Erd-Tools calls. dks_get_event_flag returns 1 or 0, or -1 when the game code wasn't found or no flag manager exists
   yet (title screen). */
int dks_get_event_flag(uint32_t id);
DksResult dks_set_event_flag(uint32_t id, int on);

/* The "DS3 run" flags. They must match Porter/src/DarkSign.Porter/Maps/RunEvents.cs, whose events in common.emevd
   act on them. */
#define DKS_FLAG_DS3_RUN_ACTIVE 1039290100u
#define DKS_FLAG_DS3_START_REQUEST 1039292100u
#define DKS_FLAG_DS3_LEAVE_REQUEST 1039292101u

#ifdef __cplusplus
}
#endif

#endif
