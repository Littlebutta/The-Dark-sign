/* Keeps the game off the internet: name lookups and connections to anything but this PC fail,
   so the game cannot reach FromSoftware's servers and plays offline. */
#ifndef DKSIGN_OFFLINE_H
#define DKSIGN_OFFLINE_H

typedef void (*dks_log_fn)(const wchar_t *fmt, ...);

/* Hooks Winsock. MinHook must be initialised. Returns 1 when every hook is in place. */
int dks_block_online(dks_log_fn log);

#endif
