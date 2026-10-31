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

typedef enum { DKS_OK, DKS_NOT_FOUND, DKS_NOT_LOADED } DksResult;

void dks_log(const char *fmt, ...);
void dks_open_log(void);

/* Items from the launcher's list (DKSIGN_ITEMS); returns the count. */
int dks_load_items(DksItem **out);

/* First match of a byte pattern ("48 8B ? ...") in the game's image, or 0. */
uintptr_t dks_find_in_game(const char *pattern_text);

/* Looks up the game addresses once; later calls return straight away. */
void dks_scan_game(void);

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

#ifdef __cplusplus
}
#endif

#endif
