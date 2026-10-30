#include "items.h"

#include <ctype.h>
#include <stdlib.h>
#include <string.h>

static const char *next_field(const char *s, char *out, size_t cap)
{
    size_t n = 0;
    while (*s && *s != '\t' && *s != '\r' && *s != '\n') {
        if (n + 1 < cap)
            out[n++] = *s;
        s++;
    }
    out[n] = 0;
    return *s == '\t' ? s + 1 : NULL;
}

int dks_parse_item_line(const char *line, DksItem *out)
{
    char id[16], upgrade[8], infusable[4];
    memset(out, 0, sizeof *out);
    if (!(line = next_field(line, id, sizeof id)) || !(line = next_field(line, out->kind, sizeof out->kind)) ||
        !(line = next_field(line, upgrade, sizeof upgrade)) || !(line = next_field(line, infusable, sizeof infusable)))
        return 0;
    if ((line = next_field(line, out->name, sizeof out->name)))
        next_field(line, out->game, sizeof out->game);
    if (!out->game[0])
        strcpy(out->game, "ER");
    char *end;
    out->id = (uint32_t)strtoul(id, &end, 16);
    if (*end || !id[0] || !out->name[0])
        return 0;
    out->max_upgrade = atoi(upgrade);
    out->infusable = infusable[0] == '1';
    return 1;
}

uint32_t dks_item_give_id(const DksItem *item, int affinity, int upgrade)
{
    if (strcmp(item->kind, "Weapon") != 0)
        return item->id;
    if (!item->infusable || affinity < 0 || affinity > 12)
        affinity = 0;
    if (upgrade < 0)
        upgrade = 0;
    if (upgrade > item->max_upgrade)
        upgrade = item->max_upgrade;
    return item->id + (uint32_t)affinity * 100 + (uint32_t)upgrade;
}

int dks_parse_pattern(const char *text, DksPattern *out)
{
    out->len = 0;
    while (*text) {
        while (*text == ' ')
            text++;
        if (!*text)
            break;
        if (out->len == DKS_PATTERN_MAX)
            return 0;
        if (*text == '?') {
            while (*text == '?')
                text++;
            out->bytes[out->len] = 0;
            out->mask[out->len++] = 0;
            continue;
        }
        char hex[3] = {text[0], text[1], 0};
        char *end;
        long v = strtol(hex, &end, 16);
        if (end != hex + 2)
            return 0;
        out->bytes[out->len] = (uint8_t)v;
        out->mask[out->len++] = 1;
        text += 2;
    }
    return out->len > 0;
}

long long dks_find_pattern(const uint8_t *data, size_t size, const DksPattern *p)
{
    if (size < p->len)
        return -1;
    for (size_t i = 0; i + p->len <= size; i++) {
        size_t j = 0;
        while (j < p->len && (!p->mask[j] || data[i + j] == p->bytes[j]))
            j++;
        if (j == p->len)
            return (long long)i;
    }
    return -1;
}

int dks_name_matches(const char *name, const char *query)
{
    if (!*query)
        return 1;
    for (; *name; name++) {
        const char *a = name, *b = query;
        while (*a && *b && tolower((unsigned char)*a) == tolower((unsigned char)*b))
            a++, b++;
        if (!*b)
            return 1;
    }
    return 0;
}
