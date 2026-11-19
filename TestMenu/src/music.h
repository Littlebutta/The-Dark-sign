/* DS3's music on the ported maps: the DLL plays the DS3 track whose map flag is on (see music.cpp). */
#ifndef DKSIGN_MUSIC_H
#define DKSIGN_MUSIC_H

#ifdef __cplusplus
extern "C" {
#endif

/* Starts the music thread when the porter wrote darksign_music.tsv (unless DKSIGN_NO_MAP_MUSIC=1). */
void dks_music_init(void);

#ifdef __cplusplus
}
#endif

#endif
