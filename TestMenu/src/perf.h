/* Frame-time recorder for the F1 menu (Ctrl+Shift+F8, or the menu's checkbox): measures the time between the game's
   frames as the swap chain presents them, and writes what it measured to the test menu's log, so a test walk on a
   DS3 map gives numbers (frames per second, hitches and where they happened) instead of a feel. See perf.c. */
#ifndef DKSIGN_PERF_H
#define DKSIGN_PERF_H

#ifdef __cplusplus
extern "C" {
#endif

/* Called by the overlay on every Present, from the render thread. */
void dks_perf_frame(void);

/* Starts or stops recording; safe from any thread (the render thread acts on it at the next frame). */
void dks_perf_request_toggle(void);

/* 1 while recording. */
int dks_perf_on(void);

/* One line about the recording (or the last one), for the menu. */
const char *dks_perf_status(void);

#ifdef __cplusplus
}
#endif

#endif
