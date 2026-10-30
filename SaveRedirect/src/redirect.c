#include "redirect.h"

#include <wctype.h>

#define VANILLA_EXT L".sl2"

static int is_sep(wchar_t c) { return c == L'\\' || c == L'/'; }

/* Case-insensitive prefix match that treats / and \ alike. Returns the matched length or 0. */
static size_t match_dir(const wchar_t *path, const wchar_t *dir)
{
    size_t i = 0;
    for (; dir[i]; i++) {
        wchar_t a = path[i], b = dir[i];
        if (!a)
            return 0;
        if (is_sep(a) && is_sep(b))
            continue;
        if (towlower(a) != towlower(b))
            return 0;
    }
    while (i > 0 && is_sep(dir[i - 1]))
        i--; /* a configured dir may end in a separator */
    return (path[i] == 0 || is_sep(path[i])) ? i : 0;
}

static const wchar_t *skip_long_prefix(const wchar_t *p)
{
    /* \\?\C:\... and \??\C:\... name the same file as C:\... */
    if ((p[0] == L'\\' && p[1] == L'\\' && p[2] == L'?' && p[3] == L'\\') ||
        (p[0] == L'\\' && p[1] == L'?' && p[2] == L'?' && p[3] == L'\\'))
        return p + 4;
    return p;
}

static int ends_with_ci(const wchar_t *s, size_t len, const wchar_t *suffix)
{
    size_t n = wcslen(suffix);
    if (len < n)
        return 0;
    for (size_t i = 0; i < n; i++)
        if (towlower(s[len - n + i]) != towlower(suffix[i]))
            return 0;
    return 1;
}

/* Replaces `from` with `to` where it ends the file name or comes right before a further
   extension (".sl2" and ".sl2.bak" both count). Works on the last path component only. */
static int swap_ext(wchar_t *path, size_t cap, const wchar_t *from, const wchar_t *to)
{
    size_t len = wcslen(path), from_len = wcslen(from), to_len = wcslen(to);
    size_t name = len;
    while (name > 0 && !is_sep(path[name - 1]))
        name--;

    for (size_t i = name; i + from_len <= len; i++) {
        if (!ends_with_ci(path + i, from_len, from))
            continue;
        wchar_t next = path[i + from_len];
        if (next != 0 && next != L'.')
            continue;
        if (len - from_len + to_len + 1 > cap)
            return 0;
        wmemmove(path + i + to_len, path + i + from_len, len - i - from_len + 1);
        wmemcpy(path + i, to, to_len);
        return 1;
    }
    return 1;
}

int dks_redirect(const dks_config *cfg, const wchar_t *in, wchar_t *out, size_t out_len)
{
    if (!cfg || !in || !cfg->vanilla_dir[0] || !cfg->save_dir[0])
        return 0;

    const wchar_t *p = skip_long_prefix(in);
    size_t matched = match_dir(p, cfg->vanilla_dir);
    if (!matched)
        return 0;

    const wchar_t *rest = p + matched; /* "" or "\76561198...\DS30000.sl2" */
    size_t dir_len = wcslen(cfg->save_dir);
    while (dir_len > 0 && is_sep(cfg->save_dir[dir_len - 1]))
        dir_len--;
    if (dir_len + wcslen(rest) + 1 > out_len)
        return 0;

    wmemcpy(out, cfg->save_dir, dir_len);
    wcscpy(out + dir_len, rest);
    for (wchar_t *c = out; *c; c++)
        if (*c == L'/')
            *c = L'\\';

    if (cfg->save_ext[0] && !swap_ext(out, out_len, VANILLA_EXT, cfg->save_ext))
        return 0;
    return 1;
}

void dks_restore_name(const dks_config *cfg, wchar_t *name, size_t name_len)
{
    if (cfg && cfg->save_ext[0])
        swap_ext(name, name_len, cfg->save_ext, VANILLA_EXT);
}

const wchar_t *dks_game_from_exe(const wchar_t *exe_path)
{
    static const struct { const wchar_t *exe, *game; } games[] = {
        { L"darksoulsremastered.exe", L"DSR" },
        { L"darksoulsiii.exe", L"DS3" },
        { L"eldenring.exe", L"ER" },
    };
    size_t len = wcslen(exe_path);
    for (size_t i = 0; i < sizeof games / sizeof games[0]; i++) {
        size_t n = wcslen(games[i].exe);
        if (ends_with_ci(exe_path, len, games[i].exe) && (len == n || is_sep(exe_path[len - n - 1])))
            return games[i].game;
    }
    return NULL;
}
