/* In-game log of the placed people's quest and hostility flags (see peopleflags.c). */
#ifndef DKSIGN_PEOPLEFLAGS_H
#define DKSIGN_PEOPLEFLAGS_H

#ifdef __cplusplus
extern "C" {
#endif

void dks_peopleflags_start(void);
/* Turns every state flag of the list off (see peopleflags.c). Returns how many were on, or -1 if the list has none
   (an older build's list) or no character is loaded. */
int dks_peopleflags_reset(void);
/* Turns every DS3 flag of darksign_flags.tsv off and lets DS3's new-game setup run again. Returns how many were on,
   or -1 without the table or a loaded character. */
int dks_ds3flags_reset_all(void);

#ifdef __cplusplus
}
#endif

#endif
