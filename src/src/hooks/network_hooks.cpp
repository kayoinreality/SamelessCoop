// Network Hooks - Winsock interception + Server redirect
//
// Two functions:
// 1. Hook Winsock connect() to redirect game connections from FromSoft → custom server
// 2. Patch hostname + RSA key in game memory so the game resolves to our server

// WinSock2 MUST be included before Windows.h
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <WinSock2.h>
#include <WS2tcpip.h>
#include <Windows.h>

#pragma comment(lib, "ws2_32.lib")

#include <Psapi.h>
#pragma comment(lib, "psapi.lib")

#include "../../include/hooks.h"
#include "../../include/addresses.h"
#include "../../include/utils.h"
#include <fstream>
#include <string>
#include <vector>
#include <algorithm>
#include <cstring>

using namespace DS2Coop::Hooks;
using namespace DS2Coop::Utils;
using namespace DS2Coop::Addresses;

// ============================================================================
// Winsock redirect state
// ============================================================================
static int (WSAAPI* g_originalConnect)(SOCKET s, const sockaddr* name, int namelen) = nullptr;
static bool g_gameOnline = false;
static bool g_redirectActive = false;
static std::string g_redirectIP = "127.0.0.1";
static uint16_t g_redirectPort = 50031;

// ============================================================================
// Boot-service HTTP emulation state
//
// At startup DS2 probes a Bandai "is the service up?" endpoint over plain HTTP
// (port 80) BEFORE it contacts the login server on 50031. With the retail
// endpoint dead, that probe fails and the game shows the "DARK SOULS II service
// is not available" popup — whose only outcomes drop the game into OFFLINE mode,
// which disables the summon-sign system the co-op depends on. We answer the
// probe ourselves: a tiny local HTTP responder returns "200 OK", and ConnectHook
// redirects the boot-window port-80 connection to it. The game then proceeds
// online, where the 50031 redirect routes matchmaking to the private server.
// ============================================================================
static volatile DWORD    g_installTick = 0;       // GetTickCount() when hooks installed
static volatile bool     g_loginSeen   = false;   // real 50031 redirect observed -> boot done
static volatile SOCKET   g_bootListenSock = INVALID_SOCKET;
static HANDLE            g_bootThread  = nullptr;  // responder thread (joined on shutdown)
static volatile uint16_t g_bootHttpPort = 0;      // ephemeral port of our local responder
static const DWORD       BOOT_WINDOW_MS = 90000;   // only emulate within 90s of boot

// Minimal local HTTP responder: accept -> (drain request) -> "200 OK" -> close.
static DWORD WINAPI BootHttpResponderThread(LPVOID) {
    int failures = 0;
    for (;;) {
        SOCKET listenSock = g_bootListenSock;
        if (listenSock == INVALID_SOCKET) break; // shutting down
        SOCKET client = accept(listenSock, nullptr, nullptr);
        if (client == INVALID_SOCKET) {
            if (g_bootListenSock == INVALID_SOCKET) break;
            if (++failures > 50) { LOG_WARNING("[BOOT] responder stopping after repeated accept() failures"); break; }
            Sleep(50);
            continue;
        }
        failures = 0;
        __try {
            char reqbuf[4096];
            int n = recv(client, reqbuf, sizeof(reqbuf) - 1, 0);
            if (n > 0) {
                reqbuf[n] = '\0';
                // Log the FULL probe request (one line) so the exact endpoint
                // path + Host are visible — that's the key to knowing what a real
                // success body should look like if an empty 200 is ever rejected.
                for (int i = 0; i < n; ++i) {
                    unsigned char c = static_cast<unsigned char>(reqbuf[i]);
                    if (c == '\r') reqbuf[i] = ' ';
                    else if (c == '\n') reqbuf[i] = '|';
                    else if (c < 0x20 || c > 0x7e) reqbuf[i] = '.';
                }
                LOG_INFO("[BOOT] Service probe request: %s", reqbuf);
            }
            static const char* resp =
                "HTTP/1.1 200 OK\r\n"
                "Content-Type: text/plain\r\n"
                "Content-Length: 0\r\n"
                "Connection: close\r\n"
                "\r\n";
            send(client, resp, static_cast<int>(strlen(resp)), 0);
            shutdown(client, SD_SEND);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        closesocket(client);
    }
    return 0;
}

// Bring up the local responder on 127.0.0.1:<ephemeral>; record the port so
// ConnectHook can redirect the boot probe to it. Failure is non-fatal (we just
// don't emulate — same as before).
static void StartBootHttpResponder() {
    // Winsock is already initialized by the game by the time hooks install, so
    // socket() succeeds without our own WSAStartup (avoids an unbalanced refcount).
    SOCKET ls = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (ls == INVALID_SOCKET) {
        LOG_WARNING("[BOOT] responder socket() failed (%d)", WSAGetLastError());
        return;
    }

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);
    addr.sin_port = 0; // ephemeral

    if (bind(ls, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        LOG_WARNING("[BOOT] responder bind() failed (%d)", WSAGetLastError());
        closesocket(ls);
        return;
    }
    int len = sizeof(addr);
    if (getsockname(ls, reinterpret_cast<sockaddr*>(&addr), &len) == SOCKET_ERROR ||
        listen(ls, 8) == SOCKET_ERROR) {
        LOG_WARNING("[BOOT] responder getsockname/listen failed (%d)", WSAGetLastError());
        closesocket(ls);
        return;
    }

    g_bootListenSock = ls;
    g_bootHttpPort = ntohs(addr.sin_port);
    LOG_INFO("[BOOT] HTTP responder up on 127.0.0.1:%u (answers DS2 service probe)", g_bootHttpPort);

    // Keep the thread handle so UninstallHooks can join it on shutdown.
    g_bootThread = CreateThread(nullptr, 0, BootHttpResponderThread, nullptr, 0, nullptr);
}

// ============================================================================
// Hooked Winsock connect() — redirects FromSoft server to custom server
// ============================================================================
static int WSAAPI ConnectHook(SOCKET s, const sockaddr* name, int namelen) {
    // Defensive: never jump through a null trampoline (shouldn't happen — MinHook
    // sets the trampoline before enabling the hook — but this runs in the game's
    // network path, so fail the connect rather than risk a null call).
    if (!g_originalConnect) { WSASetLastError(WSAENETDOWN); return SOCKET_ERROR; }

    if (name && name->sa_family == AF_INET) {
        sockaddr_in* addr = const_cast<sockaddr_in*>(reinterpret_cast<const sockaddr_in*>(name));
        uint16_t port = ntohs(addr->sin_port);

        char ipStr[INET_ADDRSTRLEN];
        inet_ntop(AF_INET, &addr->sin_addr, ipStr, sizeof(ipStr));

        LOG_INFO("[NET] Game connecting to %s:%u", ipStr, port);

        // Boot-service probe (plain HTTP, port 80): answer it locally so DS2
        // does not fall into offline mode. Only inside the boot window, only
        // until the real login redirect (50031) is seen, and only if our local
        // responder came up.
        if (g_redirectActive && port == 80 && !g_loginSeen && g_bootHttpPort != 0 &&
            (GetTickCount() - g_installTick) < BOOT_WINDOW_MS) {
            LOG_INFO("[BOOT] Redirecting service probe %s:80 -> 127.0.0.1:%u (local 200 OK)",
                     ipStr, g_bootHttpPort);
            inet_pton(AF_INET, "127.0.0.1", &addr->sin_addr);
            addr->sin_port = htons(g_bootHttpPort);
            return g_originalConnect(s, name, namelen);
        }

        // Redirect all game server connections (login=50031, auth=50000, game=50010+)
        if (g_redirectActive && (port == DS2_LOGIN_PORT || port == 50000 ||
            (port >= 50010 && port <= 50100))) {
            LOG_INFO("[NET] REDIRECTING %s:%u to custom server %s:%u (keeping port)",
                     ipStr, port, g_redirectIP.c_str(), port);

            // Rewrite destination IP only — keep the port the same
            // The server listens on all these ports locally
            inet_pton(AF_INET, g_redirectIP.c_str(), &addr->sin_addr);

            char newIp[INET_ADDRSTRLEN];
            inet_ntop(AF_INET, &addr->sin_addr, newIp, sizeof(newIp));
            LOG_INFO("[NET] Connection redirected to %s:%u", newIp, port);

            g_gameOnline = true;
            g_loginSeen = true;   // boot done — stop touching port 80
        } else if (port == DS2_LOGIN_PORT) {
            LOG_INFO("[NET] Detected DS2 login server connection (port %u) — redirect OFF", DS2_LOGIN_PORT);
            g_gameOnline = true;
        }
    }

    return g_originalConnect(s, name, namelen);
}

// ============================================================================
// WinsockHooks public interface
// ============================================================================
void WinsockHooks::SetServerRedirect(const std::string& ip, uint16_t port) {
    g_redirectIP = ip;
    g_redirectPort = port;
    g_redirectActive = true;
    LOG_INFO("[NET] Server redirect configured: %s:%u", ip.c_str(), port);
}

bool WinsockHooks::IsRedirectActive() {
    return g_redirectActive;
}

bool WinsockHooks::InstallHooks() {
    LOG_INFO("Installing Winsock hooks...");

    // Start the boot window and bring up the local service-probe responder so
    // the game's port-80 "service available?" check succeeds instead of forcing
    // offline mode.
    g_installTick = GetTickCount();
    StartBootHttpResponder();

    HMODULE ws2 = GetModuleHandleA("ws2_32.dll");
    if (!ws2) {
        ws2 = LoadLibraryA("ws2_32.dll");
    }

    if (!ws2) {
        LOG_WARNING("ws2_32.dll not loaded yet");
        return true;
    }

    void* connectAddr = GetProcAddress(ws2, "connect");
    if (!connectAddr) {
        LOG_WARNING("Could not find connect() in ws2_32.dll");
        return true;
    }

    if (HookManager::GetInstance().InstallHook(
        connectAddr,
        reinterpret_cast<void*>(&ConnectHook),
        reinterpret_cast<void**>(&g_originalConnect)
    )) {
        LOG_INFO("  HOOKED Winsock connect()");
        return true;
    }

    LOG_WARNING("  Failed to hook connect() (non-critical)");
    return true;
}

void WinsockHooks::UninstallHooks() {
    LOG_INFO("Uninstalling Winsock hooks...");
    // Tear down the boot-HTTP responder; closing the listen socket unblocks its
    // accept(), then join the thread so it is fully gone before we return (no
    // use-after-close / handle-recycle race inside the game process).
    SOCKET ls = g_bootListenSock;
    g_bootListenSock = INVALID_SOCKET;
    if (ls != INVALID_SOCKET) closesocket(ls);
    if (g_bootThread) {
        WaitForSingleObject(g_bootThread, 2000);
        CloseHandle(g_bootThread);
        g_bootThread = nullptr;
    }
}

// ============================================================================
// Server Redirect — Hostname + RSA key patching in game memory
//
// Adapted from ds3os DS2_ReplaceServerAddressHook.cpp
// DS2's hostname is NOT encrypted (unlike DS3), so we can patch directly.
// ============================================================================

// Search game memory for a wide string
static std::vector<uintptr_t> SearchWideString(const wchar_t* needle) {
    std::vector<uintptr_t> results;

    HMODULE gameModule = GetModuleHandleA("DarkSoulsII.exe");
    if (!gameModule) {
        LOG_ERROR("[REDIRECT] DarkSoulsII.exe module not found");
        return results;
    }

    MODULEINFO modInfo = {};
    GetModuleInformation(GetCurrentProcess(), gameModule, &modInfo, sizeof(modInfo));

    uintptr_t base = reinterpret_cast<uintptr_t>(modInfo.lpBaseOfDll);
    size_t moduleSize = modInfo.SizeOfImage;
    size_t needleLen = wcslen(needle);
    size_t needleBytes = needleLen * sizeof(wchar_t);

    if (needleBytes >= moduleSize) return results;

    for (size_t i = 0; i < moduleSize - needleBytes; i++) {
        if (memcmp(reinterpret_cast<void*>(base + i), needle, needleBytes) == 0) {
            results.push_back(base + i);
        }
    }

    return results;
}

// Search game memory for an ASCII string
static std::vector<uintptr_t> SearchAsciiString(const char* needle) {
    std::vector<uintptr_t> results;

    HMODULE gameModule = GetModuleHandleA("DarkSoulsII.exe");
    if (!gameModule) {
        LOG_ERROR("[REDIRECT] DarkSoulsII.exe module not found");
        return results;
    }

    MODULEINFO modInfo = {};
    GetModuleInformation(GetCurrentProcess(), gameModule, &modInfo, sizeof(modInfo));

    uintptr_t base = reinterpret_cast<uintptr_t>(modInfo.lpBaseOfDll);
    size_t moduleSize = modInfo.SizeOfImage;
    size_t needleLen = strlen(needle);

    if (needleLen >= moduleSize) return results;

    for (size_t i = 0; i < moduleSize - needleLen; i++) {
        if (memcmp(reinterpret_cast<void*>(base + i), needle, needleLen) == 0) {
            results.push_back(base + i);
        }
    }

    return results;
}

// Build a byte-swapped copy of a wide string for searching
static std::vector<wchar_t> MakeSwappedWideString(const wchar_t* src) {
    size_t len = wcslen(src);
    std::vector<wchar_t> swapped(len + 1);
    for (size_t i = 0; i <= len; i++) {
        wchar_t c = src[i];
        char* p = reinterpret_cast<char*>(&c);
        std::swap(p[0], p[1]);
        swapped[i] = c;
    }
    return swapped;
}

bool ServerRedirect::PatchHostname(const std::string& newHostname) {
    LOG_INFO("[REDIRECT] Patching server hostname to: %s", newHostname.c_str());

    const wchar_t* originalHostname = DS2_SERVER_HOSTNAME;

    // DS2 may store the hostname in normal byte order OR byte-swapped.
    // ds3os flips endian, so we search for both.
    auto swappedHostname = MakeSwappedWideString(originalHostname);

    int attempts = 0;
    const int maxAttempts = 60; // 30 seconds max wait

    while (attempts < maxAttempts) {
        // Search for normal byte order first
        auto matches = SearchWideString(originalHostname);
        // Also search for byte-swapped version
        auto swappedMatches = SearchWideString(swappedHostname.data());
        // Merge both result sets
        for (auto addr : swappedMatches) {
            matches.push_back(addr);
        }

        bool patched = false;
        for (uintptr_t addr : matches) {
            // Force memory writable
            DWORD oldProtect = 0;
            size_t hostnameBytes = (wcslen(originalHostname) + 1) * sizeof(wchar_t);
            if (!VirtualProtect(reinterpret_cast<void*>(addr),
                hostnameBytes, PAGE_READWRITE, &oldProtect)) {
                LOG_WARNING("[REDIRECT] VirtualProtect failed for hostname at 0x%p", reinterpret_cast<void*>(addr));
                continue;
            }

            // Convert hostname to wide string and write it
            // ds3os flips endian because FromSoft stores wchars byte-swapped.
            // Our SearchWideString uses memcmp against normal wchar_t, so if the
            // search matched, the memory is in normal byte order — check by reading
            // the first char to see if it's byte-swapped or not.
            std::wstring wideHostname(newHostname.begin(), newHostname.end());

            wchar_t* ptr = reinterpret_cast<wchar_t*>(addr);
            bool isSwapped = false;
            {
                // Check if 'f' (0x0066) is stored as 0x6600 (swapped)
                uint8_t* raw = reinterpret_cast<uint8_t*>(ptr);
                if (raw[0] == 0x66 && raw[1] == 0x00) {
                    isSwapped = false; // Normal LE order
                } else if (raw[0] == 0x00 && raw[1] == 0x66) {
                    isSwapped = true;  // Byte-swapped
                }
            }

            for (size_t i = 0; i < wideHostname.size() + 1; i++) {
                wchar_t chr = (i < wideHostname.size()) ? wideHostname[i] : L'\0';

                if (isSwapped) {
                    char* source = reinterpret_cast<char*>(&chr);
                    std::swap(source[0], source[1]);
                }

                memcpy(ptr, &chr, sizeof(wchar_t));
                ptr++;
            }

            // Restore protection
            VirtualProtect(reinterpret_cast<void*>(addr),
                hostnameBytes, oldProtect, &oldProtect);

            LOG_INFO("[REDIRECT] Patched hostname at 0x%p", reinterpret_cast<void*>(addr));
            patched = true;
        }

        if (patched) {
            LOG_INFO("[REDIRECT] Hostname patching complete");
            return true;
        }

        attempts++;
        Sleep(500);
    }

    LOG_ERROR("[REDIRECT] Failed to find hostname in game memory after %d attempts", maxAttempts);
    return false;
}

bool ServerRedirect::PatchRSAKey(const std::string& newPublicKey) {
    LOG_INFO("[REDIRECT] Patching RSA public key...");

    // FromSoft's original RSA public key (hardcoded in DS2 binary)
    const char* originalKey =
        "-----BEGIN RSA PUBLIC KEY-----\n"
        "MIIBCAKCAQEAxSeDuBTm3AytrIOGjDKpwJY+437i1F8leMBASVkknYdzM5HB4z8X\n"
        "YTXDylr/N6XAhgr/LcFFZ68yQNQ4AquriMONB+TWUiX0xu84ixYH3AqRtIVqLQbQ\n"
        "xKZsTfyCRC94n9EnvPeS+ueM495YhLIJQBf9T2aCeoHZBFDh2CghJQCdyd4dOT/E\n"
        "9ZxPImwj1t2fZkkKo4smpGk7GcCask2SGsnk/P2jUJxsOyFlCojaW1IldPxn+lXH\n"
        "dlgHSLjQvMlWiZ2SmOwvJqPWMv6XyUXYqsOdejRJJQjV7jeDzYG8trX+bSQxnTAw\n"
        "ENjvjslEcjBmzOCiqFTA/9H1jMjReZpI/wIBAw==\n"
        "-----END RSA PUBLIC KEY-----\n";

    int attempts = 0;
    const int maxAttempts = 60;

    while (attempts < maxAttempts) {
        auto matches = SearchAsciiString(originalKey);

        bool patched = false;
        for (uintptr_t addr : matches) {
            // Copy new key over old key (ds3os just does a straight memcpy)
            size_t copyLen = newPublicKey.size() + 1;
            size_t originalLen = strlen(originalKey) + 1;
            size_t patchLen = copyLen > originalLen ? copyLen : originalLen;

            // Force memory writable — always VirtualProtect regardless of current state
            DWORD oldProtect = 0;
            if (!VirtualProtect(reinterpret_cast<void*>(addr),
                patchLen, PAGE_READWRITE, &oldProtect)) {
                LOG_WARNING("[REDIRECT] VirtualProtect failed at 0x%p (error %u)",
                    reinterpret_cast<void*>(addr), GetLastError());
                continue;
            }

            memcpy(reinterpret_cast<void*>(addr), newPublicKey.c_str(), copyLen);

            // Zero-fill remaining bytes if new key is shorter
            if (copyLen < originalLen) {
                memset(reinterpret_cast<void*>(addr + copyLen), 0, originalLen - copyLen);
            }

            VirtualProtect(reinterpret_cast<void*>(addr),
                patchLen, oldProtect, &oldProtect);

            LOG_INFO("[REDIRECT] Patched RSA key at 0x%p", reinterpret_cast<void*>(addr));
            patched = true;
        }

        if (patched) {
            LOG_INFO("[REDIRECT] RSA key patching complete");
            return true;
        }

        attempts++;
        Sleep(500);
    }

    LOG_ERROR("[REDIRECT] Failed to find RSA key in game memory after %d attempts", maxAttempts);
    return false;
}

bool ServerRedirect::Install(const std::string& serverIp, const std::string& publicKeyPath) {
    LOG_INFO("[REDIRECT] Installing server redirect to %s", serverIp.c_str());

    // Read the public key from file
    std::string publicKey;
    std::ifstream keyFile(publicKeyPath);
    if (keyFile.is_open()) {
        publicKey.assign(std::istreambuf_iterator<char>(keyFile),
                         std::istreambuf_iterator<char>());
        keyFile.close();
        LOG_INFO("[REDIRECT] Loaded public key from %s (%zu bytes)", publicKeyPath.c_str(), publicKey.size());
    } else {
        LOG_ERROR("[REDIRECT] Could not open public key file: %s", publicKeyPath.c_str());
        return false;
    }

    // Patch hostname — the game will resolve this IP instead of FromSoft's server
    if (!PatchHostname(serverIp)) {
        LOG_ERROR("[REDIRECT] Hostname patching failed");
        return false;
    }

    // Patch RSA key — the game will use our server's key for encryption
    if (!PatchRSAKey(publicKey)) {
        LOG_ERROR("[REDIRECT] RSA key patching failed");
        return false;
    }

    LOG_INFO("[REDIRECT] Server redirect installed successfully");
    return true;
}
