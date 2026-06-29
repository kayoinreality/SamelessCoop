#include "../../include/ui.h"
#include "../../include/utils.h"
#include "../../include/session.h"
#include "../../include/network.h"
#include <Windows.h>
#include <thread>
#include <string>

using namespace DS2Coop::Utils;

namespace DS2Coop::UI {

TitleScreenNotifier& TitleScreenNotifier::GetInstance() {
    static TitleScreenNotifier instance;
    return instance;
}

TitleScreenNotifier::~TitleScreenNotifier() {
    Stop();
}

void TitleScreenNotifier::Start() {
    if (m_running) return;
    
    m_running = true;
    m_thread = std::thread(&TitleScreenNotifier::UpdateThread, this);
}

void TitleScreenNotifier::Stop() {
    m_running = false;
    if (m_thread.joinable()) {
        m_thread.join();
    }

    // Restore original title
    HWND hwnd = FindGameWindow();
    if (hwnd) {
        SetWindowTextW(hwnd, L"DARK SOULS II");
    }
}

// Helper to find window by process name
static BOOL CALLBACK EnumWindowsProc(HWND hwnd, LPARAM lParam) {
    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);

    if (processId != 0) {
        HANDLE hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (hProcess) {
            char processName[MAX_PATH];
            DWORD size = MAX_PATH;
            if (QueryFullProcessImageNameA(hProcess, 0, processName, &size)) {
                // Check if this is DarkSoulsII.exe
                if (strstr(processName, "DarkSoulsII.exe") != nullptr) {
                    // Found it! Check if it's a main window
                    if (IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0) {
                        *(HWND*)lParam = hwnd;
                        CloseHandle(hProcess);
                        return FALSE; // Stop enumeration
                    }
                }
            }
            CloseHandle(hProcess);
        }
    }
    return TRUE; // Continue enumeration
}

HWND TitleScreenNotifier::FindGameWindow() {
    // First try the simple approach (works for windowed mode)
    HWND hwnd = FindWindowW(nullptr, L"DARK SOULS II");
    if (!hwnd) {
        hwnd = FindWindowW(nullptr, L"DARK SOULS II: Scholar of the First Sin");
    }

    // If that failed, enumerate all windows and find by process name (works for fullscreen)
    if (!hwnd) {
        EnumWindows(EnumWindowsProc, (LPARAM)&hwnd);
    }

    return hwnd;
}

// Build the live status string shown in the game's title bar. Since the
// in-game overlay is disabled, this is the player's main co-op status readout.
static std::wstring BuildStatusTitle() {
    const std::wstring base = L"DARK SOULS II - SEAMLESS CO-OP";
    auto& sm = DS2Coop::Session::SessionManager::GetInstance();
    if (sm.IsActive()) {
        size_t players = DS2Coop::Network::PeerManager::GetInstance().GetPeerCount() + 1;
        std::wstring role = sm.IsHost() ? L"Host" : L"Cliente";
        std::wstring plural = (players == 1) ? L" jogador" : L" jogadores";
        return base + L"  -  " + role + L"  -  "
             + std::to_wstring(players) + plural;
    }
    if (sm.IsHost())
        return base + L"  -  Host (aguardando jogadores)";
    return base + L"  -  conectando...";
}

void TitleScreenNotifier::UpdateThread() {
    LOG_INFO("Title screen notifier thread started");

    int attempts = 0;
    bool firstUpdate = true;

    while (m_running) {
        HWND hwnd = FindGameWindow();
        if (hwnd) {
            // Live co-op status in the title bar (the in-game overlay is off).
            std::wstring title = BuildStatusTitle();

            if (SetWindowTextW(hwnd, title.c_str())) {
                if (firstUpdate) {
                    LOG_INFO("Window title now shows live co-op status (no in-game menu).");
                    firstUpdate = false;
                }
                attempts++;
            } else {
                LOG_ERROR("Failed to set window title! Error: %lu", GetLastError());
            }
        } else {
            if (firstUpdate) {
                LOG_WARNING("Waiting for game window...");
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }

    LOG_INFO("Title screen notifier stopped (updated %d times)", attempts);
}

} // namespace DS2Coop::UI

