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

#ifdef __cplusplus
}
#endif

#endif
