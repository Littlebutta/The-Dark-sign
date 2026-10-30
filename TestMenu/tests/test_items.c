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

    if (failures)
        return 1;
    printf("test_items: all passed\n");
    return 0;
}
