#include "items.h"

#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(cond) do { if (!(cond)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #cond); failures++; } } while (0)

int main(void)
{
    DksItem item;
    CHECK(dks_parse_item_line("4C4B400\tWeapon\t25\t1\tAstora Straight Sword\r\n", &item));
    CHECK(item.id == 80000000u && strcmp(item.kind, "Weapon") == 0 && item.max_upgrade == 25 && item.infusable);
    CHECK(strcmp(item.name, "Astora Straight Sword") == 0 && strcmp(item.game, "ER") == 0);
    CHECK(dks_parse_item_line("4C4B400\tWeapon\t25\t1\tAstora Straight Sword\tDS3\n", &item));
    CHECK(strcmp(item.name, "Astora Straight Sword") == 0 && strcmp(item.game, "DS3") == 0);
    CHECK(dks_item_give_id(&item, 1, 10) == 80000110u);  /* Heavy +10 */
    CHECK(dks_item_give_id(&item, 13, 99) == 80000025u); /* bad affinity -> Standard, upgrade clamped */

    CHECK(dks_parse_item_line("1000000\tWeapon\t10\t0\tSomber Blade", &item));
    CHECK(dks_item_give_id(&item, 4, 12) == 0x100000Au);  /* not infusable, +10 max */

    CHECK(dks_parse_item_line("400000AF\tItem\t0\t0\tGolden Rune [1]", &item));
    CHECK(item.id == 0x400000AFu && dks_item_give_id(&item, 3, 5) == 0x400000AFu);

    CHECK(!dks_parse_item_line("zz\tItem\t0\t0\tBad", &item));
    CHECK(!dks_parse_item_line("40000001\tItem\t0", &item));
    CHECK(!dks_parse_item_line("40000001\tItem\t0\t0\t", &item));

    DksPattern p;
    CHECK(dks_parse_pattern("48 8B 05 ? ? ?? ? 48 85", &p) && p.len == 9 && p.mask[3] == 0 && p.bytes[8] == 0x85);
    CHECK(!dks_parse_pattern("48 8G", &p));
    const unsigned char data[] = {0x00, 0x48, 0x8B, 0x05, 1, 2, 3, 4, 0x48, 0x85, 0xC0};
    CHECK(dks_parse_pattern("48 8B 05 ? ? ? ? 48 85", &p) && dks_find_pattern(data, sizeof data, &p) == 1);
    CHECK(dks_parse_pattern("48 8B 06", &p) && dks_find_pattern(data, sizeof data, &p) == -1);

    CHECK(dks_name_matches("Astora Straight Sword", "straight"));
    CHECK(dks_name_matches("Longsword", ""));
    CHECK(!dks_name_matches("Longsword", "katana"));

    int vagabond[DKS_ATTRIBUTES] = {15, 10, 11, 14, 13, 9, 9, 7};
    CHECK(dks_level_for(vagabond) == 9);
    int maxed[DKS_ATTRIBUTES] = {99, 99, 99, 99, 99, 99, 99, 99};
    CHECK(dks_level_for(maxed) == 713);
    int wild[DKS_ATTRIBUTES] = {0, 150, 5, 5, 5, 5, 5, 5};
    CHECK(dks_level_for(wild) == 1 + 99 + 30 - 79);

    DksSpawnable sp;
    CHECK(dks_parse_spawn_line("DS3\tc7000\t70000000\t43000000\t0\tLothric Knight\r\n", &sp));
    CHECK(strcmp(sp.game, "DS3") == 0 && sp.chr == 7000 && sp.npc_param == 70000000 && sp.think_param == 43000000);
    CHECK(!sp.boss && strcmp(sp.name, "Lothric Knight") == 0);
    CHECK(sp.body == DKS_BODY_UNKNOWN && dks_spawn_has_own_model(&sp)); /* old list: the ID range decides */
    CHECK(dks_parse_spawn_line("ER\tc2275\t22750020\t22750000\t1\tGiant Crab", &sp) && sp.boss && sp.chr == 2275);
    CHECK(dks_spawn_has_own_model(&sp));
    /* The body column: Elden Ring ships c7000 itself, so a DS3 entry left on it is an Elden Ring body. */
    CHECK(dks_parse_spawn_line("DS3\tc7000\t71260000\t70001000\t0\tTree Woman - Ariandel\ter\r\n", &sp));
    CHECK(sp.body == DKS_BODY_ER && !dks_spawn_has_own_model(&sp) && strcmp(sp.name, "Tree Woman - Ariandel") == 0);
    CHECK(dks_parse_spawn_line("DS3\tc7031\t70310000\t43000000\t0\tBoreal Outrider Knight\tds3\n", &sp));
    CHECK(sp.body == DKS_BODY_DS3 && dks_spawn_has_own_model(&sp));
    CHECK(dks_parse_spawn_line("DS3\tc3061\t70310000\t43000000\t0\tBoreal Outrider Knight\ter", &sp) && !dks_spawn_has_own_model(&sp));
    CHECK(dks_parse_spawn_line("ER\tc7000\t70001065\t70001000\t0\tFallen Hawks Soldier\ter", &sp) && dks_spawn_has_own_model(&sp));
    CHECK(!dks_parse_spawn_line("# game\tmodel\tNpcParam\tThinkParam\tboss\tname", &sp));
    CHECK(!dks_parse_spawn_line("BB\tc2275\t22750020\t22750000\t0\tX", &sp));
    CHECK(!dks_parse_spawn_line("ER\tx2275\t22750020\t22750000\t0\tX", &sp));
    CHECK(!dks_parse_spawn_line("ER\tc2275\tabc\t22750000\t0\tX", &sp));
    CHECK(!dks_parse_spawn_line("ER\tc2275\t22750020", &sp));

    float player[3] = {10, 5, 20}, out[3];
    float facing_z[4] = {0, 0, 0, 1};             /* no rotation: facing +Z */
    dks_spawn_position(player, facing_z, 2, out);  /* middle of the first row */
    CHECK(out[0] > 9.99f && out[0] < 10.01f && out[1] > 5.2f && out[2] > 22.99f && out[2] < 23.01f);
    float facing_x[4] = {0, 0.70710678f, 0, 0.70710678f}; /* 90 degrees about Y: +Z turns to +X */
    dks_spawn_position(player, facing_x, 2, out);
    CHECK(out[0] > 12.99f && out[0] < 13.01f && out[2] > 19.99f && out[2] < 20.01f);
    float broken[4] = {0, 0, 0, 0};
    dks_spawn_position(player, broken, 7, out);    /* second row, no facing: still somewhere near */
    CHECK(out[2] > 24.4f && out[2] < 24.6f);

    /* Fly: 20 m/s for half a second along the facing, or straight up/down. */
    dks_fly_step(player, facing_x, 1, 0, 20.0f, 0.5f, out);
    CHECK(out[0] > 19.99f && out[0] < 20.01f && out[1] > 4.99f && out[1] < 5.01f && out[2] > 19.99f && out[2] < 20.01f);
    dks_fly_step(player, facing_z, 0, -1, 20.0f, 0.5f, out);
    CHECK(out[0] > 9.99f && out[0] < 10.01f && out[1] > -5.01f && out[1] < -4.99f && out[2] > 19.99f && out[2] < 20.01f);
    dks_fly_step(player, broken, 1, 1, 20.0f, -1.0f, out); /* a bad frame time doesn't move anything */
    CHECK(out[0] == player[0] && out[1] == player[1] && out[2] == player[2]);

    if (failures)
        return 1;
    printf("test_items: all passed\n");
    return 0;
}
