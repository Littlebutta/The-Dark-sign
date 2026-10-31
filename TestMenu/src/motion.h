/* Gives ported weapons motion categories above 255 (see motion.c). */
#ifndef DKSIGN_MOTION_H
#define DKSIGN_MOTION_H

#ifdef __cplusplus
extern "C" {
#endif

/* The function that copies each hand's wepmotionCategory into the character's int cache (eldenring.exe 2.7.2 +4D1D60).
   Its first bytes are unique in the game. */
#define PAT_MOTION_CACHE_UPDATE "48 89 6C 24 20 56 48 83 EC 20 48 8B EA 48 8B F1 48 85 D2 0F 84 ? ? ? ?"
/* The getter it calls for each hand (+3BEAF0), whole body up to "movzx eax, byte [rax+E7]": its first bytes are shared
   with sibling getters for other fields. The weapon-ID getter for a hand slot (-2 left, -1 right) sits 0xB0 before it
   (+3BEA40) and is checked against its own start, WEAPON_ID_OF_START. */
#define PAT_MOTION_GETTER "40 53 48 83 EC 50 8D 42 02 48 8B D9 83 F8 07 77 6E E8 ? ? ? ? 48 63 D0 48 8D 4C 24 38 C7 44 24 20 FF FF FF FF 48 C7 44 24 28 00 00 00 00 C7 44 24 30 FF FF FF FF 8B 5C 93 7C 83 CA FF C7 44 24 38 FF FF FF FF 48 C7 44 24 40 00 00 00 00 E8 ? ? ? ? 8B D3 C7 44 24 48 00 00 00 00 48 8D 4C 24 20 E8 ? ? ? ? 48 8B 44 24 28 48 85 C0 74 0D 0F B6 80 E7 00 00 00"
#define WEAPON_ID_OF_BEFORE_GETTER 0xB0
#define WEAPON_ID_OF_START "\x40\x53\x48\x83\xEC\x20\x48\x8B\xD9\xE8"

/* Reads Mods\ER\motion-categories.txt and hooks the cache; needs MinHook initialised. */
void dks_motion_init(void);

#ifdef __cplusplus
}
#endif

#endif
