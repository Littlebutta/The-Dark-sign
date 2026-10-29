/* Native tests for the path logic: cc -I../src test_redirect.c ../src/redirect.c && ./a.out */
#include <stdio.h>
#include <string.h>

#include "redirect.h"

static int failures;

#define CHECK(cond) do { if (!(cond)) { failures++; printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #cond); } } while (0)

static dks_config config(void)
{
    dks_config c = { 0 };
    wcscpy(c.vanilla_dir, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII");
    wcscpy(c.save_dir, L"D:\\The Dark sign\\TheDarkSign\\Saves\\Default\\DS3\\");
    wcscpy(c.save_ext, L".dksign");
    return c;
}

int main(void)
{
    dks_config c = config();
    wchar_t out[DKS_MAX_PATH];

    CHECK(dks_redirect(&c, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII\\0110000100000001\\DS30000.sl2", out, DKS_MAX_PATH));
    CHECK(wcscmp(out, L"D:\\The Dark sign\\TheDarkSign\\Saves\\Default\\DS3\\0110000100000001\\DS30000.dksign") == 0);

    /* Case, slashes, the long-path prefix and the .bak copy. */
    CHECK(dks_redirect(&c, L"\\\\?\\c:/users/dev/appdata/roaming/darksoulsiii/x/DS30000.SL2.bak", out, DKS_MAX_PATH));
    CHECK(wcscmp(out, L"D:\\The Dark sign\\TheDarkSign\\Saves\\Default\\DS3\\x\\DS30000.dksign.bak") == 0);

    /* The folder itself, and a non-save file inside it keep their names. */
    CHECK(dks_redirect(&c, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII", out, DKS_MAX_PATH));
    CHECK(wcscmp(out, L"D:\\The Dark sign\\TheDarkSign\\Saves\\Default\\DS3") == 0);
    CHECK(dks_redirect(&c, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII\\x\\steam_autocloud.vdf", out, DKS_MAX_PATH));
    CHECK(wcscmp(out, L"D:\\The Dark sign\\TheDarkSign\\Saves\\Default\\DS3\\x\\steam_autocloud.vdf") == 0);

    /* Anything else is left alone, including a folder that only shares the prefix. */
    CHECK(!dks_redirect(&c, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII_backup\\DS30000.sl2", out, DKS_MAX_PATH));
    CHECK(!dks_redirect(&c, L"D:\\The Dark sign\\DARK SOULS III\\Game\\Data0.bdt", out, DKS_MAX_PATH));
    CHECK(!dks_redirect(&c, L"DS30000.sl2", out, DKS_MAX_PATH));

    /* An output buffer that is too small is refused rather than truncated. */
    CHECK(!dks_redirect(&c, L"C:\\Users\\Dev\\AppData\\Roaming\\DarkSoulsIII\\x\\DS30000.sl2", out, 20));

    wchar_t name[64] = L"ER0000.dksign.bak";
    dks_restore_name(&c, name, 64);
    CHECK(wcscmp(name, L"ER0000.sl2.bak") == 0);

    CHECK(wcscmp(dks_game_from_exe(L"D:\\The Dark sign\\ELDEN RING\\Game\\eldenring.exe"), L"ER") == 0);
    CHECK(wcscmp(dks_game_from_exe(L"D:\\x\\DarkSoulsIII.exe"), L"DS3") == 0);
    CHECK(dks_game_from_exe(L"D:\\x\\noteldenring.exe") == NULL);
    CHECK(dks_game_from_exe(L"D:\\x\\ds3sc_launcher.exe") == NULL);

    printf(failures ? "%d failed\n" : "all passed\n", failures);
    return failures != 0;
}
