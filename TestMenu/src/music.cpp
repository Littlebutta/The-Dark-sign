/* DS3's music on the ported maps (porter Maps/MapMusic.cs). The porter decodes each DS3 track to a WAV file in
   Mods\ER\darksign_music and lists, in Mods\ER\darksign_music.tsv, one line per music region of a ported map:
   "playing flag<TAB>file<TAB>loop start<TAB>loop end<TAB>fade-out ms<TAB>map<TAB>region". The maps' converted event
   scripts turn a region's playing flag on while DS3 would play it (switched on by the boss fight or always on, and the
   player inside it) and off again. Here a thread watches those flags and plays the track of the flag turned on last,
   looping between DS3's loop points, fading out the old track when another takes over (a boss's second phase) or when
   no flag is on (the boss died, the player left Firelink Shrine, a death or map load reset the flags).

   The music plays on its own XAudio2 stream, as the DLL sounds of fallback "sounds" D (DOOM RING's approach,
   stanleymov/DOOM-RING, MIT: a DLL's own audio stream instead of the game's Wwise), so no Wwise bank has to load.
   On whenever port-maps wrote darksign_music.tsv; off with DKSIGN_NO_MAP_MUSIC=1 (launcher fallback "map-music" B). DKSIGN_MUSIC_VOLUME=0-100 sets the level (default 70). */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <xaudio2.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <map>
#include <string>
#include <vector>

#include "game.h"
#include "music.h"

namespace {

constexpr DWORD TICK_MS = 20;        // fade steps
constexpr int POLL_TICKS = 5;        // flags read every 100 ms
constexpr int FADE_IN_MS = 1000;
constexpr size_t MAX_CACHED = 3;     // decoded tracks kept in memory (a boss's two phases and the area's)

struct Entry {
    uint32_t flag;
    std::wstring file;
    int loop_start, loop_end, fade_ms;
    std::string where;
    bool on;
    unsigned long long stamp; // when it last turned on
};

struct Wave {
    WAVEFORMATEX format{};
    std::vector<uint8_t> data;
};

struct Playing {
    IXAudio2SourceVoice *voice = nullptr;
    std::wstring file;
    float volume = 0, target = 0, step = 0; // per tick
};

std::vector<Entry> g_entries;
wchar_t g_dir[MAX_PATH];
IXAudio2 *g_audio;
IXAudio2MasteringVoice *g_master;
float g_level = 0.7f;
std::map<std::wstring, Wave *> g_cache;
std::vector<std::wstring> g_cache_order;
std::vector<Playing> g_voices;
int g_logged;

std::wstring widen(const char *s)
{
    std::wstring w;
    for (; *s; s++)
        w.push_back((wchar_t)(unsigned char)*s);
    return w;
}

bool load_list(const wchar_t *path)
{
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return false;
    char line[1024];
    while (fgets(line, sizeof line, f)) {
        line[strcspn(line, "\r\n")] = 0;
        char *cols[7] = {};
        int n = 0;
        for (char *p = line; n < 7;) {
            cols[n++] = p;
            char *tab = strchr(p, '\t');
            if (!tab)
                break;
            *tab = 0;
            p = tab + 1;
        }
        if (n < 5 || cols[0][0] < '0' || cols[0][0] > '9')
            continue;
        Entry e{};
        e.flag = (uint32_t)strtoul(cols[0], nullptr, 10);
        e.file = widen(cols[1]);
        e.loop_start = atoi(cols[2]);
        e.loop_end = atoi(cols[3]);
        e.fade_ms = atoi(cols[4]);
        e.where = std::string(n > 5 ? cols[5] : "") + " " + (n > 6 ? cols[6] : "");
        if (e.flag && !e.file.empty())
            g_entries.push_back(e);
    }
    fclose(f);
    return true;
}

Wave *read_wave(const std::wstring &file)
{
    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%ls\\%ls", g_dir, file.c_str());
    path[MAX_PATH - 1] = 0;
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return nullptr;
    std::vector<uint8_t> bytes;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size > 0) {
        bytes.resize((size_t)size);
        if (fread(bytes.data(), 1, bytes.size(), f) != bytes.size())
            bytes.clear();
    }
    fclose(f);
    if (bytes.size() < 44 || memcmp(bytes.data(), "RIFF", 4) || memcmp(bytes.data() + 8, "WAVE", 4))
        return nullptr;
    auto *wave = new Wave();
    bool fmt = false;
    for (size_t at = 12; at + 8 <= bytes.size();) {
        uint32_t chunk;
        memcpy(&chunk, bytes.data() + at + 4, 4);
        if (at + 8 + chunk > bytes.size())
            break;
        if (!memcmp(bytes.data() + at, "fmt ", 4) && chunk >= 16) {
            memcpy(&wave->format, bytes.data() + at + 8, 16);
            wave->format.cbSize = 0;
            fmt = true;
        } else if (!memcmp(bytes.data() + at, "data", 4)) {
            wave->data.assign(bytes.begin() + at + 8, bytes.begin() + at + 8 + chunk);
        }
        at += 8 + chunk + (chunk & 1);
    }
    if (!fmt || wave->data.empty() || wave->format.wFormatTag != WAVE_FORMAT_PCM) {
        delete wave;
        return nullptr;
    }
    return wave;
}

bool in_use(const Wave *wave)
{
    for (auto &p : g_voices) {
        auto found = g_cache.find(p.file);
        if (p.voice && found != g_cache.end() && found->second == wave)
            return true;
    }
    return false;
}

Wave *wave_for(const std::wstring &file)
{
    auto found = g_cache.find(file);
    if (found != g_cache.end())
        return found->second;
    Wave *wave = read_wave(file);
    if (!wave)
        return nullptr;
    g_cache[file] = wave;
    g_cache_order.push_back(file);
    // Drop the oldest track no voice plays any more.
    for (size_t i = 0; g_cache.size() > MAX_CACHED && i < g_cache_order.size();) {
        auto old = g_cache.find(g_cache_order[i]);
        if (old != g_cache.end() && old->second != wave && !in_use(old->second)) {
            delete old->second;
            g_cache.erase(old);
            g_cache_order.erase(g_cache_order.begin() + (long)i);
        } else {
            i++;
        }
    }
    return wave;
}

float step_for(int ms, float distance)
{
    int ticks = ms > 0 ? ms / (int)TICK_MS : 1;
    return distance / (float)(ticks > 0 ? ticks : 1);
}

void start(const Entry &e)
{
    Wave *wave = wave_for(e.file);
    if (!wave) {
        if (g_logged++ < 40)
            dks_log("music: no track %ls for%s (flag %u)", e.file.c_str(), e.where.c_str(), e.flag);
        return;
    }
    Playing p;
    p.file = e.file;
    if (FAILED(g_audio->CreateSourceVoice(&p.voice, &wave->format)))
        return;
    XAUDIO2_BUFFER buffer{};
    buffer.AudioBytes = (UINT32)wave->data.size();
    buffer.pAudioData = wave->data.data();
    buffer.Flags = XAUDIO2_END_OF_STREAM;
    UINT32 frames = wave->format.nBlockAlign ? (UINT32)(wave->data.size() / wave->format.nBlockAlign) : 0;
    if (e.loop_start >= 0 && e.loop_end > e.loop_start && (UINT32)e.loop_end < frames) {
        buffer.LoopBegin = (UINT32)e.loop_start;
        buffer.LoopLength = (UINT32)(e.loop_end - e.loop_start + 1);
        buffer.LoopCount = XAUDIO2_LOOP_INFINITE;
    }
    p.voice->SetVolume(0);
    if (FAILED(p.voice->SubmitSourceBuffer(&buffer)) || FAILED(p.voice->Start(0))) {
        p.voice->DestroyVoice();
        return;
    }
    p.target = g_level;
    p.step = step_for(FADE_IN_MS, g_level);
    g_voices.push_back(p);
    if (g_logged++ < 40)
        dks_log("music: playing %ls for%s (flag %u)", e.file.c_str(), e.where.c_str(), e.flag);
}

void fade_out(Playing &p, int ms)
{
    p.target = 0;
    p.step = step_for(ms > 0 ? ms : 2000, p.volume > 0 ? p.volume : g_level);
}

void tick_fades()
{
    for (size_t i = 0; i < g_voices.size();) {
        Playing &p = g_voices[i];
        if (p.volume < p.target)
            p.volume = p.volume + p.step < p.target ? p.volume + p.step : p.target;
        else if (p.volume > p.target)
            p.volume = p.volume - p.step > p.target ? p.volume - p.step : p.target;
        p.voice->SetVolume(p.volume);
        XAUDIO2_VOICE_STATE state;
        p.voice->GetState(&state, XAUDIO2_VOICE_NOSAMPLESPLAYED);
        if ((p.target <= 0 && p.volume <= 0) || state.BuffersQueued == 0) {
            p.voice->DestroyVoice();
            g_voices.erase(g_voices.begin() + (long)i);
        } else {
            i++;
        }
    }
}

const std::wstring *current_file()
{
    for (auto it = g_voices.rbegin(); it != g_voices.rend(); ++it)
        if (it->target > 0)
            return &it->file;
    return nullptr;
}

DWORD WINAPI run(LPVOID)
{
    unsigned long long clock = 0, started = 0; // the stamp of the flag whose track was started last
    for (int t = 0;; t++) {
        Sleep(TICK_MS);
        tick_fades();
        if (t % POLL_TICKS)
            continue;
        // Which track should play: the one whose flag turned on last.
        const Entry *want = nullptr;
        bool flags_known = true;
        for (auto &e : g_entries) {
            int state = dks_get_event_flag(e.flag);
            if (state < 0) {
                flags_known = false; // no character loaded (title screen)
                break;
            }
            if (state && !e.on)
                e.stamp = ++clock;
            e.on = state != 0;
        }
        if (flags_known)
            for (auto &e : g_entries)
                if (e.on && (!want || e.stamp > want->stamp))
                    want = &e;
        const std::wstring *now = current_file();
        if (want && now && *now == want->file)
            continue;
        // A track that plays once and has ended isn't started again until its flag turns on anew.
        if (want && !now && want->stamp == started)
            continue;
        if (!want && !now)
            continue;
        // Another track (or none): the playing one fades out with its DS3 fade-out time.
        for (auto &p : g_voices)
            if (p.target > 0) {
                int ms = 2000;
                for (auto &e : g_entries)
                    if (e.file == p.file)
                        ms = e.fade_ms;
                fade_out(p, ms);
                if (g_logged++ < 40)
                    dks_log("music: fading out %ls", p.file.c_str());
            }
        if (want) {
            start(*want);
            started = want->stamp;
        }
    }
    return 0;
}

} // namespace

extern "C" void dks_music_init(void)
{
    wchar_t flag[8];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_NO_MAP_MUSIC", flag, 8);
    if (n > 0 && n < 8 && flag[0] == L'1') {
        dks_log("music: off (DKSIGN_NO_MAP_MUSIC=1, fallback map-music B)");
        return;
    }
    wchar_t mod[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_MOD_DIR", mod, MAX_PATH)) {
        dks_log("music: DKSIGN_MOD_DIR not set, DS3 music off");
        return;
    }
    wchar_t list[MAX_PATH];
    _snwprintf(list, MAX_PATH, L"%ls\\darksign_music.tsv", mod);
    list[MAX_PATH - 1] = 0;
    _snwprintf(g_dir, MAX_PATH, L"%ls\\darksign_music", mod);
    g_dir[MAX_PATH - 1] = 0;
    if (!load_list(list)) {
        dks_log("music: no darksign_music.tsv (rebuild the content); DS3 music off");
        return;
    }
    if (g_entries.empty()) {
        dks_log("music: darksign_music.tsv lists no tracks; DS3 music off");
        return;
    }
    wchar_t volume[8];
    n = GetEnvironmentVariableW(L"DKSIGN_MUSIC_VOLUME", volume, 8);
    if (n > 0 && n < 8) {
        int v = _wtoi(volume);
        g_level = (float)(v < 0 ? 0 : v > 100 ? 100 : v) / 100.0f;
    }
    using Create = HRESULT(WINAPI *)(IXAudio2 **, UINT32, XAUDIO2_PROCESSOR);
    HMODULE lib = LoadLibraryW(L"xaudio2_9.dll");
    if (!lib)
        lib = LoadLibraryW(L"xaudio2_8.dll");
    auto create = lib ? (Create)GetProcAddress(lib, "XAudio2Create") : nullptr;
    if (!create || FAILED(create(&g_audio, 0, XAUDIO2_DEFAULT_PROCESSOR))) {
        dks_log("music: XAudio2 is not available; DS3 music off");
        return;
    }
    if (FAILED(g_audio->CreateMasteringVoice(&g_master, XAUDIO2_DEFAULT_CHANNELS, XAUDIO2_DEFAULT_SAMPLERATE, 0, nullptr,
                                             nullptr, AudioCategory_GameMedia))) {
        dks_log("music: no audio output; DS3 music off");
        return;
    }
    HANDLE thread = CreateThread(nullptr, 0, run, nullptr, 0, nullptr);
    if (thread)
        CloseHandle(thread);
    dks_log("music: %zu DS3 music regions listed, level %d%%", g_entries.size(), (int)(g_level * 100 + 0.5f));
}
