#pragma once
/* "Enemies ignore the player" for testing (F1 menu checkbox, Ctrl+Shift+F9, or DKSIGN_HIDE=1 at launch), so a test run
   can walk through a map without pulling its regular enemies. While on, enemies don't notice the player. It acts on
   every enemy, spawned ones and bosses too, so turn it off to test a fight. Whether an enemy that is already fighting
   lets go is untested.

   How: the community "Hide From Enemies" switch in Hexinton's Elden Ring cheat table (v2.93, CREDITS.md): two bytes
   (a short je) in the game's character search update are replaced with NOPs, and the original bytes are put back when
   it is turned off. The byte pattern is looked up once and was checked to be unique in the game's exe (2026-11-24).
   Nothing is written unless the bytes there are the original je (74 0C) or our NOPs (90 90).

   dks_hide_update applies the wanted state; the render thread calls it every frame (cheap once found). */
#ifdef __cplusplus
extern "C" {
#endif

void dks_hide_set(int on);
int dks_hide_on(void);
void dks_hide_update(void);
/* One line for the F1 menu: found / not found / applied. */
const char *dks_hide_status(void);

#ifdef __cplusplus
}
#endif
