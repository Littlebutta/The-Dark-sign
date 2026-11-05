/* Fallback "sounds" D: the DLL plays the ported DS3 sounds on its own audio stream (see sound.cpp). */
#ifndef DKSIGN_SOUND_H
#define DKSIGN_SOUND_H

#ifdef __cplusplus
extern "C" {
#endif

/* Hooks the game's TAE event execution when DKSIGN_DLL_SOUNDS=1; call after MH_Initialize. */
void dks_sound_init(void);

#ifdef __cplusplus
}
#endif

#endif
