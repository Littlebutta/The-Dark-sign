#include "spawn.h"

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* EldenRingTool (ERProcess.cs): a call, then "mov rax, [WorldChrMan]" and "mov r13, [rax + main player offset]". */
#define PAT_WORLD_CHR_MAN "E8 ? ? ? ? 48 8B 05 ? ? ? ? 4C 8B A8 ? ? ? ? 4D 85 ED 0F 84 ? ? ? ?"
/* souls-client (binary-mapper/eldenring.toml REMOVE_CHR_INS): "mov rdx, r12; call RemoveChrIns; mov dl, 1; mov rcx, r12". */
#define PAT_REMOVE_CHR_INS "49 8B D4 E8 ? ? ? ? B2 01 49 8B CC E8"

/* fromsoftware-rs (crates/eldenring/src/cs/world_chr_man.rs, chr_ins.rs, chr_ins/module): offsets for the current game.
   The debug creator is read relative to the main player field (0x1E508 -> 0x1E658), whose offset comes from the
   pattern, so a game update that grows WorldChrMan before both still lands on it. */
#define DEBUG_CREATOR_AFTER_PLAYER 0x150
#define CREATOR_SPAWN 0x44          /* bool: the game creates the character in init data, then clears it */
#define CREATOR_INIT 0xB0           /* CSDebugChrCreatorInitData */
#define CREATOR_LAST_CREATED 0x1B0  /* ChrIns* */
#define INIT_POSITION 0x00          /* float x, y, z, w */
#define INIT_NPC_PARAM 0x40
#define INIT_THINK_PARAM 0x44
#define INIT_ENTITY_ID 0x48
#define INIT_TALK_ID 0x4C
#define INIT_NAME 0x50              /* wchar_t[0x20], "c2275" */
#define INIT_CHARA_INIT_PARAM 0xCC
#define CHR_MODULES 0x190
#define CHR_ENTITY_ID 0x1E8
#define MODULES_DATA 0x00
#define MODULES_PHYSICS 0x68
#define DATA_HP 0x138
#define PHYSICS_ORIENTATION 0x50    /* quaternion x, y, z, w */
#define PHYSICS_POSITION 0x70       /* float x, y, z */

/* Entity IDs for spawned characters, so a pointer kept from an earlier frame can be checked to still be ours. */
#define ENTITY_BASE 1039295000u
#define ENTITY_COUNT 1000u
#define MAX_QUEUE 64
#define MAX_TRACKED 256
#define SPAWN_TIMEOUT_MS 15000
/* A spawn counts as safe once the character has been in the world this long without the game going down. */
#define SPAWN_SAFE_MS 20000
#define MAX_CRASHED 128

typedef void (*RemoveChrInsFn)(void *world_chr_man, void *chr_ins);

static SRWLOCK g_lock = SRWLOCK_INIT;
static volatile LONG g_scanned;
static uintptr_t g_wcm_slot;
static uint32_t g_player_offset;
static RemoveChrInsFn g_remove;
static uintptr_t g_image_lo, g_image_hi;

typedef struct {
    DksSpawnable who;
    int n; /* place in its batch, for the position */
} Request;

static Request g_queue[MAX_QUEUE];
static int g_queue_len;
static int g_active;              /* a request is with the game */
static Request g_current;
static uint32_t g_current_entity;
static uintptr_t g_current_before; /* the last created character before the request */
static ULONGLONG g_current_since;
static uint32_t g_next_entity;

#define WATCH_FX_MAX 24
typedef struct {
    uintptr_t chr;
    uint32_t entity;
    /* The watch (spawn_watch): what was last written to the log about this character. */
    ULONGLONG seen;   /* when it was last sampled */
    ULONGLONG logged; /* when a line was last written */
    int32_t hp;
    float pos[3];
    int still;        /* 1 once it was logged as standing still */
    int32_t anim[2];
    int32_t fx[WATCH_FX_MAX]; /* the special effects (SpEffect IDs) it had at the last sample */
    int fx_n;
} Spawned;

/* Watch: how often a spawned character is sampled, how far it must move between samples to count as moving, and how
   often a moving one is written down. */
#define WATCH_SAMPLE_MS 500
#define WATCH_MOVE_M 0.03f
#define WATCH_LINE_MS 3000
#define DATA_HP_MAX 0x13C
/* Two places an animation ID has been reported at (not confirmed for this game version: logged raw, as a?/b?). */
#define MODULES_TIMEACT 0x18
#define TIMEACT_ANIM 0x20
#define MODULES_EVENT 0x58
#define EVENT_ANIM 0xC8
/* The special effects on a character (fromsoftware-rs ChrIns.special_effect, unconfirmed for this version: logged as
   "fx?"): ChrIns + 0x178 -> SpecialEffect, + 0x8 -> first entry; entry + 0x8 = SpEffect ID, + 0x30 = next entry. */
#define CHR_SPEFFECT 0x178
#define SPEFFECT_HEAD 0x8
#define FXENTRY_ID 0x8
#define FXENTRY_NEXT 0x30
static Spawned g_spawned[MAX_TRACKED];
static int g_spawned_len;

static volatile LONG g_clear_requested;
static char g_status[200] = "Pick an enemy and press Spawn.";

/* Crash memory: the character being spawned is written to dksign_spawn_pending.log (in the save folder) until it has
   stood SPAWN_SAFE_MS; a pending file found at the next start means the game went down right after spawning it, and it
   is added to dksign_spawn_crashes.log, which the menu marks (both are .log so "Save test report" picks them up). */
static int g_crashed[MAX_CRASHED];
static int g_crashed_len;
static int g_pending_open;          /* a pending file is written */
static ULONGLONG g_pending_spawned; /* when its character appeared, 0 while still being created */

static int save_path(const wchar_t *file, wchar_t out[MAX_PATH])
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_SAVE_DIR", dir, MAX_PATH);
    if (!n || n >= MAX_PATH - 40)
        return 0;
    _snwprintf(out, MAX_PATH, L"%ls\\%ls", dir, file);
    out[MAX_PATH - 1] = 0;
    return 1;
}

static void load_crashes(void)
{
    wchar_t pending[MAX_PATH], crashes[MAX_PATH];
    if (!save_path(L"dksign_spawn_pending.log", pending) || !save_path(L"dksign_spawn_crashes.log", crashes))
        return;
    FILE *p = _wfopen(pending, L"rb");
    if (p) {
        char line[256] = {0};
        if (fgets(line, sizeof line, p)) {
            line[strcspn(line, "\r\n")] = 0;
            FILE *c = _wfopen(crashes, L"ab");
            if (c) {
                SYSTEMTIME t;
                GetLocalTime(&t);
                fprintf(c, "%s\tgame closed or crashed within %d s of spawning it (%04d-%02d-%02d %02d:%02d)\r\n", line,
                        SPAWN_SAFE_MS / 1000, t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute);
                fclose(c);
            }
            dks_log("spawn: the game went down right after spawning %s last time; marked in the menu", line);
        }
        fclose(p);
        DeleteFileW(pending);
    }
    FILE *c = _wfopen(crashes, L"rb");
    if (!c)
        return;
    char line[256];
    while (g_crashed_len < MAX_CRASHED && fgets(line, sizeof line, c)) {
        int chr = 0;
        if ((line[0] == 'c' || line[0] == 'C') && sscanf(line + 1, "%d", &chr) == 1 && chr > 0 && !dks_spawn_crashed(chr))
            g_crashed[g_crashed_len++] = chr;
    }
    fclose(c);
}

int dks_spawn_crashed(int chr)
{
    for (int i = 0; i < g_crashed_len; i++)
        if (g_crashed[i] == chr)
            return 1;
    return 0;
}

static void pending_write(const DksSpawnable *who)
{
    wchar_t path[MAX_PATH];
    if (!save_path(L"dksign_spawn_pending.log", path))
        return;
    FILE *f = _wfopen(path, L"wb");
    if (!f)
        return;
    fprintf(f, "c%04d\t%s\t%s\r\n", who->chr, who->game, who->name);
    fflush(f);
    fclose(f);
    g_pending_open = 1;
    g_pending_spawned = 0;
}

static void pending_clear(void)
{
    wchar_t path[MAX_PATH];
    if (g_pending_open && save_path(L"dksign_spawn_pending.log", path))
        DeleteFileW(path);
    g_pending_open = 0;
}

static DksSpawnable *g_list;
static int g_list_len, g_list_from_file, g_list_loaded;

/* Elden Ring's own enemies that stand in when there's no spawn list yet (from souls-client's spawn_name). */
static const DksSpawnable BUILT_IN[] = {
    {"ER", 2275, 22750020, 22750000, 0, "Giant Crab", DKS_BODY_ER},
    {"ER", 4351, 43510010, 43510000, 0, "Knight (First Step test spawn)", DKS_BODY_ER},
    {"ER", 5130, 51300099, 51300000, 1, "Messmer the Impaler", DKS_BODY_ER},
};

static void set_status(const char *fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    vsnprintf(g_status, sizeof g_status, fmt, args);
    va_end(args);
    dks_log("spawn: %s", g_status);
}

const char *dks_spawn_status(void) { return g_status; }

static int read_mem(uintptr_t addr, void *out, size_t size)
{
    SIZE_T got = 0;
    return addr > 0x10000 && ReadProcessMemory(GetCurrentProcess(), (LPCVOID)addr, out, size, &got) && got == size;
}

static int write_mem(uintptr_t addr, const void *data, size_t size)
{
    SIZE_T put = 0;
    return addr > 0x10000 && WriteProcessMemory(GetCurrentProcess(), (LPVOID)addr, data, size, &put) && put == size;
}

static uintptr_t read_ptr(uintptr_t addr)
{
    uintptr_t v = 0;
    return read_mem(addr, &v, sizeof v) ? v : 0;
}

static int in_image(uintptr_t p) { return p >= g_image_lo && p < g_image_hi; }

/* A live game object: a readable pointer whose first field is a vtable inside the game's image. */
static int is_object(uintptr_t p) { return p && in_image(read_ptr(p)); }

static void find_image(void)
{
    uintptr_t base = (uintptr_t)GetModuleHandleW(NULL);
    uintptr_t end = base;
    MEMORY_BASIC_INFORMATION mbi;
    for (uint8_t *at = (uint8_t *)base; VirtualQuery(at, &mbi, sizeof mbi) && (uintptr_t)mbi.AllocationBase == base;
         at = (uint8_t *)mbi.BaseAddress + mbi.RegionSize)
        end = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
    g_image_lo = base;
    g_image_hi = end > base ? end : base + 0x10000000;
}

static int accept_world_chr_man(uintptr_t hit, void *ctx)
{
    (void)ctx;
    uint32_t offset = *(const uint32_t *)(hit + 15);
    /* Main player offsets seen so far: 0x18468 (1.0x) to 0x1E508 (1.16). */
    return offset >= 0x10000 && offset < 0x40000 && offset % 8 == 0;
}

void dks_spawn_scan(void)
{
    if (g_scanned)
        return;
    AcquireSRWLockExclusive(&g_lock);
    if (!g_scanned) {
        find_image();
        uintptr_t hit = dks_find_in_game_where(PAT_WORLD_CHR_MAN, accept_world_chr_man, NULL);
        if (hit) {
            g_wcm_slot = hit + 5 + 7 + *(const int32_t *)(hit + 8);
            g_player_offset = *(const uint32_t *)(hit + 15);
        }
        uintptr_t call = dks_find_in_game(PAT_REMOVE_CHR_INS);
        if (call)
            g_remove = (RemoveChrInsFn)(call + 8 + *(const int32_t *)(call + 4));
        dks_log("spawn: WorldChrMan +%llx, main player +%x, debug creator +%x, RemoveChrIns +%llx",
                g_wcm_slot ? (unsigned long long)(g_wcm_slot - g_image_lo) : 0ULL, g_player_offset,
                g_player_offset ? g_player_offset + DEBUG_CREATOR_AFTER_PLAYER : 0,
                g_remove ? (unsigned long long)((uintptr_t)g_remove - g_image_lo) : 0ULL);
        MemoryBarrier();
        g_scanned = 1;
    }
    ReleaseSRWLockExclusive(&g_lock);
}

static uintptr_t world_chr_man(void) { return g_wcm_slot ? read_ptr(g_wcm_slot) : 0; }

static uintptr_t debug_creator(uintptr_t wcm)
{
    uintptr_t creator = wcm ? read_ptr(wcm + g_player_offset + DEBUG_CREATOR_AFTER_PLAYER) : 0;
    return is_object(creator) ? creator : 0;
}

/* The main player, 0 at the title screen or while loading. */
static uintptr_t main_player(uintptr_t wcm) { return wcm ? read_ptr(wcm + g_player_offset) : 0; }

static int remove_wanted(void)
{
    wchar_t v[8];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_SPAWN_REMOVE", v, 8);
    return n > 0 && n < 8 && v[0] == L'1';
}

int dks_spawn_list(const DksSpawnable **out, int *from_file)
{
    if (!g_list_loaded) {
        g_list_loaded = 1;
        load_crashes();
        wchar_t path[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH);
        wchar_t *slash = n && n < MAX_PATH ? wcsrchr(path, L'\\') : NULL;
        FILE *f = NULL;
        if (slash && (size_t)(slash - path) + 30 < MAX_PATH) {
            wcscpy(slash + 1, L"darksign_spawn_list.tsv");
            f = _wfopen(path, L"rb");
        }
        if (f) {
            int cap = 512;
            g_list = malloc(sizeof *g_list * cap);
            char line[512];
            while (g_list && fgets(line, sizeof line, f)) {
                if (g_list_len == cap) {
                    DksSpawnable *grown = realloc(g_list, sizeof *g_list * (cap *= 2));
                    if (!grown)
                        break;
                    g_list = grown;
                }
                if (dks_parse_spawn_line(line, &g_list[g_list_len]))
                    g_list_len++;
            }
            fclose(f);
            g_list_from_file = g_list_len > 0;
        }
        if (!g_list_from_file) {
            free(g_list);
            g_list = malloc(sizeof BUILT_IN);
            if (g_list) {
                memcpy(g_list, BUILT_IN, sizeof BUILT_IN);
                g_list_len = (int)(sizeof BUILT_IN / sizeof BUILT_IN[0]);
            } else
                g_list_len = 0;
        }
        dks_log("spawn: %d characters %s", g_list_len, g_list_from_file ? "from darksign_spawn_list.tsv" : "built in (no spawn list)");
    }
    *out = g_list;
    *from_file = g_list_from_file;
    return g_list_len;
}

DksResult dks_spawn(const DksSpawnable *who, int count)
{
    dks_spawn_scan();
    uintptr_t wcm = world_chr_man();
    if (!g_wcm_slot) {
        set_status("Not available: this game version's WorldChrMan wasn't recognised.");
        return DKS_NOT_FOUND;
    }
    if (!main_player(wcm)) {
        set_status("Load your character first.");
        return DKS_NOT_LOADED;
    }
    if (!debug_creator(wcm)) {
        set_status("Not available: the game's debug character creator wasn't found (WorldChrMan +%x).",
                   g_player_offset + DEBUG_CREATOR_AFTER_PLAYER);
        return DKS_NOT_FOUND;
    }
    if (count < 1)
        count = 1;
    if (count > 20)
        count = 20;
    int queued = 0;
    for (int i = 0; i < count && g_queue_len < MAX_QUEUE; i++, queued++)
        g_queue[g_queue_len++] = (Request){*who, i};
    set_status("Spawning %d x %s (%s)...", queued, who->name, who->game);
    return DKS_OK;
}

static int read_player_place(uintptr_t player, float pos[3], float quat[4])
{
    uintptr_t physics = read_ptr(read_ptr(player + CHR_MODULES) + MODULES_PHYSICS);
    return physics && read_mem(physics + PHYSICS_POSITION, pos, 3 * sizeof(float)) &&
           read_mem(physics + PHYSICS_ORIENTATION, quat, 4 * sizeof(float));
}

static void spawn_watch(void);

static void track(uintptr_t chr, uint32_t entity)
{
    /* Forget characters that are gone, then add this one. */
    int kept = 0;
    for (int i = 0; i < g_spawned_len; i++) {
        uint32_t id = 0;
        if (is_object(g_spawned[i].chr) && read_mem(g_spawned[i].chr + CHR_ENTITY_ID, &id, 4) && id == g_spawned[i].entity)
            g_spawned[kept++] = g_spawned[i];
    }
    g_spawned_len = kept;
    if (g_spawned_len < MAX_TRACKED)
        g_spawned[g_spawned_len++] = (Spawned){.chr = chr, .entity = entity};
}

/* Hands one request to the game's debug creator. */
static int start_request(uintptr_t wcm, uintptr_t creator, const Request *r)
{
    float pos[3], quat[4], at[4] = {0, 0, 0, 0};
    uintptr_t player = main_player(wcm);
    if (!player || !read_player_place(player, pos, quat))
        return 0;
    dks_spawn_position(pos, quat, r->n, at);

    uint32_t entity = ENTITY_BASE + (g_next_entity++ % ENTITY_COUNT);
    int32_t npc = r->who.npc_param, think = r->who.think_param, talk = -1, init_param = -1;
    wchar_t name[0x20] = {0};
    _snwprintf(name, 0x1F, L"c%04d", r->who.chr);
    uintptr_t init = creator + CREATOR_INIT;
    uint8_t one = 1;
    g_current_before = read_ptr(creator + CREATOR_LAST_CREATED);
    if (!write_mem(init + INIT_POSITION, at, sizeof at) || !write_mem(init + INIT_NPC_PARAM, &npc, 4) ||
        !write_mem(init + INIT_THINK_PARAM, &think, 4) || !write_mem(init + INIT_ENTITY_ID, &entity, 4) ||
        !write_mem(init + INIT_TALK_ID, &talk, 4) || !write_mem(init + INIT_NAME, name, sizeof name) ||
        !write_mem(init + INIT_CHARA_INIT_PARAM, &init_param, 4))
        return 0;
    pending_write(&r->who);
    MemoryBarrier();
    if (!write_mem(creator + CREATOR_SPAWN, &one, 1)) {
        pending_clear();
        return 0;
    }
    g_current = *r;
    g_current_entity = entity;
    g_current_since = GetTickCount64();
    g_active = 1;
    dks_log("spawn: asked for c%04d NpcParam %d ThinkParam %d entity %u at %.1f %.1f %.1f", r->who.chr, npc, think, entity,
            at[0], at[1], at[2]);
    return 1;
}

void dks_spawn_request_clear(void) { InterlockedExchange(&g_clear_requested, 1); }

void dks_spawn_update(void)
{
    if (InterlockedExchange(&g_clear_requested, 0))
        dks_spawn_clear();
    spawn_watch();
    if (g_pending_open && !g_active && g_pending_spawned && GetTickCount64() - g_pending_spawned > SPAWN_SAFE_MS)
        pending_clear();
    if (!g_active && !g_queue_len)
        return;
    uintptr_t wcm = world_chr_man();
    uintptr_t creator = debug_creator(wcm);
    if (!creator || !main_player(wcm)) {
        if (g_queue_len || g_active)
            set_status("Spawning stopped: no character loaded (loading screen?).");
        g_queue_len = 0;
        g_active = 0;
        return;
    }
    if (g_active) {
        uintptr_t last = read_ptr(creator + CREATOR_LAST_CREATED);
        uint32_t id = 0;
        int ours = last && is_object(last) && read_mem(last + CHR_ENTITY_ID, &id, 4) &&
                   (id == g_current_entity || last != g_current_before);
        if (ours) {
            track(last, id == g_current_entity ? g_current_entity : id);
            g_pending_spawned = GetTickCount64();
            if (id != g_current_entity)
                dks_log("spawn: c%04d created with entity %u, not %u", g_current.who.chr, id, g_current_entity);
            g_active = 0;
            if (!g_queue_len)
                set_status("Spawned %s (%s). %d spawned by the menu now.", g_current.who.name, g_current.who.game, dks_spawn_alive());
        } else if (GetTickCount64() - g_current_since > SPAWN_TIMEOUT_MS) {
            uint8_t flag = 0;
            read_mem(creator + CREATOR_SPAWN, &flag, 1);
            if (flag) {
                uint8_t zero = 0;
                write_mem(creator + CREATOR_SPAWN, &zero, 1);
            }
            set_status("Elden Ring didn't create %s (c%04d) within %d s%s. Send the test menu log.", g_current.who.name,
                       g_current.who.chr, SPAWN_TIMEOUT_MS / 1000,
                       flag ? " (the request was never picked up)" : " (model or params missing from the mod?)");
            g_active = 0;
            g_queue_len = 0;
            pending_clear();
            return;
        } else
            return;
    }
    if (g_queue_len) {
        Request next = g_queue[0];
        memmove(g_queue, g_queue + 1, sizeof g_queue[0] * (size_t)--g_queue_len);
        if (!start_request(wcm, creator, &next)) {
            set_status("Couldn't hand the spawn request to the game (memory not writable?).");
            g_queue_len = 0;
        }
    }
}

int dks_spawn_pending(void) { return g_queue_len + g_active; }

static uintptr_t chr_data(uintptr_t chr) { return read_ptr(read_ptr(chr + CHR_MODULES) + MODULES_DATA); }

/* The SpEffect IDs on a character, at most WATCH_FX_MAX; -1 when the list can't be read. */
static int read_effects(uintptr_t chr, int32_t *out)
{
    uintptr_t fx = read_ptr(chr + CHR_SPEFFECT);
    if (!fx)
        return -1;
    int n = 0;
    uintptr_t seen[64];
    for (uintptr_t e = read_ptr(fx + SPEFFECT_HEAD); e && n < WATCH_FX_MAX; e = read_ptr(e + FXENTRY_NEXT)) {
        for (int k = 0; k < n && k < 64; k++)
            if (seen[k] == e)
                return n; /* a loop: stop */
        int32_t id = 0;
        if (!read_mem(e + FXENTRY_ID, &id, 4))
            return n ? n : -1;
        if (n < 64)
            seen[n] = e;
        out[n++] = id;
    }
    return n;
}

static int has_effect(const int32_t *list, int n, int32_t id)
{
    for (int k = 0; k < n; k++)
        if (list[k] == id)
            return 1;
    return 0;
}

/* Writes "fx? +a +b -c" when the effects changed since the last sample (an enemy that stops taking hits after a weapon
   art shows here with what the hit put on it). */
static void watch_effects(Spawned *s, int first)
{
    int32_t now[WATCH_FX_MAX];
    int n = read_effects(s->chr, now);
    if (n < 0)
        return;
    char line[400];
    int len = 0;
    for (int k = 0; k < n && len < (int)sizeof line - 16; k++)
        if (first || !has_effect(s->fx, s->fx_n, now[k]))
            len += snprintf(line + len, sizeof line - (size_t)len, " +%d", now[k]);
    if (!first)
        for (int k = 0; k < s->fx_n && len < (int)sizeof line - 16; k++)
            if (!has_effect(now, n, s->fx[k]))
                len += snprintf(line + len, sizeof line - (size_t)len, " -%d", s->fx[k]);
    if (len > 0)
        dks_log("watch: %u fx?%s (%d now)", s->entity, line, n);
    memcpy(s->fx, now, sizeof now[0] * (size_t)n);
    s->fx_n = n;
}

/* The watch (RULE 4): what each spawned character is doing, written to the log when it changes: health after a hit,
   when it stops moving and when it moves again, and when its position stops being a number. An enemy that "freezes
   and can't be hit" shows here as alive or dead, standing or not, so the next look at the log says which. */
static void spawn_watch(void)
{
    ULONGLONG now = GetTickCount64();
    for (int i = 0; i < g_spawned_len; i++) {
        Spawned *s = &g_spawned[i];
        if (now - s->seen < WATCH_SAMPLE_MS)
            continue;
        uint32_t id = 0;
        if (!is_object(s->chr) || !read_mem(s->chr + CHR_ENTITY_ID, &id, 4) || id != s->entity)
            continue;
        int first = s->seen == 0;
        s->seen = now;
        uintptr_t modules = read_ptr(s->chr + CHR_MODULES), data = read_ptr(modules + MODULES_DATA);
        int32_t hp = 0, hp_max = 0, anim[2] = {-1, -1};
        float pos[3] = {0}, quat[4];
        read_mem(data + DATA_HP, &hp, 4);
        read_mem(data + DATA_HP_MAX, &hp_max, 4);
        int placed = read_player_place(s->chr, pos, quat);
        read_mem(read_ptr(modules + MODULES_TIMEACT) + TIMEACT_ANIM, &anim[0], 4);
        read_mem(read_ptr(modules + MODULES_EVENT) + EVENT_ANIM, &anim[1], 4);
        float dx = pos[0] - s->pos[0], dy = pos[1] - s->pos[1], dz = pos[2] - s->pos[2];
        float moved = dx * dx + dy * dy + dz * dz;
        int bad = !placed || pos[0] != pos[0] || pos[1] != pos[1] || pos[2] != pos[2] || moved != moved;
        int moving = !bad && moved > WATCH_MOVE_M * WATCH_MOVE_M;
        const char *what = NULL;
        if (first)
            what = "spawned";
        else if (hp != s->hp)
            what = hp <= 0 ? "died" : hp < s->hp ? "hit" : "healed";
        else if (bad && !s->still)
            what = "position is not a number";
        else if (!moving && !s->still)
            what = "stands still";
        else if (moving && s->still)
            what = "moves again";
        else if (moving && now - s->logged >= WATCH_LINE_MS)
            what = "moving";
        else if (!moving && (anim[0] != s->anim[0] || anim[1] != s->anim[1]) && now - s->logged >= WATCH_SAMPLE_MS * 2)
            what = "still, new animation";
        if (what) {
            dks_log("watch: %u %s: hp %d/%d (was %d), at %.1f %.1f %.1f, a? %d b? %d", s->entity, what, hp, hp_max, first ? hp : s->hp,
                    pos[0], pos[1], pos[2], anim[0], anim[1]);
            s->logged = now;
        }
        s->still = !moving;
        s->hp = hp;
        s->pos[0] = pos[0], s->pos[1] = pos[1], s->pos[2] = pos[2];
        s->anim[0] = anim[0], s->anim[1] = anim[1];
        watch_effects(s, first);
    }
}

int dks_spawn_alive(void)
{
    int alive = 0;
    for (int i = 0; i < g_spawned_len; i++) {
        uint32_t id = 0;
        int32_t hp = 0;
        uintptr_t chr = g_spawned[i].chr;
        if (is_object(chr) && read_mem(chr + CHR_ENTITY_ID, &id, 4) && id == g_spawned[i].entity &&
            read_mem(chr_data(chr) + DATA_HP, &hp, 4) && hp > 0)
            alive++;
    }
    return alive;
}

int dks_spawn_clear(void)
{
    dks_spawn_scan();
    g_queue_len = 0;
    if (!g_wcm_slot) {
        set_status("Not available: this game version's WorldChrMan wasn't recognised.");
        return -1;
    }
    uintptr_t wcm = world_chr_man();
    int remove = remove_wanted() && g_remove;
    if (remove_wanted() && !g_remove)
        dks_log("spawn: DKSIGN_SPAWN_REMOVE=1 but RemoveChrIns wasn't found; killing instead");
    int cleared = 0;
    for (int i = 0; i < g_spawned_len; i++) {
        uint32_t id = 0;
        uintptr_t chr = g_spawned[i].chr;
        if (!is_object(chr) || !read_mem(chr + CHR_ENTITY_ID, &id, 4) || id != g_spawned[i].entity)
            continue;
        if (remove && wcm) {
            g_remove((void *)wcm, (void *)chr);
            cleared++;
        } else {
            int32_t hp = 0, zero = 0;
            uintptr_t data = chr_data(chr);
            if (read_mem(data + DATA_HP, &hp, 4) && hp > 0 && write_mem(data + DATA_HP, &zero, 4))
                cleared++;
        }
    }
    g_spawned_len = 0;
    set_status(cleared ? "Cleared %d spawned enem%s (%s)." : "No spawned enemies to clear.", cleared, cleared == 1 ? "y" : "ies",
               remove ? "removed" : "killed");
    return cleared;
}

/* God mode (see spawn.h). The bit and the HP top-up are applied from the render thread every frame; turning it off
   clears only the bit this sets, so the game's other debug bits are left alone. */
#define DATA_DEBUG_FLAGS 0x19B
#define DEBUG_NO_DAMAGE 0x02

static volatile LONG g_god = -1; /* -1: DKSIGN_GOD not read yet */
static volatile LONG g_god_applied;
static int g_god_logged;

int dks_god_on(void)
{
    if (g_god < 0) {
        wchar_t v[8];
        DWORD n = GetEnvironmentVariableW(L"DKSIGN_GOD", v, 8);
        if (InterlockedCompareExchange(&g_god, n > 0 && n < 8 && v[0] == L'1', -1) == -1 && g_god > 0)
            dks_log("god mode: on at launch (DKSIGN_GOD=1)");
    }
    return g_god > 0;
}

void dks_god_set(int on)
{
    dks_god_on();
    InterlockedExchange(&g_god, on ? 1 : 0);
    g_god_logged = 0;
    dks_log("god mode: %s", on ? "on" : "off");
}

void dks_god_update(void)
{
    int on = dks_god_on();
    if (!on && !g_god_applied)
        return;
    dks_spawn_scan();
    uintptr_t player = main_player(world_chr_man());
    uintptr_t data = is_object(player) ? chr_data(player) : 0;
    if (!is_object(data)) {
        g_god_logged = 0; /* loading screen: log again once a character is back */
        return;
    }
    uint8_t flags = 0;
    if (!read_mem(data + DATA_DEBUG_FLAGS, &flags, 1))
        return;
    uint8_t want = on ? (uint8_t)(flags | DEBUG_NO_DAMAGE) : (uint8_t)(flags & ~DEBUG_NO_DAMAGE);
    if (want != flags)
        write_mem(data + DATA_DEBUG_FLAGS, &want, 1);
    InterlockedExchange(&g_god_applied, on);
    int32_t hp = 0, max_hp = 0;
    if (on && read_mem(data + DATA_HP, &hp, 4) && read_mem(data + DATA_HP_MAX, &max_hp, 4) && max_hp > 0 && hp > 0 &&
        hp < max_hp)
        write_mem(data + DATA_HP, &max_hp, 4);
    if (on && !g_god_logged) {
        g_god_logged = 1;
        dks_log("god mode: applied to the player (debug flags %02x -> %02x, HP %d / %d)", flags, want, hp, max_hp);
    }
}

/* Fly and no-clip (see spawn.h). Offsets for the current game: ChrIns+0x58 is ChrCtrl and ChrCtrl+0xF0 its flags
   word, bit 2 "disable map collision" and bit 3 "disable map collision 2" (fromsoftware-rs chr_ins.rs ChrCtrlFlags;
   EldenRingTool's "No map collision" sets bit 3 of the same byte); ChrPhysicsModule+0x1D3 is the "no gravity" byte
   EldenRingTool's "No gravity" sets (fromsoftware-rs physics.rs: fade_out_gravity_disabled). Bit 19 "disable object
   collision" is reset by the game every frame (fromsoftware-rs), so it is set again every frame and may not hold. */
#define CHR_CTRL 0x58
#define CTRL_FLAGS 0xF0
#define CTRL_NO_MAP_HIT ((1u << 2) | (1u << 3))
#define CTRL_NO_OBJECT_HIT (1u << 19)
#define PHYSICS_NO_GRAVITY 0x1D3
#define FLY_SPEED_MIN 2.0f
#define FLY_SPEED_MAX 100.0f

static volatile LONG g_fly, g_noclip;
static float g_fly_speed = 15.0f;
static int g_gravity_applied;  /* we set the no-gravity byte */
static uint32_t g_ctrl_applied; /* flag bits we set */
static int g_fly_logged;
static LARGE_INTEGER g_fly_last;

void dks_fly_set(int on)
{
    InterlockedExchange(&g_fly, on ? 1 : 0);
    g_fly_logged = 0;
    dks_log("fly: %s", on ? "on" : "off");
}

int dks_fly_on(void) { return g_fly > 0; }

void dks_noclip_set(int on)
{
    InterlockedExchange(&g_noclip, on ? 1 : 0);
    g_fly_logged = 0;
    dks_log("no-clip: %s", on ? "on" : "off");
}

int dks_noclip_on(void) { return g_noclip > 0; }

float dks_fly_speed(void) { return g_fly_speed; }

void dks_fly_set_speed(float metres_per_second)
{
    if (!(metres_per_second >= FLY_SPEED_MIN))
        metres_per_second = FLY_SPEED_MIN;
    if (metres_per_second > FLY_SPEED_MAX)
        metres_per_second = FLY_SPEED_MAX;
    g_fly_speed = metres_per_second;
}

static int key_down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

void dks_fly_update(int keys)
{
    int noclip = dks_noclip_on(), fly = dks_fly_on() || noclip;
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    float dt = g_fly_last.QuadPart && freq.QuadPart ? (float)(now.QuadPart - g_fly_last.QuadPart) / (float)freq.QuadPart : 0.0f;
    g_fly_last = now;
    if (dt > 0.1f)
        dt = 0.1f; /* a hitch or a loading screen: don't jump */
    if (!fly && !g_gravity_applied && !g_ctrl_applied)
        return;
    dks_spawn_scan();
    uintptr_t player = main_player(world_chr_man());
    uintptr_t physics = is_object(player) ? read_ptr(read_ptr(player + CHR_MODULES) + MODULES_PHYSICS) : 0;
    uintptr_t ctrl = is_object(player) ? read_ptr(player + CHR_CTRL) : 0;
    if (!is_object(physics) || !is_object(ctrl)) {
        /* loading screen: the character is new afterwards, so nothing of ours is left on it */
        g_gravity_applied = 0;
        g_ctrl_applied = 0;
        g_fly_logged = 0;
        return;
    }

    uint8_t gravity = 0, want_gravity;
    if (!read_mem(physics + PHYSICS_NO_GRAVITY, &gravity, 1))
        return;
    want_gravity = fly ? 1 : g_gravity_applied ? 0 : gravity;
    if (want_gravity != gravity)
        write_mem(physics + PHYSICS_NO_GRAVITY, &want_gravity, 1);
    g_gravity_applied = fly;

    uint32_t flags = 0, want_flags;
    if (!read_mem(ctrl + CTRL_FLAGS, &flags, 4))
        return;
    uint32_t bits = noclip ? CTRL_NO_MAP_HIT | CTRL_NO_OBJECT_HIT : 0;
    want_flags = (flags & ~(g_ctrl_applied & ~bits)) | bits;
    if (want_flags != flags)
        write_mem(ctrl + CTRL_FLAGS, &want_flags, 4);
    g_ctrl_applied = bits;

    if (fly && !g_fly_logged) {
        g_fly_logged = 1;
        dks_log("fly: applied to the player (no-gravity byte %u -> %u, ChrCtrl flags %08x -> %08x, no-clip %s)", gravity,
                want_gravity, flags, want_flags, noclip ? "on" : "off");
    }

    if (!fly || !keys || !(dt > 0.0f))
        return;
    int up = key_down(VK_PRIOR) - key_down(VK_NEXT), forward = key_down(VK_HOME) - key_down(VK_END);
    if (!up && !forward)
        return;
    float pos[3], quat[4], to[3];
    if (!read_mem(physics + PHYSICS_POSITION, pos, sizeof pos) || !read_mem(physics + PHYSICS_ORIENTATION, quat, sizeof quat))
        return;
    dks_fly_step(pos, quat, forward, up, g_fly_speed, dt, to);
    write_mem(physics + PHYSICS_POSITION, to, sizeof to);
}
