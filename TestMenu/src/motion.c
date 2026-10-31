/* Motion categories above 255 for ported weapons.
   A weapon picks its moveset (animation section aNNN) by EquipParamWeapon.wepmotionCategory, one byte. The game copies
   it per hand into an int cache (left, right, hand style) in the function hooked here, so after the original has filled
   the cache we put in the wider category the porter wrote to Mods\ER\motion-categories.txt for that weapon. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "MinHook.h"
#include "game.h"
#include "motion.h"

typedef unsigned char (*CacheUpdate)(int *cache, void *equip);
typedef int (*WeaponIdOf)(void *equip, int slot);

#define MAX_WIDE 4096
static struct { int base; int category; } g_wide[MAX_WIDE];
static int g_wide_count;
static CacheUpdate o_cache_update;
static WeaponIdOf g_weapon_id_of;

static int wide_category(int weapon_id)
{
    if (weapon_id <= 0)
        return -1;
    int base = weapon_id / 10000 * 10000; /* affinity and upgrade level live in the last four digits */
    for (int i = 0; i < g_wide_count; i++)
        if (g_wide[i].base == base)
            return g_wide[i].category;
    return -1;
}

static unsigned char h_cache_update(int *cache, void *equip)
{
    if (!equip) /* the original returns at once then, without touching the cache */
        return o_cache_update(cache, equip);
    int before[3] = {cache[0], cache[1], cache[2]};
    o_cache_update(cache, equip);
    for (int hand = 0; hand < 2; hand++) {
        int category = wide_category(g_weapon_id_of(equip, hand == 0 ? -2 : -1));
        if (category >= 0)
            cache[hand] = category;
    }
    /* Changed only against what the cache held before, so the wide value doesn't look new every frame. */
    return before[0] != cache[0] || before[1] != cache[1] || before[2] != cache[2];
}

static int load_table(void)
{
    wchar_t path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH))
        return 0;
    wchar_t *slash = wcsrchr(path, L'\\');
    if (!slash)
        return 0;
    wcscpy(slash + 1, L"motion-categories.txt"); /* next to the item list, in Mods\ER */
    FILE *f = _wfopen(path, L"r");
    if (!f)
        return 0;
    char line[128];
    while (g_wide_count < MAX_WIDE && fgets(line, sizeof line, f)) {
        int base, category;
        if (line[0] != '#' && sscanf(line, "%d %d", &base, &category) == 2 && category > 0 && category < 1000) {
            g_wide[g_wide_count].base = base;
            g_wide[g_wide_count].category = category;
            g_wide_count++;
        }
    }
    fclose(f);
    return g_wide_count;
}

void dks_motion_init(void)
{
    if (!load_table()) {
        dks_log("motion: no weapons on categories above 255");
        return;
    }
    void *update = (void *)dks_find_in_game(PAT_MOTION_CACHE_UPDATE);
    uintptr_t getter = dks_find_in_game(PAT_MOTION_GETTER);
    uintptr_t id_of = getter ? getter - WEAPON_ID_OF_BEFORE_GETTER : 0;
    if (id_of && memcmp((void *)id_of, WEAPON_ID_OF_START, sizeof WEAPON_ID_OF_START - 1) == 0)
        g_weapon_id_of = (WeaponIdOf)id_of;
    if (!update || !g_weapon_id_of) {
        dks_log("motion: game code not found; %d weapons keep their template movesets", g_wide_count);
        return;
    }
    if (MH_CreateHook(update, (void *)h_cache_update, (void **)&o_cache_update) != MH_OK || MH_EnableHook(update) != MH_OK) {
        dks_log("motion: cannot hook the motion category cache");
        return;
    }
    dks_log("motion: %d weapons on categories above 255", g_wide_count);
}
