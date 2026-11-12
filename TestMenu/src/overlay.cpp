/* dksign_testmenu: The Dark Sign's Elden Ring test menu, drawn inside the game like ReShade's overlay.
   ModEngine2 loads it (external_dlls). It hooks the game's DirectX 12 swap chain to draw a Dear ImGui window, and
   F1 shows or hides it. While it is open, the game's keyboard and mouse input and its cursor lock are held back so
   the menu can be used. It is a testing aid for single-player, offline play only. */
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>

#include <algorithm>
#include <string>
#include <vector>

#include "MinHook.h"
#include "imgui.h"
#include "backends/imgui_impl_dx12.h"
#include "backends/imgui_impl_win32.h"
#include "game.h"
#include "motion.h"
#include "hotkeys.h"
#include "flagcheck.h"
#include "sound.h"
#include "spawn.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

namespace {

using PresentFn = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain3 *, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain3 *, UINT, UINT, const DXGI_PRESENT_PARAMETERS *);
using ResizeBuffersFn = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain3 *, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using ResizeBuffers1Fn = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain3 *, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT *, IUnknown *const *);
using ExecuteFn = void(STDMETHODCALLTYPE *)(ID3D12CommandQueue *, UINT, ID3D12CommandList *const *);
using GetDeviceStateFn = HRESULT(STDMETHODCALLTYPE *)(IDirectInputDevice8W *, DWORD, LPVOID);
using GetDeviceDataFn = HRESULT(STDMETHODCALLTYPE *)(IDirectInputDevice8W *, DWORD, LPDIDEVICEOBJECTDATA, LPDWORD, DWORD);
using SetCursorPosFn = BOOL(WINAPI *)(int, int);
using ClipCursorFn = BOOL(WINAPI *)(const RECT *);
using GetCursorPosFn = BOOL(WINAPI *)(LPPOINT);
using GetRawInputDataFn = UINT(WINAPI *)(HRAWINPUT, UINT, LPVOID, PUINT, UINT);
using GetRawInputBufferFn = UINT(WINAPI *)(PRAWINPUT, PUINT, UINT);

PresentFn o_present;
Present1Fn o_present1;
ResizeBuffersFn o_resize;
ResizeBuffers1Fn o_resize1;
ExecuteFn o_execute;
GetDeviceStateFn o_get_state;
GetDeviceDataFn o_get_data;
SetCursorPosFn o_set_cursor_pos;
ClipCursorFn o_clip_cursor;
GetCursorPosFn o_get_cursor_pos;
GetRawInputDataFn o_get_raw_input_data;
GetRawInputBufferFn o_get_raw_input_buffer;

struct Frame {
    ID3D12CommandAllocator *allocator = nullptr;
    ID3D12Resource *target = nullptr;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv{};
    UINT64 fence_value = 0;
};

ID3D12CommandQueue *g_queue;
ID3D12Device *g_device;
ID3D12DescriptorHeap *g_rtv_heap, *g_srv_heap;
ID3D12GraphicsCommandList *g_list;
ID3D12Fence *g_fence;
UINT64 g_fence_value;
HANDLE g_fence_event;
std::vector<Frame> g_frames;
bool g_ready, g_failed, g_targets_ready;
HWND g_hwnd;
WNDPROC g_game_wndproc;
volatile bool g_open;
bool g_f1_was_down;
POINT g_frozen_cursor;            // where the game sees the cursor while the menu is open
thread_local bool t_in_overlay;   // the menu's own cursor reads see the real position

// Menu state.
DksItem *g_items;
int g_item_count;
std::vector<int> g_shown;
char g_search[96];
int g_game_filter, g_kind_filter, g_selected = -1, g_quantity = 1, g_upgrade, g_affinity;
int g_rune_amount = 1000000;
const char *const ATTRIBUTE_NAMES[DKS_ATTRIBUTES] = {"Vigor", "Mind", "Endurance", "Strength", "Dexterity", "Intelligence", "Faith", "Arcane"};
int g_attributes[DKS_ATTRIBUTES];
bool g_attributes_loaded; // g_attributes holds the character's values (or the user's edits of them)
std::string g_status = "Pick an item and press Give.";
bool g_focus_search;

// The item adder's modes: one per game the items come from.
const char *const GAMES[] = {"ER", "DS3", ""};
const char *const GAME_LABELS[] = {"Elden Ring", "Dark Souls III", "All games"};
const char *const KINDS[] = {"All", "Weapon", "Armor", "Talisman", "Item", "AshOfWar"};
const char *const KIND_LABELS[] = {"All", "Weapons", "Armor", "Talismans", "Items", "Ashes of War"};
const char *const AFFINITIES[] = {"Standard", "Heavy", "Keen", "Quality", "Fire", "Flame Art", "Lightning",
                                  "Sacred", "Magic", "Cold", "Poison", "Blood", "Occult"};

void refilter()
{
    g_shown.clear();
    for (int i = 0; i < g_item_count; i++)
        if ((!GAMES[g_game_filter][0] || strcmp(g_items[i].game, GAMES[g_game_filter]) == 0) &&
            (g_kind_filter == 0 || strcmp(g_items[i].kind, KINDS[g_kind_filter]) == 0) && dks_name_matches(g_items[i].name, g_search))
            g_shown.push_back(i);
    if (g_selected >= 0 && std::find(g_shown.begin(), g_shown.end(), g_selected) == g_shown.end())
        g_selected = -1;
}

const char *result_text(DksResult r)
{
    return r == DKS_NOT_FOUND ? "Not available: this game version's code wasn't recognised."
         : r == DKS_NOT_KEPT  ? "The game didn't keep the request flag (no flag block for that ID in this save or version). "
                                "Send the test menu log."
                              : "Load your character first.";
}

void give_selected()
{
    if (g_selected < 0) {
        g_status = "Pick an item first.";
        return;
    }
    const DksItem &item = g_items[g_selected];
    int quantity = std::clamp(g_quantity, 1, 999);
    uint32_t id = dks_item_give_id(&item, g_affinity, g_upgrade);
    DksResult r = dks_give_item(id, quantity);
    if (r != DKS_OK) {
        g_status = result_text(r);
        return;
    }
    char text[200];
    snprintf(text, sizeof text, "Gave %d x %s", quantity, item.name);
    if (!strcmp(item.kind, "Weapon") && (item.max_upgrade || item.infusable))
        snprintf(text + strlen(text), sizeof text - strlen(text), " (%s +%d)", item.infusable ? AFFINITIES[g_affinity] : "",
                 std::min(g_upgrade, item.max_upgrade));
    g_status = text;
}

// The maps the porter ported, from darksign_maps.tsv next to the item list (DKSIGN_ITEMS):
// "DS3 map<TAB>Elden Ring slot<TAB>name". Read once, when the DS3 run tab first opens.
struct PortedMap {
    std::string ds3, er, name;
    uint32_t warp_flag;  // 0 in lists written before per-map warps
};
std::vector<PortedMap> g_maps;
bool g_maps_loaded;

// The DS3 boss fights with a warp, from darksign_bosses.tsv (BossArenas in the porter):
// "DS3 map<TAB>Elden Ring slot<TAB>map name<TAB>boss<TAB>warp request flag". The warp event resets the fight's flags,
// so a fight already won starts again from its first phase.
struct BossArena {
    std::string ds3, er, map, boss;
    uint32_t warp_flag;
};
std::vector<BossArena> g_bosses;
std::string g_run_status = "Starts a Dark Souls III run inside Elden Ring.";
std::string g_map_check;        // "0 FAIL, 2 WARN" from darksign_maps_check.txt
uint32_t g_pending_flag = 0;    // the request flag last set, until the event clears it
ULONGLONG g_pending_since = 0;

void load_maps()
{
    g_maps_loaded = true;
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_ITEMS", path, MAX_PATH);
    if (!n || n >= MAX_PATH)
        return;
    wchar_t *slash = wcsrchr(path, L'\\');
    if (!slash || (size_t)(slash - path) + 20 >= MAX_PATH)
        return;
    // The porter's read-back check of the ported maps: its last line is "map-validate: N FAIL, M WARN".
    wcscpy(slash + 1, L"darksign_maps_check.txt");
    if (FILE *c = _wfopen(path, L"rb")) {
        char line[512];
        while (fgets(line, sizeof line, c))
            if (strncmp(line, "map-validate:", 13) == 0) {
                line[strcspn(line, "\r\n")] = 0;
                g_map_check = line + 14;
            }
        fclose(c);
        dks_log("maps: porter check: %s", g_map_check.c_str());
    }
    wcscpy(slash + 1, L"darksign_maps.tsv");
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return;
    char line[512];
    while (fgets(line, sizeof line, f)) {
        line[strcspn(line, "\r\n")] = 0;
        char *tab1 = strchr(line, '\t');
        char *tab2 = tab1 ? strchr(tab1 + 1, '\t') : nullptr;
        if (!tab1 || !tab2 || line[0] == '#')
            continue;
        char *tab3 = strchr(tab2 + 1, '\t');
        uint32_t warp = 0;
        if (tab3) {
            *tab3 = 0;
            warp = (uint32_t)strtoul(tab3 + 1, nullptr, 10);
        }
        *tab1 = *tab2 = 0;
        g_maps.push_back({line, tab1 + 1, tab2 + 1, warp});
    }
    fclose(f);
    dks_log("maps: %d ported", (int)g_maps.size());

    wcscpy(slash + 1, L"darksign_bosses.tsv");
    if (FILE *b = _wfopen(path, L"rb")) {
        while (fgets(line, sizeof line, b)) {
            line[strcspn(line, "\r\n")] = 0;
            if (line[0] == '#')
                continue;
            char *cols[5] = {line};
            int n = 1;
            for (char *p = line; *p && n < 5; ++p)
                if (*p == '\t') {
                    *p = 0;
                    cols[n++] = p + 1;
                }
            if (n < 5)
                continue;
            uint32_t flag = (uint32_t)strtoul(cols[4], nullptr, 10);
            if (flag)
                g_bosses.push_back({cols[0], cols[1], cols[2], cols[3], flag});
        }
        fclose(b);
    }
    dks_log("maps: %d boss arenas", (int)g_bosses.size());
}

void request_flag(uint32_t flag, const char *done)
{
    DksResult r = dks_set_event_flag(flag, 1);
    if (r != DKS_OK) {
        g_run_status = result_text(r);
        if (r == DKS_NOT_KEPT)
            dks_log("ds3 run: request flag %u did not stick; check RunEvents.FlagBlock against this game version", flag);
        return;
    }
    g_run_status = done;
    g_pending_flag = flag;
    g_pending_since = GetTickCount64();
    dks_log("ds3 run: set request flag %u", flag);
    g_open = false; // hand the game its input back for the warp
}

void draw_ds3_run()
{
    if (!g_maps_loaded)
        load_maps();
    int active = dks_get_event_flag(DKS_FLAG_DS3_RUN_ACTIVE);
    if (active < 0)
        ImGui::TextDisabled("DS3 run: load a character first");
    else
        ImGui::Text("DS3 run: %s", active ? "in progress" : "not started");
    ImGui::Separator();

    const PortedMap *start = nullptr;
    for (const PortedMap &m : g_maps)
        if (m.ds3 == "m40_00_00_00" && !start)
            start = &m;
    if (g_maps.empty())
        ImGui::TextWrapped("No Dark Souls III maps are ported yet. Press Rebuild content in the launcher, then restart the game.");
    else {
        ImGui::TextUnformatted("Ported Dark Souls III maps:");
        for (const PortedMap &m : g_maps)
        {
            ImGui::BulletText("%s  (%s, in Elden Ring's %s slot)", m.name.c_str(), m.ds3.c_str(), m.er.c_str());
            if (m.warp_flag) {
                ImGui::SameLine();
                ImGui::PushID(m.er.c_str());
                if (ImGui::SmallButton("Warp here"))
                    request_flag(m.warp_flag, "Warping...");
                ImGui::PopID();
            }
        }
    }
    if (!g_bosses.empty()) {
        ImGui::Spacing();
        ImGui::TextUnformatted("Boss fights (resets the fight and starts it from the first phase):");
        for (const BossArena &b : g_bosses) {
            ImGui::BulletText("%s  (%s)", b.boss.c_str(), b.map.c_str());
            ImGui::SameLine();
            ImGui::PushID((int)b.warp_flag);
            if (ImGui::SmallButton("Fight")) {
                dks_log("bosses: warp to %s in %s (%s as %s), request flag %u", b.boss.c_str(), b.map.c_str(), b.ds3.c_str(), b.er.c_str(), b.warp_flag);
                request_flag(b.warp_flag, "Warping to the boss arena...");
            }
            ImGui::PopID();
        }
    }
    ImGui::Spacing();
    ImGui::BeginDisabled(!start);
    if (ImGui::Button("Start DS3 run at the Cemetery of Ash", ImVec2(-1, 0)))
        request_flag(DKS_FLAG_DS3_START_REQUEST, "Warping to the Cemetery of Ash...");
    ImGui::EndDisabled();
    ImGui::BeginDisabled(active != 1);
    if (ImGui::Button("Leave DS3 run (back to Elden Ring)", ImVec2(-1, 0)))
        request_flag(DKS_FLAG_DS3_LEAVE_REQUEST, "Warping back to Elden Ring...");
    ImGui::EndDisabled();
    ImGui::Separator();
    ImGui::TextWrapped("%s", g_run_status.c_str());

    // Diagnostics for the first tests: did the event script pick up the request, and what did the porter's check say?
    if (g_pending_flag) {
        int still = dks_get_event_flag(g_pending_flag);
        ULONGLONG waited = GetTickCount64() - g_pending_since;
        if (still == 0) {
            dks_log("ds3 run: request flag %u cleared by the event after %llu ms", g_pending_flag, waited);
            g_pending_flag = 0;
        } else if (still == 1 && waited > 5000) {
            ImGui::TextColored(ImVec4(1.0f, 0.6f, 0.3f, 1.0f), "The run event hasn't picked up the request after %llu s.", waited / 1000);
            ImGui::TextWrapped("The mod's event script (event\\common.emevd.dcx) is probably not loaded, or was built for another game version. "
                               "Send TheDarkSign\\Mods\\ER\\darksign_maps_check.txt and the test menu log.");
            static bool logged = false;
            if (!logged) {
                dks_log("ds3 run: request flag %u still set after %llu ms; common.emevd events not running?", g_pending_flag, waited);
                logged = true;
            }
        }
    }
    if (!g_map_check.empty())
        ImGui::TextDisabled("Porter check of the ported maps: %s", g_map_check.c_str());
}

// The "Spawn enemies" tab: Dark Souls III (ported) or Elden Ring characters from darksign_spawn_list.tsv (spawn.c).
int g_spawn_game;          // 0 Dark Souls III, 1 Elden Ring
int g_spawn_selected = -1; // index into the spawn list
int g_spawn_count = 1;
char g_spawn_search[96];
bool g_spawn_bosses = true;
bool g_spawn_ds3_models_only = true; // hide DS3 enemies the port could only put on an Elden Ring body (method C)

// A DS3 entry either wears the model the port built for it or the Elden Ring template it was left on, in which case
// it looks like that Elden Ring enemy. The spawn list says which (items.c).
bool has_ds3_model(const DksSpawnable &s) { return dks_spawn_has_own_model(&s) != 0; }

void draw_spawn()
{
    const DksSpawnable *list;
    int from_file = 0;
    int count = dks_spawn_list(&list, &from_file);
    static const char *const GAME_CODES[] = {"DS3", "ER"};

    ImGui::RadioButton("Dark Souls III enemies", &g_spawn_game, 0);
    ImGui::SameLine();
    ImGui::RadioButton("Elden Ring enemies", &g_spawn_game, 1);
    ImGui::SameLine();
    ImGui::Checkbox("Bosses", &g_spawn_bosses);
    if (g_spawn_game == 0) {
        ImGui::SameLine();
        ImGui::Checkbox("Only DS3 models", &g_spawn_ds3_models_only);
        if (ImGui::IsItemHovered())
            ImGui::SetTooltip("Untick to also list DS3 enemies the port put on an Elden Ring body\n"
                              "(they have DS3's name, HP and souls but look like the Elden Ring enemy).");
    }
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##spawnsearch", "Search enemies...", g_spawn_search, sizeof g_spawn_search);

    if (g_spawn_selected >= count || (g_spawn_selected >= 0 && strcmp(list[g_spawn_selected].game, GAME_CODES[g_spawn_game]) != 0))
        g_spawn_selected = -1;
    int shown = 0, hidden_bodies = 0;
    ImGui::BeginChild("spawnlist", ImVec2(0, -200), ImGuiChildFlags_Borders);
    for (int i = 0; i < count; i++) {
        const DksSpawnable &s = list[i];
        if (strcmp(s.game, GAME_CODES[g_spawn_game]) != 0 || (s.boss && !g_spawn_bosses) || !dks_name_matches(s.name, g_spawn_search))
            continue;
        if (g_spawn_ds3_models_only && !has_ds3_model(s)) {
            hidden_bodies++;
            continue;
        }
        shown++;
        ImGui::PushID(i);
        if (ImGui::Selectable(s.name, g_spawn_selected == i, ImGuiSelectableFlags_AllowDoubleClick)) {
            g_spawn_selected = i;
            if (ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left))
                dks_spawn(&s, g_spawn_count);
        }
        bool crashed = dks_spawn_crashed(s.chr);
        ImGui::SameLine(ImGui::GetContentRegionAvail().x - (crashed ? 330 : 220));
        if (crashed) {
            ImGui::TextColored(ImVec4(1.0f, 0.45f, 0.35f, 1.0f), "crashed last time");
            ImGui::SameLine();
        }
        ImGui::TextDisabled("%s%s c%04d", s.boss ? "boss " : "", has_ds3_model(s) ? "" : "ER body", s.chr);
        ImGui::PopID();
    }
    if (!shown)
        ImGui::TextWrapped(g_spawn_game == 0 && from_file
                               ? "No Dark Souls III enemy has its own model in this build (Fallbacks: enemy port is off, or it failed). See Work\\reports\\enemy-port.tsv."
                           : g_spawn_game == 0
                               ? "No spawn list yet. Press Rebuild content in the launcher (the enemy port writes it), then restart the game."
                               : "Nothing matches.");
    ImGui::EndChild();
    if (hidden_bodies)
        ImGui::TextDisabled("%d more DS3 enemies on Elden Ring bodies are hidden (untick \"Only DS3 models\").", hidden_bodies);
    if (!from_file)
        ImGui::TextDisabled("Built-in list (no darksign_spawn_list.tsv yet): rebuild the content for every enemy.");

    const DksSpawnable *who = g_spawn_selected >= 0 ? &list[g_spawn_selected] : nullptr;
    if (who && dks_spawn_crashed(who->chr))
        ImGui::TextColored(ImVec4(1.0f, 0.45f, 0.35f, 1.0f), "The game went down right after spawning this one last time (its ported model is the likely cause).");
    ImGui::SetNextItemWidth(160);
    ImGui::SliderInt("How many", &g_spawn_count, 1, 10);
    ImGui::BeginDisabled(!who);
    if (ImGui::Button(who ? (std::string("Spawn ") + who->name + " next to me").c_str() : "Spawn", ImVec2(-1, 0)) && who)
        dks_spawn(who, g_spawn_count);
    ImGui::EndDisabled();

    ImGui::Separator();
    int alive = dks_spawn_alive(), pending = dks_spawn_pending();
    ImGui::Text("Spawned by this menu and still alive: %d%s", alive, pending ? "  (more on the way)" : "");
    if (ImGui::Button("Clear enemies spawned from this menu", ImVec2(-1, 0)))
        dks_spawn_clear();
    ImGui::TextDisabled("Ctrl+Shift+F4 clears them too. Only enemies spawned here are touched.");
    ImGui::Separator();
    ImGui::TextWrapped("%s", dks_spawn_status());
}

void change_runes(bool add)
{
    long long held = dks_get_runes();
    DksResult r = dks_set_runes(add ? std::max(held, 0LL) + g_rune_amount : g_rune_amount);
    g_status = r == DKS_OK ? "Runes updated." : result_text(r);
}

void load_attributes()
{
    int level;
    g_attributes_loaded = dks_get_attributes(g_attributes, &level) == DKS_OK;
}

void apply_attributes()
{
    DksResult r = dks_set_attributes(g_attributes);
    if (r != DKS_OK) {
        g_status = result_text(r);
        return;
    }
    char text[120];
    snprintf(text, sizeof text, "Now level %d. Max HP, FP and stamina catch up after resting at a Site of Grace.", dks_level_for(g_attributes));
    g_status = text;
}

void draw_attributes()
{
    if (!g_attributes_loaded)
        load_attributes();
    if (!g_attributes_loaded) {
        ImGui::TextDisabled("Load a character to change its level.");
        return;
    }
    int current[DKS_ATTRIBUTES], level = 0;
    bool known = dks_get_attributes(current, &level) == DKS_OK;
    ImGui::Text("Level %d now, %d with these attributes", known ? level : 0, dks_level_for(g_attributes));
    for (int i = 0; i < DKS_ATTRIBUTES; i++) {
        if (i % 2)
            ImGui::SameLine(ImGui::GetContentRegionAvail().x * 0.5f + ImGui::GetStyle().WindowPadding.x);
        ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x * (i % 2 ? 0.55f : 0.27f));
        ImGui::SliderInt(ATTRIBUTE_NAMES[i], &g_attributes[i], 1, DKS_ATTRIBUTE_MAX);
    }
    for (int preset : {40, 60, 80, 99}) {
        char label[24];
        snprintf(label, sizeof label, "All %d", preset);
        if (ImGui::Button(label))
            std::fill(std::begin(g_attributes), std::end(g_attributes), preset);
        ImGui::SameLine();
    }
    if (ImGui::Button("Reset to character"))
        load_attributes();
    if (ImGui::Button("Apply level and attributes", ImVec2(-1, 0)))
        apply_attributes();
}

void draw_menu()
{
    ImGuiIO &io = ImGui::GetIO();
    ImGui::SetNextWindowSize(ImVec2(620, 680), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x * 0.5f, io.DisplaySize.y * 0.5f), ImGuiCond_FirstUseEver, ImVec2(0.5f, 0.5f));
    bool open = true;
    if (!ImGui::Begin("The Dark Sign  |  Test menu  (F1)", &open, ImGuiWindowFlags_NoCollapse)) {
        ImGui::End();
        return;
    }
    if (!open)
        g_open = false;

    bool god = dks_god_on() != 0;
    if (ImGui::Checkbox("God mode (no damage, HP kept full)", &god))
        dks_god_set(god);
    ImGui::SameLine();
    ImGui::TextDisabled("Ctrl+Shift+F5");
    bool fly = dks_fly_on() != 0;
    if (ImGui::Checkbox("Fly", &fly))
        dks_fly_set(fly);
    ImGui::SameLine();
    ImGui::TextDisabled("Ctrl+Shift+F6");
    ImGui::SameLine(0, 24);
    bool noclip = dks_noclip_on() != 0;
    if (ImGui::Checkbox("No-clip (through walls, flies too)", &noclip))
        dks_noclip_set(noclip);
    ImGui::SameLine();
    ImGui::TextDisabled("Ctrl+Shift+F7");
    if (fly || noclip) {
        float speed = dks_fly_speed();
        ImGui::SetNextItemWidth(220);
        if (ImGui::SliderFloat("Fly speed", &speed, 2.0f, 100.0f, "%.0f m/s"))
            dks_fly_set_speed(speed);
        ImGui::SameLine();
        ImGui::TextDisabled("PgUp/PgDn up/down, Home/End forward/back");
    }

    bool changed = false, levels = false, run = false, spawn = false;
    if (ImGui::BeginTabBar("games")) {
        for (int i = 0; i < IM_ARRAYSIZE(GAME_LABELS); i++)
            if (ImGui::BeginTabItem(GAME_LABELS[i])) {
                if (g_game_filter != i) {
                    g_game_filter = i;
                    changed = true;
                }
                ImGui::EndTabItem();
            }
        if (ImGui::BeginTabItem("Level and attributes")) {
            levels = true;
            ImGui::EndTabItem();
        }
        if (ImGui::BeginTabItem("DS3 run")) {
            run = true;
            ImGui::EndTabItem();
        }
        if (ImGui::BeginTabItem("Spawn enemies")) {
            spawn = true;
            ImGui::EndTabItem();
        }
        ImGui::EndTabBar();
    }
    if (spawn) {
        draw_spawn();
        ImGui::End();
        return;
    }
    if (run) {
        draw_ds3_run();
        ImGui::End();
        return;
    }
    if (levels) {
        draw_attributes();
        ImGui::Separator();
        ImGui::TextWrapped("%s", g_status.c_str());
        ImGui::End();
        return;
    }
    g_attributes_loaded = false; // re-read the character next time the tab opens
    ImGui::SetNextItemWidth(-200);
    if (g_focus_search) {
        ImGui::SetKeyboardFocusHere();
        g_focus_search = false;
    }
    changed |= ImGui::InputTextWithHint("##search", "Search items...", g_search, sizeof g_search);
    ImGui::SameLine();
    ImGui::SetNextItemWidth(-1);
    changed |= ImGui::Combo("##kind", &g_kind_filter, KIND_LABELS, IM_ARRAYSIZE(KIND_LABELS));
    if (changed)
        refilter();

    if (!g_item_count)
        ImGui::TextWrapped("No item list. Start Elden Ring from The Dark Sign launcher.");
    ImGui::BeginChild("items", ImVec2(0, -190), ImGuiChildFlags_Borders);
    ImGuiListClipper clipper;
    clipper.Begin((int)g_shown.size());
    while (clipper.Step()) {
        for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++) {
            const DksItem &item = g_items[g_shown[row]];
            ImGui::PushID(g_shown[row]);
            if (ImGui::Selectable(item.name, g_selected == g_shown[row], ImGuiSelectableFlags_AllowDoubleClick)) {
                g_selected = g_shown[row];
                if (ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left))
                    give_selected();
            }
            ImGui::SameLine(ImGui::GetContentRegionAvail().x - 130);
            ImGui::TextDisabled(g_game_filter == 2 ? "%s  %s" : "%s", item.kind, item.game);
            ImGui::PopID();
        }
    }
    ImGui::EndChild();
    ImGui::TextDisabled("%d of %d items", (int)g_shown.size(), g_item_count);

    const DksItem *item = g_selected >= 0 ? &g_items[g_selected] : nullptr;
    ImGui::SetNextItemWidth(120);
    ImGui::InputInt("Quantity", &g_quantity);
    if (item && !strcmp(item->kind, "Weapon")) {
        ImGui::SameLine();
        ImGui::BeginDisabled(item->max_upgrade == 0);
        ImGui::SetNextItemWidth(140);
        ImGui::SliderInt("Upgrade", &g_upgrade, 0, std::max(item->max_upgrade, 1), "+%d");
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::BeginDisabled(!item->infusable);
        ImGui::SetNextItemWidth(-1);
        ImGui::Combo("##affinity", &g_affinity, AFFINITIES, IM_ARRAYSIZE(AFFINITIES));
        ImGui::EndDisabled();
    }
    if (ImGui::Button(item ? (std::string("Give ") + item->name).c_str() : "Give", ImVec2(-1, 0)))
        give_selected();

    ImGui::Separator();
    long long held = dks_get_runes();
    if (held >= 0)
        ImGui::Text("Runes held: %lld", held);
    else
        ImGui::TextDisabled("Runes held: load a character");
    ImGui::SetNextItemWidth(200);
    ImGui::InputInt("##runes", &g_rune_amount, 10000, 1000000);
    ImGui::SameLine();
    if (ImGui::Button("Add runes"))
        change_runes(true);
    ImGui::SameLine();
    if (ImGui::Button("Set runes"))
        change_runes(false);

    ImGui::Separator();
    ImGui::TextWrapped("%s", g_status.c_str());
    ImGui::End();
}

void toggle()
{
    g_open = !g_open;
    if (g_open) {
        dks_scan_game();
        g_focus_search = true;
        if (o_clip_cursor)
            o_clip_cursor(nullptr); // the game locks the cursor to its window; free it while the menu is open
        if (o_get_cursor_pos)
            o_get_cursor_pos(&g_frozen_cursor);
    }
    ImGui::GetIO().MouseDrawCursor = g_open;
}

// ---- input: the game doesn't see keyboard and mouse while the menu is open ----

LRESULT CALLBACK wndproc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (g_open && g_ready) {
        ImGui_ImplWin32_WndProcHandler(hwnd, msg, wp, lp);
        switch (msg) {
        case WM_INPUT:
            return DefWindowProcW(hwnd, msg, wp, lp);
        case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP: case WM_CHAR:
        case WM_MOUSEMOVE: case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
        case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_MBUTTONDOWN: case WM_MBUTTONUP:
        case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL: case WM_SETCURSOR:
            return msg == WM_SETCURSOR ? TRUE : 0;
        }
    }
    return CallWindowProcW(g_game_wndproc, hwnd, msg, wp, lp);
}

HRESULT STDMETHODCALLTYPE h_get_state(IDirectInputDevice8W *self, DWORD size, LPVOID data)
{
    HRESULT hr = o_get_state(self, size, data);
    if (g_open && SUCCEEDED(hr))
        memset(data, 0, size);
    return hr;
}

HRESULT STDMETHODCALLTYPE h_get_data(IDirectInputDevice8W *self, DWORD size, LPDIDEVICEOBJECTDATA data, LPDWORD count, DWORD flags)
{
    HRESULT hr = o_get_data(self, size, data, count, flags);
    if (g_open && SUCCEEDED(hr) && count && !(flags & DIGDD_PEEK))
        *count = 0;
    return hr;
}

BOOL WINAPI h_set_cursor_pos(int x, int y) { return g_open ? TRUE : o_set_cursor_pos(x, y); }
BOOL WINAPI h_clip_cursor(const RECT *rect) { return o_clip_cursor(g_open ? nullptr : rect); }

// The game also turns cursor movement into camera movement, so while the menu is open it sees a cursor that
// doesn't move.
BOOL WINAPI h_get_cursor_pos(LPPOINT point)
{
    if (g_open && !t_in_overlay && point) {
        *point = g_frozen_cursor;
        return TRUE;
    }
    return o_get_cursor_pos(point);
}

// The game reads the mouse (camera) and keyboard as raw input. While the menu is open, mouse movement and buttons
// are emptied out and every key reads as released, so the camera stays put and no key sticks down.
void mute(RAWINPUT *input)
{
    if (input->header.dwType == RIM_TYPEMOUSE) {
        input->data.mouse.lLastX = input->data.mouse.lLastY = 0;
        input->data.mouse.usButtonFlags = 0;
        input->data.mouse.usButtonData = 0;
    } else if (input->header.dwType == RIM_TYPEKEYBOARD) {
        input->data.keyboard.Flags |= RI_KEY_BREAK;
        input->data.keyboard.Message = WM_KEYUP;
    }
}

UINT WINAPI h_get_raw_input_data(HRAWINPUT handle, UINT command, LPVOID data, PUINT size, UINT header_size)
{
    UINT result = o_get_raw_input_data(handle, command, data, size, header_size);
    if (g_open && command == RID_INPUT && data && result != (UINT)-1 && result >= sizeof(RAWINPUTHEADER))
        mute((RAWINPUT *)data);
    return result;
}

UINT WINAPI h_get_raw_input_buffer(PRAWINPUT data, PUINT size, UINT header_size)
{
    UINT count = o_get_raw_input_buffer(data, size, header_size);
    if (g_open && data && count != (UINT)-1) {
        RAWINPUT *input = data;
        for (UINT i = 0; i < count; i++) {
            mute(input);
            // NEXTRAWINPUTBLOCK: entries are pointer-aligned.
            input = (RAWINPUT *)(((ULONG_PTR)((BYTE *)input + input->header.dwSize) + sizeof(ULONG_PTR) - 1) & ~(sizeof(ULONG_PTR) - 1));
        }
    }
    return count;
}

// ---- rendering ----

void wait_for(UINT64 value)
{
    if (g_fence->GetCompletedValue() < value) {
        g_fence->SetEventOnCompletion(value, g_fence_event);
        WaitForSingleObject(g_fence_event, 2000);
    }
}

void release_targets()
{
    if (g_fence)
        wait_for(g_fence_value);
    for (Frame &f : g_frames)
        if (f.target) {
            f.target->Release();
            f.target = nullptr;
        }
    g_targets_ready = false;
}

bool create_targets(IDXGISwapChain3 *swap_chain)
{
    DXGI_SWAP_CHAIN_DESC desc;
    swap_chain->GetDesc(&desc);
    if (desc.BufferCount != g_frames.size()) {
        for (Frame &f : g_frames)
            if (f.allocator)
                f.allocator->Release();
        if (g_rtv_heap)
            g_rtv_heap->Release();
        g_frames.assign(desc.BufferCount, Frame{});
        D3D12_DESCRIPTOR_HEAP_DESC heap = {D3D12_DESCRIPTOR_HEAP_TYPE_RTV, desc.BufferCount, D3D12_DESCRIPTOR_HEAP_FLAG_NONE, 0};
        if (FAILED(g_device->CreateDescriptorHeap(&heap, IID_PPV_ARGS(&g_rtv_heap))))
            return false;
        for (Frame &f : g_frames)
            if (FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&f.allocator))))
                return false;
    }
    UINT step = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    D3D12_CPU_DESCRIPTOR_HANDLE handle = g_rtv_heap->GetCPUDescriptorHandleForHeapStart();
    for (UINT i = 0; i < g_frames.size(); i++) {
        if (FAILED(swap_chain->GetBuffer(i, IID_PPV_ARGS(&g_frames[i].target))))
            return false;
        g_frames[i].rtv = handle;
        g_device->CreateRenderTargetView(g_frames[i].target, nullptr, handle);
        handle.ptr += step;
    }
    g_targets_ready = true;
    return true;
}

bool init(IDXGISwapChain3 *swap_chain)
{
    if (FAILED(swap_chain->GetDevice(IID_PPV_ARGS(&g_device))))
        return false;
    DXGI_SWAP_CHAIN_DESC desc;
    swap_chain->GetDesc(&desc);
    g_hwnd = desc.OutputWindow;

    D3D12_DESCRIPTOR_HEAP_DESC srv = {D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 1, D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE, 0};
    if (FAILED(g_device->CreateDescriptorHeap(&srv, IID_PPV_ARGS(&g_srv_heap))) ||
        FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence))) || !create_targets(swap_chain) ||
        FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_frames[0].allocator, nullptr, IID_PPV_ARGS(&g_list))))
        return false;
    g_list->Close();
    g_fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    ImGui::CreateContext();
    ImGuiIO &io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    char font[MAX_PATH];
    UINT len = GetWindowsDirectoryA(font, MAX_PATH - 32);
    if (len && len < MAX_PATH - 32) {
        strcat(font, "\\Fonts\\segoeui.ttf");
        if (GetFileAttributesA(font) != INVALID_FILE_ATTRIBUTES)
            io.Fonts->AddFontFromFileTTF(font, 20.0f);
    }
    ImGui::StyleColorsDark();
    ImGuiStyle &style = ImGui::GetStyle();
    style.WindowRounding = 6;
    style.FrameRounding = 4;
    style.Colors[ImGuiCol_WindowBg].w = 0.94f;
    style.Colors[ImGuiCol_TitleBgActive] = ImVec4(0.30f, 0.24f, 0.14f, 1);
    style.Colors[ImGuiCol_Header] = ImVec4(0.42f, 0.35f, 0.23f, 0.6f);
    style.Colors[ImGuiCol_HeaderHovered] = ImVec4(0.52f, 0.43f, 0.27f, 0.8f);
    style.Colors[ImGuiCol_Button] = ImVec4(0.36f, 0.30f, 0.19f, 0.8f);
    style.Colors[ImGuiCol_ButtonHovered] = ImVec4(0.50f, 0.41f, 0.25f, 1);

    ImGui_ImplWin32_Init(g_hwnd);
    ImGui_ImplDX12_InitInfo info;
    info.Device = g_device;
    info.CommandQueue = g_queue;
    info.NumFramesInFlight = (int)g_frames.size();
    info.RTVFormat = desc.BufferDesc.Format;
    info.SrvDescriptorHeap = g_srv_heap;
    info.LegacySingleSrvCpuDescriptor = g_srv_heap->GetCPUDescriptorHandleForHeapStart();
    info.LegacySingleSrvGpuDescriptor = g_srv_heap->GetGPUDescriptorHandleForHeapStart();
    if (!ImGui_ImplDX12_Init(&info))
        return false;

    g_game_wndproc = (WNDPROC)SetWindowLongPtrW(g_hwnd, GWLP_WNDPROC, (LONG_PTR)wndproc);
    dks_log("overlay: ready (%u buffers, format %d)", desc.BufferCount, (int)desc.BufferDesc.Format);
    return true;
}

void render(IDXGISwapChain3 *swap_chain)
{
    if (g_failed)
        return;
    if (!g_ready) {
        if (!g_queue)
            return; // the game hasn't submitted work yet
        if (!(g_ready = init(swap_chain))) {
            g_failed = true;
            dks_log("overlay: setup failed, the test menu is off");
            return;
        }
    }

    // F1 counts only while the game has focus, so other programs keep their F1.
    // Ctrl+Shift+F1 is the "start DS3 run" hotkey (hotkeys.c), not a menu toggle.
    bool down = (GetAsyncKeyState(VK_F1) & 0x8000) != 0 && !((GetAsyncKeyState(VK_CONTROL) & 0x8000) && (GetAsyncKeyState(VK_SHIFT) & 0x8000));
    if (down && !g_f1_was_down && GetForegroundWindow() == g_hwnd)
        toggle();
    g_f1_was_down = down;
    dks_spawn_update(); // spawns queued from the menu go on while it's closed
    dks_god_update();   // god mode stays on while the menu is closed
    // Fly keys count while the game has focus and the menu isn't taking the keyboard (a text box, a list).
    dks_fly_update(GetForegroundWindow() == g_hwnd && (!g_open || !ImGui::GetIO().WantCaptureKeyboard));
    if (!g_open) {
        // Hold no back-buffer references while the menu is closed: a ResizeBuffers(1) the hooks miss (another overlay's
        // hook in front of ours, Alt+Enter) would otherwise fail with DXGI_ERROR_INVALID_CALL and take the game down.
        if (g_targets_ready)
            release_targets();
        return;
    }
    if (!g_targets_ready && !create_targets(swap_chain))
        return;

    ImGui_ImplDX12_NewFrame();
    t_in_overlay = true;
    ImGui_ImplWin32_NewFrame();
    t_in_overlay = false;
    ImGui::NewFrame();
    draw_menu();
    ImGui::Render();

    Frame &frame = g_frames[swap_chain->GetCurrentBackBufferIndex()];
    wait_for(frame.fence_value);
    frame.allocator->Reset();
    g_list->Reset(frame.allocator, nullptr);

    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = frame.target;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    g_list->ResourceBarrier(1, &barrier);
    g_list->OMSetRenderTargets(1, &frame.rtv, FALSE, nullptr);
    g_list->SetDescriptorHeaps(1, &g_srv_heap);
    ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), g_list);
    std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
    g_list->ResourceBarrier(1, &barrier);
    g_list->Close();

    ID3D12CommandList *lists[] = {g_list};
    o_execute(g_queue, 1, lists);
    g_queue->Signal(g_fence, ++g_fence_value);
    frame.fence_value = g_fence_value;
}

HRESULT STDMETHODCALLTYPE h_present(IDXGISwapChain3 *self, UINT sync, UINT flags)
{
    render(self);
    return o_present(self, sync, flags);
}

HRESULT STDMETHODCALLTYPE h_present1(IDXGISwapChain3 *self, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS *params)
{
    render(self);
    return o_present1(self, sync, flags, params);
}

HRESULT STDMETHODCALLTYPE h_resize(IDXGISwapChain3 *self, UINT count, UINT w, UINT h, DXGI_FORMAT format, UINT flags)
{
    if (g_ready)
        release_targets();
    return o_resize(self, count, w, h, format, flags);
}

HRESULT STDMETHODCALLTYPE h_resize1(IDXGISwapChain3 *self, UINT count, UINT w, UINT h, DXGI_FORMAT format, UINT flags,
                                    const UINT *masks, IUnknown *const *queues)
{
    if (g_ready)
        release_targets();
    return o_resize1(self, count, w, h, format, flags, masks, queues);
}

// The game's own direct queue is the one the menu draws on.
void STDMETHODCALLTYPE h_execute(ID3D12CommandQueue *self, UINT count, ID3D12CommandList *const *lists)
{
    if (!g_queue && self->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT) {
        self->AddRef();
        g_queue = self;
    }
    o_execute(self, count, lists);
}

// ---- setup: find the methods to hook from throwaway DirectX 12 and DirectInput objects ----

bool hook(void *target, void *detour, void **original, const char *name)
{
    if (MH_CreateHook(target, detour, original) != MH_OK || MH_EnableHook(target) != MH_OK) {
        dks_log("overlay: cannot hook %s", name);
        return false;
    }
    return true;
}

bool hook_dx12()
{
    WNDCLASSEXW wc = {sizeof wc};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"DarkSignOverlayProbe";
    RegisterClassExW(&wc);
    HWND window = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);

    bool ok = false;
    ID3D12Device *device = nullptr;
    ID3D12CommandQueue *queue = nullptr;
    IDXGIFactory4 *factory = nullptr;
    IDXGISwapChain1 *swap_chain = nullptr;
    D3D12_COMMAND_QUEUE_DESC queue_desc = {};
    DXGI_SWAP_CHAIN_DESC1 sc = {};
    sc.Width = sc.Height = 64;
    sc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sc.SampleDesc.Count = 1;
    sc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sc.BufferCount = 2;
    sc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    if (SUCCEEDED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))) &&
        SUCCEEDED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue))) &&
        SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) &&
        SUCCEEDED(factory->CreateSwapChainForHwnd(queue, window, &sc, nullptr, nullptr, &swap_chain))) {
        void **sc_vtbl = *(void ***)swap_chain;
        void **queue_vtbl = *(void ***)queue;
        ok = hook(sc_vtbl[8], (void *)h_present, (void **)&o_present, "Present") &&
             hook(sc_vtbl[13], (void *)h_resize, (void **)&o_resize, "ResizeBuffers") &&
             hook(queue_vtbl[10], (void *)h_execute, (void **)&o_execute, "ExecuteCommandLists");
        hook(sc_vtbl[22], (void *)h_present1, (void **)&o_present1, "Present1");
        IDXGISwapChain3 *sc3 = nullptr;
        if (SUCCEEDED(swap_chain->QueryInterface(IID_PPV_ARGS(&sc3)))) {
            hook((*(void ***)sc3)[39], (void *)h_resize1, (void **)&o_resize1, "ResizeBuffers1");
            sc3->Release();
        }
    } else {
        dks_log("overlay: could not create the DirectX 12 probe");
    }
    if (swap_chain) swap_chain->Release();
    if (factory) factory->Release();
    if (queue) queue->Release();
    if (device) device->Release();
    DestroyWindow(window);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return ok;
}

void hook_input()
{
    hook((void *)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetCursorPos"), (void *)h_set_cursor_pos,
         (void **)&o_set_cursor_pos, "SetCursorPos");
    hook((void *)GetProcAddress(GetModuleHandleW(L"user32.dll"), "ClipCursor"), (void *)h_clip_cursor,
         (void **)&o_clip_cursor, "ClipCursor");
    hook((void *)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetCursorPos"), (void *)h_get_cursor_pos,
         (void **)&o_get_cursor_pos, "GetCursorPos");
    hook((void *)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetRawInputData"), (void *)h_get_raw_input_data,
         (void **)&o_get_raw_input_data, "GetRawInputData");
    hook((void *)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetRawInputBuffer"), (void *)h_get_raw_input_buffer,
         (void **)&o_get_raw_input_buffer, "GetRawInputBuffer");

    using CreateFn = HRESULT(WINAPI *)(HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);
    HMODULE dinput = LoadLibraryW(L"dinput8.dll");
    auto create = dinput ? (CreateFn)GetProcAddress(dinput, "DirectInput8Create") : nullptr;
    IDirectInput8W *di = nullptr;
    IDirectInputDevice8W *keyboard = nullptr;
    if (create && SUCCEEDED(create(GetModuleHandleW(nullptr), DIRECTINPUT_VERSION, IID_IDirectInput8W, (void **)&di, nullptr)) &&
        SUCCEEDED(di->CreateDevice(GUID_SysKeyboard, &keyboard, nullptr))) {
        void **vtbl = *(void ***)keyboard;
        hook(vtbl[9], (void *)h_get_state, (void **)&o_get_state, "GetDeviceState");
        hook(vtbl[10], (void *)h_get_data, (void **)&o_get_data, "GetDeviceData");
    }
    if (keyboard) keyboard->Release();
    if (di) di->Release();
}

// The item, rune and flag addresses are looked up here, off the render thread, so the first F1 doesn't stall a frame
// for the scan; whatever is missing is looked for again a little later.
void prescan()
{
    dks_scan_game();
    dks_spawn_scan();
    for (int i = 0; i < 3; i++) {
        Sleep(15000);
        dks_rescan_missing();
    }
}

DWORD WINAPI start(LPVOID)
{
    g_item_count = dks_load_items(&g_items);
    refilter();
    if (MH_Initialize() != MH_OK) {
        dks_log("overlay: MinHook failed to start");
        return 0;
    }
    dks_hotkeys_start();
    // me3 and ModEngine2 load this DLL while the game is still starting up. Game code is scanned and hooked only once
    // the game has its window (its code unpacked and its globals set up), so a scan that runs too early doesn't leave
    // the motion hook or the item and flag calls "not found" for the whole session.
    if (!dks_wait_for_game_window(120000))
        dks_log("game window not seen after 120 s; hooking anyway");
    dks_motion_init();
    dks_sound_init();
    dks_flagcheck_start();
    // Fallback "F1 test menu" B: no overlay (if hooking DirectX 12 breaks the game); the hotkeys still work.
    wchar_t no_overlay[8];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_NO_OVERLAY", no_overlay, 8);
    if (n > 0 && n < 8 && no_overlay[0] == L'1') {
        dks_log("overlay: off (DKSIGN_NO_OVERLAY=1); hotkeys only");
        prescan();
        return 0;
    }
    if (!hook_dx12()) {
        prescan();
        return 0;
    }
    hook_input();
    dks_log("overlay: hooks in place; press F1 in game");
    prescan();
    return 0;
}

bool is_elden_ring()
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    const wchar_t *name = wcsrchr(exe, L'\\');
    return _wcsicmp(name ? name + 1 : exe, L"eldenring.exe") == 0;
}

} // namespace

/* Lets the launcher recognise this DLL. */
extern "C" __declspec(dllexport) const char *DkSignTestMenuVersion(void) { return "dksign_testmenu 8"; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        if (!is_elden_ring())
            return TRUE;
        dks_open_log();
        dks_log("test menu loaded");
        if (HANDLE thread = CreateThread(nullptr, 0, start, nullptr, 0, nullptr))
            CloseHandle(thread);
    }
    // The game unloading us means it shut down in an orderly way (quit from the menu, the window closed, or the game
    // ending its own main loop after an error). A hard crash never gets here, so this line tells the two apart.
    else if (reason == DLL_PROCESS_DETACH && is_elden_ring())
        dks_log("exit: the game is shutting down in an orderly way (no hard crash up to here)");
    return TRUE;
}
