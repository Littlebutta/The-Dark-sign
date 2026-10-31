/* Gives ported weapons motion categories above 255 (see motion.c). */
#ifndef DKSIGN_MOTION_H
#define DKSIGN_MOTION_H

#ifdef __cplusplus
extern "C" {
#endif

/* The function that copies each hand's wepmotionCategory into the character's int cache (eldenring.exe 2.7.2 +4D1D60)
   and the one that gives the weapon ID in a hand slot (-2 left, -1 right; +3BEA40). */
#define PAT_MOTION_CACHE_UPDATE "TODO"
#define PAT_WEAPON_ID_OF "TODO"

/* Reads Mods\ER\motion-categories.txt and hooks the cache; needs MinHook initialised. */
void dks_motion_init(void);

#ifdef __cplusplus
}
#endif

#endif
