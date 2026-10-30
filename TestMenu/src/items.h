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

#ifdef __cplusplus
}
#endif

#endif
