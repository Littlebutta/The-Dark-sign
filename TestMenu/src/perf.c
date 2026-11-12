/* Frame-time recorder (see perf.h). While it records it writes one log line per second:
     perf: 12 s at 200.3 -194.9 -714.1: 47 fps, average 21.3 ms, slowest 140.2 ms, 3 over 33 ms, 2 over 50 ms, 1 over 100 ms
   and, when stopped, a summary of the whole walk (median, 95th and 99th percentile, slowest frame, hitches per minute)
   with the ten slowest frames and where the player stood for each. The position is the F1 log's (spawn.c), so a
   hitch can be put on the map. A "hitch" is a frame over 50 ms (three frames' time at 60 fps).

   The time between two Present calls is what the player sees as one frame: CPU and GPU work, loading stalls and
   shader compiles all show in it. It doesn't say which of them it was; the test builds (launcher Fallbacks) do. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "game.h"
#include "perf.h"
#include "spawn.h"

#define MAX_FRAMES (60 * 60 * 120) /* two hours at 120 fps; frames past that only count in the per-second lines */
#define WORST 10

typedef struct {
    float ms;
    float pos[3];
    int have_pos;
    double at; /* seconds into the recording */
} Slow;

static volatile LONG g_toggle;
static int g_on;
static LARGE_INTEGER g_freq, g_last, g_start, g_second_start;
static float *g_frames;
static int g_count, g_total;
static Slow g_worst[WORST];
static int g_worst_count;
/* The current second. */
static int s_frames, s_over33, s_over50, s_over100;
static double s_sum, s_max;
static int g_seconds;
static char g_status[256] = "Not recording. Ctrl+Shift+F8 starts and stops it.";

void dks_perf_request_toggle(void) { InterlockedExchange(&g_toggle, 1); }

int dks_perf_on(void) { return g_on; }

const char *dks_perf_status(void) { return g_status; }

static double seconds(LARGE_INTEGER from, LARGE_INTEGER to) { return (double)(to.QuadPart - from.QuadPart) / (double)g_freq.QuadPart; }

static int compare_floats(const void *a, const void *b)
{
    float x = *(const float *)a, y = *(const float *)b;
    return (x > y) - (x < y);
}

static void reset_second(void)
{
    s_frames = s_over33 = s_over50 = s_over100 = 0;
    s_sum = s_max = 0;
}

static void start(LARGE_INTEGER now)
{
    if (!g_frames)
        g_frames = (float *)malloc(sizeof(float) * MAX_FRAMES);
    g_count = g_total = g_worst_count = g_seconds = 0;
    reset_second();
    g_start = g_second_start = g_last = now;
    g_on = 1;
    float pos[3];
    if (dks_player_position(pos))
        dks_log("perf: recording frame times from %.1f %.1f %.1f (Ctrl+Shift+F8 stops)", pos[0], pos[1], pos[2]);
    else
        dks_log("perf: recording frame times (no player loaded yet; Ctrl+Shift+F8 stops)");
    snprintf(g_status, sizeof g_status, "Recording... (Ctrl+Shift+F8 stops)");
}

static void stop(LARGE_INTEGER now)
{
    g_on = 0;
    double length = seconds(g_start, now);
    if (g_count == 0 || !g_frames) {
        dks_log("perf: stopped, no frames recorded");
        snprintf(g_status, sizeof g_status, "Stopped: no frames recorded.");
        return;
    }
    int over50 = 0, over100 = 0;
    double sum = 0;
    for (int i = 0; i < g_count; i++) {
        sum += g_frames[i];
        over50 += g_frames[i] > 50.0f;
        over100 += g_frames[i] > 100.0f;
    }
    qsort(g_frames, (size_t)g_count, sizeof(float), compare_floats);
    float median = g_frames[g_count / 2], p95 = g_frames[(int)(g_count * 0.95)], p99 = g_frames[(int)(g_count * 0.99)],
          slowest = g_frames[g_count - 1];
    double minutes = length / 60.0 > 0 ? length / 60.0 : 1.0 / 60;
    dks_log("perf summary: %.0f s, %d frames, %.1f fps on average; frame time median %.1f ms, 95%% under %.1f ms, 99%% under %.1f ms, slowest %.1f ms; "
            "hitches (over 50 ms) %d = %.1f per minute, over 100 ms %d",
            length, g_total, g_total / (length > 0 ? length : 1), median, p95, p99, slowest, over50, over50 / minutes, over100);
    for (int i = 0; i < g_worst_count; i++) {
        if (g_worst[i].have_pos)
            dks_log("perf summary: slow frame %d: %.1f ms at %.0f s, at %.1f %.1f %.1f", i + 1, g_worst[i].ms, g_worst[i].at,
                    g_worst[i].pos[0], g_worst[i].pos[1], g_worst[i].pos[2]);
        else
            dks_log("perf summary: slow frame %d: %.1f ms at %.0f s", i + 1, g_worst[i].ms, g_worst[i].at);
    }
    snprintf(g_status, sizeof g_status, "Last walk: %.0f s, %.1f fps, median %.1f ms, slowest %.0f ms, %.1f hitches per minute (in the log).",
             length, g_total / (length > 0 ? length : 1), median, slowest, over50 / minutes);
}

static void keep_if_slow(float ms, double at)
{
    int slot = -1;
    if (g_worst_count < WORST)
        slot = g_worst_count++;
    else {
        int least = 0;
        for (int i = 1; i < WORST; i++)
            if (g_worst[i].ms < g_worst[least].ms)
                least = i;
        if (ms > g_worst[least].ms)
            slot = least;
    }
    if (slot < 0)
        return;
    g_worst[slot].ms = ms;
    g_worst[slot].at = at;
    g_worst[slot].have_pos = dks_player_position(g_worst[slot].pos);
    /* Slowest first. */
    for (int i = g_worst_count - 1; i > 0 && g_worst[i].ms > g_worst[i - 1].ms; i--) {
        Slow t = g_worst[i];
        g_worst[i] = g_worst[i - 1];
        g_worst[i - 1] = t;
    }
}

void dks_perf_frame(void)
{
    LARGE_INTEGER now;
    if (!g_freq.QuadPart)
        QueryPerformanceFrequency(&g_freq);
    QueryPerformanceCounter(&now);
    if (InterlockedExchange(&g_toggle, 0)) {
        if (g_on)
            stop(now);
        else
            start(now);
        return;
    }
    if (!g_on)
        return;
    float ms = (float)(seconds(g_last, now) * 1000.0);
    g_last = now;
    if (g_count < MAX_FRAMES && g_frames)
        g_frames[g_count++] = ms;
    g_total++;
    s_frames++;
    s_sum += ms;
    if (ms > s_max)
        s_max = ms;
    s_over33 += ms > 33.4f;
    s_over50 += ms > 50.0f;
    s_over100 += ms > 100.0f;
    if (ms > 50.0f || g_worst_count < WORST)
        keep_if_slow(ms, seconds(g_start, now));
    double second = seconds(g_second_start, now);
    if (second >= 1.0) {
        g_seconds++;
        float pos[3];
        int have = dks_player_position(pos);
        char where[64] = "";
        if (have)
            snprintf(where, sizeof where, " at %.1f %.1f %.1f", pos[0], pos[1], pos[2]);
        dks_log("perf: %d s%s: %.0f fps, average %.1f ms, slowest %.1f ms, %d over 33 ms, %d over 50 ms, %d over 100 ms",
                g_seconds, where, s_frames / second, s_sum / s_frames, s_max, s_over33, s_over50, s_over100);
        snprintf(g_status, sizeof g_status, "Recording %d s: %.0f fps, slowest %.0f ms in the last second (Ctrl+Shift+F8 stops)",
                 g_seconds, s_frames / second, s_max);
        reset_second();
        g_second_start = now;
    }
}
