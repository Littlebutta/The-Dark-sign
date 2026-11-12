/* Platform-independent parts of the test menu: the item list file, item IDs and byte patterns.
   Kept apart from the Windows code so they can be tested on the build machine. */
#ifndef DKSIGN_ITEMS_H
#define DKSIGN_ITEMS_H

#include <stddef.h>
#include <stdint.h>

#define DKS_NAME_MAX 96
#define DKS_PATTERN_MAX 64

#ifdef __cplusplus
extern "C" {
#endif

/* Elden Ring item categories, as the high bits of an item ID. */
enum {
    DKS_CAT_WEAPON = 0x00000000u,
    DKS_CAT_ARMOR = 0x10000000u,
    DKS_CAT_TALISMAN = 0x20000000u,
    DKS_CAT_GOODS = 0x40000000u,
    DKS_CAT_ASH = 0x80000000u,
};

typedef struct {
    uint32_t id;         /* full ID, category bits included */
    char kind[16];       /* "Weapon", "Armor", "Talisman", "Item", "AshOfWar" */
    int max_upgrade;     /* weapons: highest +N, others 0 */
    int infusable;       /* weapons that take affinities (Heavy, Keen, ...) */
    char name[DKS_NAME_MAX]; /* UTF-8 */
    char game[8];        /* where it comes from: "ER", "DS3" (later "DSR", "BB") */
} DksItem;

/* One line of darksign_items.tsv: "<hex id>\t<kind>\t<max upgrade>\t<infusable 0|1>\t<name>[\t<game>]".
   The game defaults to "ER". Returns 1 if valid. */
int dks_parse_item_line(const char *line, DksItem *out);

/* The ID to give: a weapon's base ID plus affinity (0-12, x100) and upgrade level, both clamped to what it allows. */
uint32_t dks_item_give_id(const DksItem *item, int affinity, int upgrade);

typedef struct {
    uint8_t bytes[DKS_PATTERN_MAX];
    uint8_t mask[DKS_PATTERN_MAX]; /* 1 = must match */
    size_t len;
} DksPattern;

/* "48 8B 05 ? ? ? ? 48" -> bytes and mask ("?" and "??" are wildcards). Returns 1 if valid. */
int dks_parse_pattern(const char *text, DksPattern *out);

/* Offset of the first match in data, or -1. */
long long dks_find_pattern(const uint8_t *data, size_t size, const DksPattern *p);

/* Elden Ring's eight attributes, in the order the game stores them. */
#define DKS_ATTRIBUTES 8
#define DKS_ATTRIBUTE_MAX 99

/* The character level that goes with a set of attributes: every class starts with attributes summing to its level
   plus 79, and each level adds one point. Values are clamped to 1-99 first. */
int dks_level_for(const int attributes[DKS_ATTRIBUTES]);

/* Case-insensitive ASCII substring test, so "long sw" finds "Longsword". */
int dks_name_matches(const char *name, const char *query);

/* One line of darksign_spawn_list.tsv (Porter Enemies/SpawnList.cs):
   "<game DS3|ER>\t<model cNNNN>\t<NpcParam>\t<ThinkParam>\t<boss 0|1>\t<name>\t<body ds3|er>". Returns 1 if valid.
   The body column says whose model the character wears; a list written before it existed has none (DKS_BODY_UNKNOWN). */
enum { DKS_BODY_UNKNOWN = 0, DKS_BODY_ER = 1, DKS_BODY_DS3 = 2 };
typedef struct {
    char game[8];
    int chr;             /* 2275 for c2275 */
    int npc_param;
    int think_param;
    int boss;
    char name[DKS_NAME_MAX];
    int body;            /* DKS_BODY_* */
} DksSpawnable;

/* 1 when a spawn list entry wears the model its name says: every Elden Ring entry, and a DS3 entry whose model the
   port built (body "ds3"). A DS3 entry on an Elden Ring body looks like that Elden Ring enemy. Lists without the body
   column fall back to the port's ID range (c7000-c7999), which Elden Ring's own c7000 also falls in. */
int dks_spawn_has_own_model(const DksSpawnable *s);

int dks_parse_spawn_line(const char *line, DksSpawnable *out);

/* One line of darksign_boss_kits.tsv (Porter Maps/BossKits.cs): a DS3 boss whose own map-script mechanics (Pontiff's
   clone, Friede's stages) travel with it in common.emevd, keyed to fixed entity IDs.
   "<boss model cNNNN>\t<NpcParam>\t<kit entity>\t<name>\t<companions: cNNNN:NpcParam:ThinkParam:entity,...>".
   Returns 1 if valid. A spawned boss of that model gets the kit entity, and its companions are spawned with it. */
#define DKS_KIT_COMPANIONS 8
typedef struct {
    int chr;
    int npc_param;
    int think_param;
    unsigned entity;
} DksKitCharacter;
typedef struct {
    DksKitCharacter boss; /* think_param unused: the spawn list's entry decides */
    char name[DKS_NAME_MAX];
    int companions_len;
    DksKitCharacter companions[DKS_KIT_COMPANIONS];
} DksBossKit;

int dks_parse_kit_line(const char *line, DksBossKit *out);

/* Where the n-th character of a spawn goes, from the player's position and facing (a Havok quaternion x, y, z, w):
   rows of five, three metres ahead and 1.5 m apart, a little above the ground. out = x, y, z. */
void dks_spawn_position(const float player[3], const float quaternion[4], int n, float out[3]);

/* One frame of the F1 menu's fly mode: moves pos along the character's facing (the quaternion applied to +Z,
   flattened onto the ground) by forward * speed * dt and straight up by up * speed * dt. forward and up are -1, 0
   or 1 (keys held). out = x, y, z. */
void dks_fly_step(const float pos[3], const float quaternion[4], int forward, int up, float speed, float dt, float out[3]);

#ifdef __cplusplus
}
#endif

#endif
