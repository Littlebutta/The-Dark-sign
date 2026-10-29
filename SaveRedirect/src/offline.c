#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "MinHook.h"
#include "offline.h"

static dks_log_fn g_log;
static volatile LONG g_blocked_count;

typedef int (WSAAPI *connect_t)(SOCKET, const struct sockaddr *, int);
typedef int (WSAAPI *WSAConnect_t)(SOCKET, const struct sockaddr *, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
typedef int (WSAAPI *sendto_t)(SOCKET, const char *, int, int, const struct sockaddr *, int);
typedef int (WSAAPI *WSASendTo_t)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr *, int,
                                  LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI *getaddrinfo_t)(PCSTR, PCSTR, const ADDRINFOA *, PADDRINFOA *);
typedef int (WSAAPI *GetAddrInfoW_t)(PCWSTR, PCWSTR, const ADDRINFOW *, PADDRINFOW *);
typedef INT (WSAAPI *GetAddrInfoExW_t)(PCWSTR, PCWSTR, DWORD, LPGUID, const ADDRINFOEXW *, PADDRINFOEXW *,
                                       struct timeval *, LPOVERLAPPED, LPLOOKUPSERVICE_COMPLETION_ROUTINE, LPHANDLE);
typedef struct hostent *(WSAAPI *gethostbyname_t)(const char *);

static connect_t o_connect;
static WSAConnect_t o_WSAConnect;
static sendto_t o_sendto;
static WSASendTo_t o_WSASendTo;
static getaddrinfo_t o_getaddrinfo;
static GetAddrInfoW_t o_GetAddrInfoW;
static GetAddrInfoExW_t o_GetAddrInfoExW;
static gethostbyname_t o_gethostbyname;

static void note(const wchar_t *what, const wchar_t *target)
{
    /* The game retries constantly while offline; the first few are enough to see it work. */
    if (g_log && InterlockedIncrement(&g_blocked_count) <= 20)
        g_log(L"offline: blocked %ls %ls", what, target);
}

/* Loopback stays allowed: Steam and some runtimes talk to this PC over it. */
static int is_local(const struct sockaddr *addr, int len)
{
    if (!addr)
        return 1; /* a connected UDP socket's sendto: its connect was already checked */
    if (addr->sa_family == AF_INET && len >= (int)sizeof(struct sockaddr_in))
        return (ntohl(((const struct sockaddr_in *)addr)->sin_addr.s_addr) >> 24) == 127;
    if (addr->sa_family == AF_INET6 && len >= (int)sizeof(struct sockaddr_in6)) {
        const struct in6_addr *a = &((const struct sockaddr_in6 *)addr)->sin6_addr;
        static const unsigned char loopback[16] = { [15] = 1 };
        static const unsigned char mapped_prefix[12] = { [10] = 0xff, [11] = 0xff };
        return memcmp(a->s6_addr, loopback, 16) == 0 ||
               (memcmp(a->s6_addr, mapped_prefix, 12) == 0 && a->s6_addr[12] == 127);
    }
    return addr->sa_family == AF_UNIX;
}

static int is_local_name_w(const wchar_t *name)
{
    return !name || !*name || _wcsicmp(name, L"localhost") == 0 || wcsncmp(name, L"127.", 4) == 0 ||
           wcscmp(name, L"::1") == 0;
}

static int is_local_name(const char *name)
{
    return !name || !*name || _stricmp(name, "localhost") == 0 || strncmp(name, "127.", 4) == 0 ||
           strcmp(name, "::1") == 0;
}

static void describe(const struct sockaddr *addr, wchar_t *out, DWORD out_len)
{
    wcscpy(out, L"?");
    if (addr && (addr->sa_family == AF_INET || addr->sa_family == AF_INET6)) {
        int len = addr->sa_family == AF_INET ? sizeof(struct sockaddr_in) : sizeof(struct sockaddr_in6);
        WSAAddressToStringW((LPSOCKADDR)addr, len, NULL, out, &out_len);
    }
}

static int refuse(const wchar_t *what, const struct sockaddr *addr)
{
    wchar_t text[64];
    describe(addr, text, 64);
    note(what, text);
    WSASetLastError(WSAENETUNREACH);
    return SOCKET_ERROR;
}

static int WSAAPI h_connect(SOCKET s, const struct sockaddr *a, int l)
{
    return is_local(a, l) ? o_connect(s, a, l) : refuse(L"connect", a);
}

static int WSAAPI h_WSAConnect(SOCKET s, const struct sockaddr *a, int l, LPWSABUF c, LPWSABUF e, LPQOS q, LPQOS g)
{
    return is_local(a, l) ? o_WSAConnect(s, a, l, c, e, q, g) : refuse(L"connect", a);
}

static int WSAAPI h_sendto(SOCKET s, const char *b, int n, int f, const struct sockaddr *a, int l)
{
    return is_local(a, l) ? o_sendto(s, b, n, f, a, l) : refuse(L"send", a);
}

static int WSAAPI h_WSASendTo(SOCKET s, LPWSABUF b, DWORD n, LPDWORD sent, DWORD f, const struct sockaddr *a, int l,
                              LPWSAOVERLAPPED o, LPWSAOVERLAPPED_COMPLETION_ROUTINE r)
{
    return is_local(a, l) ? o_WSASendTo(s, b, n, sent, f, a, l, o, r) : refuse(L"send", a);
}

static int WSAAPI h_getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA *hints, PADDRINFOA *result)
{
    if (is_local_name(node))
        return o_getaddrinfo(node, service, hints, result);
    wchar_t wide[256];
    MultiByteToWideChar(CP_UTF8, 0, node, -1, wide, 256);
    wide[255] = 0;
    note(L"lookup", wide);
    *result = NULL;
    return WSAHOST_NOT_FOUND;
}

static int WSAAPI h_GetAddrInfoW(PCWSTR node, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *result)
{
    if (is_local_name_w(node))
        return o_GetAddrInfoW(node, service, hints, result);
    note(L"lookup", node);
    *result = NULL;
    return WSAHOST_NOT_FOUND;
}

static INT WSAAPI h_GetAddrInfoExW(PCWSTR node, PCWSTR service, DWORD ns, LPGUID id, const ADDRINFOEXW *hints,
                                   PADDRINFOEXW *result, struct timeval *timeout, LPOVERLAPPED ov,
                                   LPLOOKUPSERVICE_COMPLETION_ROUTINE done, LPHANDLE cancel)
{
    if (is_local_name_w(node))
        return o_GetAddrInfoExW(node, service, ns, id, hints, result, timeout, ov, done, cancel);
    note(L"lookup", node);
    *result = NULL;
    return WSAHOST_NOT_FOUND;
}

static struct hostent *WSAAPI h_gethostbyname(const char *name)
{
    if (is_local_name(name))
        return o_gethostbyname(name);
    wchar_t wide[256];
    MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, 256);
    wide[255] = 0;
    note(L"lookup", wide);
    WSASetLastError(WSAHOST_NOT_FOUND);
    return NULL;
}

static int hook(HMODULE ws2, const char *name, LPVOID detour, LPVOID *original)
{
    FARPROC target = GetProcAddress(ws2, name);
    if (!target)
        return 0;
    if (MH_CreateHook((LPVOID)target, detour, original) != MH_OK || MH_EnableHook((LPVOID)target) != MH_OK) {
        if (g_log)
            g_log(L"offline: hook %hs failed", name);
        return 0;
    }
    return 1;
}

int dks_block_online(dks_log_fn log)
{
    g_log = log;
    /* The three games import ws2_32 themselves, so it is already mapped. */
    HMODULE ws2 = GetModuleHandleW(L"ws2_32.dll");
    if (!ws2)
        ws2 = LoadLibraryW(L"ws2_32.dll");
    if (!ws2)
        return 0;

    int ok = 1;
    ok &= hook(ws2, "connect", h_connect, (LPVOID *)&o_connect);
    ok &= hook(ws2, "WSAConnect", h_WSAConnect, (LPVOID *)&o_WSAConnect);
    ok &= hook(ws2, "sendto", h_sendto, (LPVOID *)&o_sendto);
    ok &= hook(ws2, "WSASendTo", h_WSASendTo, (LPVOID *)&o_WSASendTo);
    ok &= hook(ws2, "getaddrinfo", h_getaddrinfo, (LPVOID *)&o_getaddrinfo);
    ok &= hook(ws2, "GetAddrInfoW", h_GetAddrInfoW, (LPVOID *)&o_GetAddrInfoW);
    ok &= hook(ws2, "GetAddrInfoExW", h_GetAddrInfoExW, (LPVOID *)&o_GetAddrInfoExW);
    ok &= hook(ws2, "gethostbyname", h_gethostbyname, (LPVOID *)&o_gethostbyname);
    if (log)
        log(ok ? L"offline: online connections blocked" : L"offline: some network hooks are missing");
    return ok;
}
