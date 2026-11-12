#include "items.h"

#include <math.h>

#include <ctype.h>
#include <stdio.h>
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

int dks_level_for(const int attributes[DKS_ATTRIBUTES])
{
    int sum = 0;
    for (int i = 0; i < DKS_ATTRIBUTES; i++)
        sum += attributes[i] < 1 ? 1 : attributes[i] > DKS_ATTRIBUTE_MAX ? DKS_ATTRIBUTE_MAX : attributes[i];
    return sum - 79 < 1 ? 1 : sum - 79;
}

int dks_parse_spawn_line(const char *line, DksSpawnable *out)
{
    char model[16], npc[16], think[16], boss[4], body[8] = "";
    memset(out, 0, sizeof *out);
    if (line[0] == '#' || !(line = next_field(line, out->game, sizeof out->game)) || !(line = next_field(line, model, sizeof model)) ||
        !(line = next_field(line, npc, sizeof npc)) || !(line = next_field(line, think, sizeof think)) ||
        !(line = next_field(line, boss, sizeof boss)))
        return 0;
    if ((line = next_field(line, out->name, sizeof out->name)))
        next_field(line, body, sizeof body);
    out->body = strcmp(body, "ds3") == 0 ? DKS_BODY_DS3 : strcmp(body, "er") == 0 ? DKS_BODY_ER : DKS_BODY_UNKNOWN;
    char *end;
    if ((model[0] != 'c' && model[0] != 'C') || !model[1])
        return 0;
    long chr = strtol(model + 1, &end, 10);
    if (*end || chr <= 0 || chr > 9999)
        return 0;
    out->chr = (int)chr;
    out->npc_param = (int)strtol(npc, &end, 10);
    if (*end || !npc[0] || out->npc_param <= 0)
        return 0;
    out->think_param = (int)strtol(think, &end, 10);
    if (*end || !think[0])
        return 0;
    out->boss = boss[0] == '1';
    if (!out->name[0])
        snprintf(out->name, sizeof out->name, "c%04d", out->chr);
    return strcmp(out->game, "DS3") == 0 || strcmp(out->game, "ER") == 0;
}

int dks_spawn_has_own_model(const DksSpawnable *s)
{
    if (strcmp(s->game, "DS3") != 0)
        return 1;
    if (s->body != DKS_BODY_UNKNOWN)
        return s->body == DKS_BODY_DS3;
    return s->chr >= 7000 && s->chr <= 7999;
}

/* The facing: the quaternion applied to +Z, flattened onto the ground (+Z when it can't be worked out). */
static void facing(const float q[4], float *x, float *z)
{
    float fx = 2.0f * (q[0] * q[2] + q[3] * q[1]);
    float fz = 1.0f - 2.0f * (q[0] * q[0] + q[1] * q[1]);
    float len = sqrtf(fx * fx + fz * fz);
    if (!(len > 0.01f) || len != len) {
        fx = 0.0f;
        fz = 1.0f;
    } else {
        fx /= len;
        fz /= len;
    }
    *x = fx;
    *z = fz;
}

void dks_fly_step(const float pos[3], const float q[4], int forward, int up, float speed, float dt, float out[3])
{
    float fx, fz;
    facing(q, &fx, &fz);
    float step = speed * dt;
    if (!(step > 0.0f) || step != step)
        step = 0.0f;
    out[0] = pos[0] + fx * step * (float)forward;
    out[1] = pos[1] + step * (float)up;
    out[2] = pos[2] + fz * step * (float)forward;
}

void dks_spawn_position(const float player[3], const float q[4], int n, float out[3])
{
    float fx, fz;
    facing(q, &fx, &fz);
    if (n < 0)
        n = 0;
    float ahead = 3.0f + (float)(n / 5) * 1.5f;
    float side = (float)(n % 5 - 2) * 1.5f;
    out[0] = player[0] + fx * ahead + fz * side;
    out[1] = player[1] + 0.3f;
    out[2] = player[2] + fz * ahead - fx * side;
}
