/* Fallback "sounds" D (launcher Fallbacks window): the DLL plays the ported DS3 sounds itself, on its own XAudio2
   stream, instead of through the game's Wwise bank. DOOM RING (stanleymov/DOOM-RING, MIT) plays its weapon sounds
   the same way, from a DLL on its own audio stream, and its erfps2 hooks the game's TAE event dispatch through the
   CSChrTaeAnimEvent vtable (RVA 0x2a392c0 in Elden Ring 2.7.2, entry 2 executes one event, the event's type comes
   first in its argument block and a pointer to the event's parameters after it).

   The porter (DARKSIGN_SOUND_METHOD=dll) writes each DS3 sample to Mods\ER\darksign_sounds\<c|s><ID>.wav and gives
   the TAE sound events (128/129: kind, ID, ...) the IDs 900000000+. Here, every executed sound event with such an ID
   plays its WAV. The game asks Wwise for the same ID too, which has no such event, so nothing plays twice.

   The hook only goes in on Elden Ring 2.7.2.0 (the version the RVA is for) and after checking that the vtable's
   entries point into the game's code; otherwise it logs why and does nothing. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <xaudio2.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <unordered_map>
#include <vector>

#include "MinHook.h"
#include "game.h"
#include "sound.h"

namespace {

constexpr uintptr_t TAE_EVENT_VTABLE_RVA = 0x2a392c0; // Elden Ring 2.7.2.0 (DOOM RING src/rva/ww.rs)
constexpr int FIRST_PORTED_ID = 900000000;          // Porter SoundPort.IdBase
constexpr int MAX_VOICES = 24;

struct TaeEventArgs {
    uint32_t event_type;
    uint32_t pad;
    const int32_t *params;
};
// Only the first two arguments are known; the hook forwards all four register arguments and the return register
// unchanged, so a third/fourth argument or a return value the game relies on survives the detour.
using ExecOne = uintptr_t(__fastcall *)(void *event, TaeEventArgs *args, void *a3, void *a4);

struct Wave {
    WAVEFORMATEX format{};
    std::vector<uint8_t> data;
};

ExecOne o_exec_one;
IXAudio2 *g_audio;
IXAudio2MasteringVoice *g_master;
wchar_t g_dir[MAX_PATH];
SRWLOCK g_lock = SRWLOCK_INIT;
std::unordered_map<int64_t, Wave *> g_waves; // (kind << 32) | id -> wave, all loaded at start (no disk reads in game)
std::unordered_map<int64_t, ULONGLONG> g_last; // when that sound was last seen, to play each event once
IXAudio2SourceVoice *g_voices[MAX_VOICES];
int g_next_voice;
int g_played;

int64_t key(int kind, int id) { return ((int64_t)kind << 32) | (uint32_t)id; }

Wave *load_wave(int kind, int id)
{
    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%ls\\%c%09d.wav", g_dir, kind == 5 ? L's' : L'c', id);
    path[MAX_PATH - 1] = 0;
    FILE *f = _wfopen(path, L"rb");
    if (!f)
        return nullptr;
    std::vector<uint8_t> bytes;
    uint8_t chunk[65536];
    size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0)
        bytes.insert(bytes.end(), chunk, chunk + n);
    fclose(f);
    // RIFF/WAVE with a 16-byte PCM fmt chunk and a data chunk, as the porter writes them.
    if (bytes.size() < 44 || memcmp(bytes.data(), "RIFF", 4) || memcmp(bytes.data() + 8, "WAVE", 4))
        return nullptr;
    auto *wave = new Wave();
    bool fmt = false;
    for (size_t at = 12; at + 8 <= bytes.size();) {
        uint32_t size;
        memcpy(&size, bytes.data() + at + 4, 4);
        if (at + 8 + size > bytes.size())
            break;
        if (!memcmp(bytes.data() + at, "fmt ", 4) && size >= 16) {
            memcpy(&wave->format, bytes.data() + at + 8, 16);
            wave->format.cbSize = 0;
            fmt = true;
        } else if (!memcmp(bytes.data() + at, "data", 4)) {
            wave->data.assign(bytes.begin() + at + 8, bytes.begin() + at + 8 + size);
        }
        at += 8 + size + (size & 1);
    }
    if (!fmt || wave->data.empty() || wave->format.wFormatTag != WAVE_FORMAT_PCM) {
        delete wave;
        return nullptr;
    }
    return wave;
}

void play(int kind, int id)
{
    AcquireSRWLockExclusive(&g_lock);
    // A sound event runs every frame of its window: a new play only after the event has been quiet a moment.
    ULONGLONG now = GetTickCount64();
    auto last = g_last.find(key(kind, id));
    bool fresh = last == g_last.end() || now - last->second > 150;
    g_last[key(kind, id)] = now;
    Wave *wave = nullptr;
    if (fresh) {
        auto found = g_waves.find(key(kind, id));
        wave = found != g_waves.end() ? found->second : nullptr;
    }
    if (wave && g_audio) {
        // Voices are reused round robin; one still playing is cut for the new sound.
        IXAudio2SourceVoice *&voice = g_voices[g_next_voice];
        g_next_voice = (g_next_voice + 1) % MAX_VOICES;
        if (voice) {
            voice->DestroyVoice();
            voice = nullptr;
        }
        if (SUCCEEDED(g_audio->CreateSourceVoice(&voice, &wave->format))) {
            XAUDIO2_BUFFER buffer{};
            buffer.AudioBytes = (UINT32)wave->data.size();
            buffer.pAudioData = wave->data.data();
            buffer.Flags = XAUDIO2_END_OF_STREAM;
            if (SUCCEEDED(voice->SubmitSourceBuffer(&buffer)))
                voice->Start(0);
            if (g_played++ < 20)
                dks_log("sound: playing %c%09d", kind == 5 ? 's' : 'c', id);
        }
    } else if (fresh && !wave && g_played < 20) {
        dks_log("sound: no WAV for %c%09d", kind == 5 ? 's' : 'c', id);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

uintptr_t __fastcall h_exec_one(void *event, TaeEventArgs *args, void *a3, void *a4)
{
    // Read the event before the game runs it (it may reuse the argument block), play after.
    int kind = 0, id = 0;
    if (args && args->params && (args->event_type == 128 || args->event_type == 129)) {
        kind = args->params[0];
        id = args->params[1];
    }
    uintptr_t ret = o_exec_one(event, args, a3, a4);
    if ((kind == 1 || kind == 5) && id >= FIRST_PORTED_ID && id < FIRST_PORTED_ID + 100000000)
        play(kind, id);
    return ret;
}

bool game_is_2720()
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    DWORD ignored = 0, size = GetFileVersionInfoSizeW(exe, &ignored);
    if (!size)
        return false;
    std::vector<uint8_t> info(size);
    VS_FIXEDFILEINFO *fixed = nullptr;
    UINT len = 0;
    if (!GetFileVersionInfoW(exe, 0, size, info.data()) || !VerQueryValueW(info.data(), L"\\", (void **)&fixed, &len) || !fixed)
        return false;
    dks_log("sound: game version %u.%u.%u.%u", HIWORD(fixed->dwFileVersionMS), LOWORD(fixed->dwFileVersionMS),
            HIWORD(fixed->dwFileVersionLS), LOWORD(fixed->dwFileVersionLS));
    return fixed->dwFileVersionMS == MAKELONG(7, 2) && fixed->dwFileVersionLS == MAKELONG(0, 2);
}

bool in_game_code(uintptr_t address)
{
    MEMORY_BASIC_INFORMATION mbi;
    HMODULE base = GetModuleHandleW(nullptr);
    return VirtualQuery((void *)address, &mbi, sizeof mbi) && mbi.AllocationBase == base && mbi.State == MEM_COMMIT
           && (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY));
}

bool start_audio()
{
    using Create = HRESULT(WINAPI *)(IXAudio2 **, UINT32, XAUDIO2_PROCESSOR);
    HMODULE lib = LoadLibraryW(L"xaudio2_9.dll");
    if (!lib)
        lib = LoadLibraryW(L"xaudio2_8.dll");
    auto create = lib ? (Create)GetProcAddress(lib, "XAudio2Create") : nullptr;
    if (!create || FAILED(create(&g_audio, 0, XAUDIO2_DEFAULT_PROCESSOR))) {
        dks_log("sound: XAudio2 is not available");
        return false;
    }
    if (FAILED(g_audio->CreateMasteringVoice(&g_master, XAUDIO2_DEFAULT_CHANNELS, XAUDIO2_DEFAULT_SAMPLERATE, 0, nullptr,
                                             nullptr, AudioCategory_GameEffects))) {
        dks_log("sound: no audio output");
        return false;
    }
    return true;
}

} // namespace

extern "C" void dks_sound_init(void)
{
    wchar_t flag[8];
    DWORD n = GetEnvironmentVariableW(L"DKSIGN_DLL_SOUNDS", flag, 8);
    if (!(n > 0 && n < 8 && flag[0] == L'1'))
        return;
    wchar_t mod[MAX_PATH];
    if (!GetEnvironmentVariableW(L"DKSIGN_MOD_DIR", mod, MAX_PATH)) {
        dks_log("sound: DKSIGN_MOD_DIR not set, DLL sounds off");
        return;
    }
    _snwprintf(g_dir, MAX_PATH, L"%ls\\darksign_sounds", mod);
    g_dir[MAX_PATH - 1] = 0;
    if (GetFileAttributesW(g_dir) == INVALID_FILE_ATTRIBUTES) {
        dks_log("sound: %ls is missing (rebuild the content with sounds method D)", g_dir);
        return;
    }
    if (!game_is_2720()) {
        dks_log("sound: the TAE hook is only known for Elden Ring 2.7.2.0; DLL sounds off");
        return;
    }
    auto **vtable = (void **)((uintptr_t)GetModuleHandleW(nullptr) + TAE_EVENT_VTABLE_RVA);
    if (!in_game_code((uintptr_t)vtable[0]) || !in_game_code((uintptr_t)vtable[1]) || !in_game_code((uintptr_t)vtable[2])) {
        dks_log("sound: the TAE event vtable doesn't look right; DLL sounds off");
        return;
    }
    // Every WAV is read now, on the DLL's own thread, so the game thread never waits on the disk.
    WIN32_FIND_DATAW found;
    wchar_t pattern[MAX_PATH];
    _snwprintf(pattern, MAX_PATH, L"%ls\\*.wav", g_dir);
    pattern[MAX_PATH - 1] = 0;
    HANDLE find = FindFirstFileW(pattern, &found);
    if (find != INVALID_HANDLE_VALUE) {
        do {
            wchar_t letter = 0;
            int id = 0;
            if (swscanf(found.cFileName, L"%c%d.wav", &letter, &id) == 2 && (letter == L'c' || letter == L's'))
                if (Wave *wave = load_wave(letter == L's' ? 5 : 1, id))
                    g_waves[key(letter == L's' ? 5 : 1, id)] = wave;
        } while (FindNextFileW(find, &found));
        FindClose(find);
    }
    dks_log("sound: %zu WAV files loaded", g_waves.size());
    if (g_waves.empty() || !start_audio())
        return;
    if (MH_CreateHook(vtable[2], (void *)h_exec_one, (void **)&o_exec_one) != MH_OK || MH_EnableHook(vtable[2]) != MH_OK) {
        dks_log("sound: cannot hook TAE event execution");
        return;
    }
    dks_log("sound: playing ported sounds from %ls", g_dir);
}
