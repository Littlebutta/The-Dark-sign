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

/* God mode for testing (the F1 menu checkbox, Ctrl+Shift+F5, or DKSIGN_GOD=1 at launch). While on, the main player's
   ChrDataModule "no damage" debug bit is kept set (fromsoftware-rs, chr_ins/module/data.rs: debug_flags at 0x19B,
   "2nd bit makes you undamageable") and HP is topped back up to max in case a game version moves that bit.
   dks_god_update applies it; the render thread calls it every frame. */
void dks_god_set(int on);
int dks_god_on(void);
void dks_god_update(void);

/* Fly and no-clip for getting around while testing (F1 menu checkboxes, Ctrl+Shift+F6 / F7).
   Fly keeps the player's "no gravity" byte set, so the character stays at its height and walks on air with the game's
   own controls; while the game has focus, Page Up / Page Down move straight up / down and Home / End fly forward /
   back along the way the character faces, at dks_fly_speed metres per second (2-100).
   No-clip also turns off the player's map collision (walls, floors) and, as far as the game lets it, object collision,
   and implies fly so the player doesn't drop through the floor. Turning either off clears only what it set.
   dks_fly_update applies both; the render thread calls it every frame, keys = 1 when the movement keys should count
   (game focused, F1 menu not typing). Offsets: fromsoftware-rs and EldenRingTool (CREDITS.md). */
/* The main player's position (the F1 log's coordinates), 1 when there is a player to read; for the frame-time
   recorder (perf.c). */
int dks_player_position(float pos[3]);

void dks_fly_set(int on);
int dks_fly_on(void);
void dks_noclip_set(int on);
int dks_noclip_on(void);
float dks_fly_speed(void);
void dks_fly_set_speed(float metres_per_second);
void dks_fly_update(int keys);

#ifdef __cplusplus
}
#endif

#endif
