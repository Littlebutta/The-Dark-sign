/* The F1 menu's enemy spawner: spawns Dark Souls III (ported) or Elden Ring characters next to the player and clears
   the ones it spawned.

   Spawning goes through the game's own debug character creator (CS::CSDebugChrCreator, owned by WorldChrMan): the
   request (model, NpcParam, ThinkParam, entity ID, position) is written into its init data and its "spawn" flag set;
   the game creates the character on its own thread on the next frame, loading the model if needed, and remembers it
   as the last created character. This is how fromsoftware-rs' WorldChrMan::spawn_debug_character and thefifthmatt's
   souls-client debug_spawn do it (CREDITS.md); the WorldChrMan pattern is EldenRingTool's (kh0nsu).

   Clearing uses the characters this menu spawned (kept by pointer and entity ID, checked before every use):
   method A sets their HP to 0 so the game kills them; fallback B (DKSIGN_SPAWN_REMOVE=1) removes them with the
   game's RemoveChrIns, souls-client's way, which leaves no bodies but runs off the game's update thread. */
#ifndef DKSIGN_SPAWN_H
#define DKSIGN_SPAWN_H

#include "game.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The spawnable characters: darksign_spawn_list.tsv next to the item list (DKSIGN_ITEMS), or a few built-in Elden
   Ring ones when there is none. *from_file says which. Read once; later calls return the same list. */
int dks_spawn_list(const DksSpawnable **out, int *from_file);

/* 1 if the game closed or crashed right after this character (chr 7064 for c7064) was spawned in an earlier session
   (dksign_spawn_crashes.log). Valid once dks_spawn_list has been called. */
int dks_spawn_crashed(int chr);

/* Looks up the game addresses (once). Safe from any thread; the menu's prescan calls it early. */
void dks_spawn_scan(void);

/* Queues count (1-20) of a character next to the player. DKS_NOT_FOUND when the game code wasn't recognised,
   DKS_NOT_LOADED when no character is loaded. */
DksResult dks_spawn(const DksSpawnable *who, int count);

/* Drives the queue: hands the next request to the game once the last one is done. Call every frame. */
void dks_spawn_update(void);

/* Requests still queued or in progress, and characters spawned by the menu that are still there and alive. */
int dks_spawn_pending(void);
int dks_spawn_alive(void);

/* Clears every character the menu spawned (method A: kill, B: remove). Returns how many, or -1 when the game code
   wasn't recognised. Also empties the queue. */
int dks_spawn_clear(void);

/* Asks for dks_spawn_clear on the thread that calls dks_spawn_update (for the hotkey thread). */
void dks_spawn_request_clear(void);

/* What the spawner last did, for the menu's status line. */
const char *dks_spawn_status(void);

#ifdef __cplusplus
}
#endif

#endif
