// MaimaiSteam.exe -- Steam entry point / supervisor for maimai DX (SDEZ)
//
// Build: tools/steam-launcher/build.ps1 (MSVC) or build_wsl.sh (zig cross-compile)
// Install: put this exe + start-steam.bat next to Sinmai.exe, then point a Steam
//          non-Steam shortcut at the exe. See tools/steam-launcher/README.md.
//
// Why this exists: Steam's "add a non-Steam game" can only point at one exe and
// the game has to be started through a .bat (it injects mai2hook.dll into
// amdaemon.exe and sets OPENSSL_ia32cap). Pointing Steam straight at the .bat
// leaves a console window on screen for the whole session.
//
// What this does:
//   1. runs start-steam.bat with a HIDDEN console (the bat injects + verifies
//      amdaemon and retries internally),
//   2. waits until Sinmai.exe shows up,
//   3. keeps waiting while the game runs, so Steam shows "playing" correctly,
//   4. if the game dies within 25s (a failed injection shows up exactly like
//      that: black screen / quick exit), it cleans up and retries the whole
//      chain -- up to 3 rounds,
//   5. waits ~10s after the game exits so start.bat can run its
//      "taskkill amdaemon.exe" cleanup, then quits.
//
// Logs go to maimaiDX.log next to this exe (ASCII only).

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <tlhelp32.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static fs::path g_dir;

static void Log(const std::string& msg)
{
    std::ofstream f(g_dir / L"maimaiDX.log", std::ios::app);
    if (!f.is_open()) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    char ts[32];
    wsprintfA(ts, "%02d/%02d %02d:%02d:%02d", st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    f << ts << "  " << msg << std::endl;
}

static DWORD FindProcessId(const std::wstring& name)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W e{};
    e.dwSize = sizeof(e);
    DWORD pid = 0;
    if (Process32FirstW(snap, &e)) {
        do {
            if (_wcsicmp(e.szExeFile, name.c_str()) == 0) { pid = e.th32ProcessID; break; }
        } while (Process32NextW(snap, &e));
    }
    CloseHandle(snap);
    return pid;
}

static void KillProcess(const std::wstring& name)
{
    std::wstring cmd = L"cmd.exe /c taskkill /f /im " + name + L" >nul 2>&1";
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');
    STARTUPINFOW si{};
    PROCESS_INFORMATION pi{};
    si.cb = sizeof(si);
    if (CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                       nullptr, g_dir.c_str(), &si, &pi)) {
        WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
}

static bool StartHidden(const fs::path& bat, HANDLE& hProc)
{
    std::wstring cmd = L"cmd.exe /d /c \"\"" + bat.wstring() + L"\"\"";
    std::wstring cwd = g_dir.wstring();
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');
    STARTUPINFOW si{};
    PROCESS_INFORMATION pi{};
    si.cb = sizeof(si);
    if (!CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                        nullptr, cwd.c_str(), &si, &pi)) {
        return false;
    }
    CloseHandle(pi.hThread);
    hProc = pi.hProcess;
    return true;
}

static void ErrorBox(const std::wstring& text)
{
    MessageBoxW(nullptr, text.c_str(), L"maimaiDX launcher", MB_OK | MB_ICONERROR);
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    wchar_t self[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, self, MAX_PATH) == 0) return 1;
    g_dir = fs::path(self).parent_path();

    Log("launcher start");

    fs::path game = g_dir / L"Sinmai.exe";
    fs::path bat  = g_dir / L"start-steam.bat";

    if (!fs::exists(game)) {
        ErrorBox(L"Sinmai.exe was not found next to this launcher:\n\n" + game.wstring());
        Log("ERROR: Sinmai.exe not found");
        return 1;
    }

    const int kMaxRounds = 3;
    const int kMinHealthySeconds = 25;   // shorter than this = treat as a failed launch

    for (int round = 1; round <= kMaxRounds; ++round) {
        std::string r = std::to_string(round);
        DWORD pid = FindProcessId(L"Sinmai.exe");
        HANDLE hBat = nullptr;

        if (pid == 0) {
            if (!fs::exists(bat)) {
                ErrorBox(L"start-steam.bat was not found next to this launcher:\n\n" + bat.wstring());
                Log("ERROR: start-steam.bat not found");
                return 1;
            }
            KillProcess(L"amdaemon.exe");     // clean slate for this round
            if (!StartHidden(bat, hBat)) {
                ErrorBox(L"Could not start start-steam.bat (error " +
                         std::to_wstring(GetLastError()) + L").");
                Log("ERROR: CreateProcess failed");
                return 1;
            }
            Log("round " + r + ": started start-steam.bat (hidden)");

            for (int i = 0; i < 360 && pid == 0; ++i) {   // up to 90s
                pid = FindProcessId(L"Sinmai.exe");
                if (pid != 0) break;
                if (hBat && WaitForSingleObject(hBat, 0) == WAIT_OBJECT_0) {
                    Log("round " + r + ": the batch finished without starting the game (inject failed)");
                    CloseHandle(hBat);
                    hBat = nullptr;
                    break;
                }
                Sleep(250);
            }
        } else {
            Log("round " + r + ": Sinmai.exe already running, just waiting for it");
        }

        if (pid == 0) {
            Log("round " + r + ": no game, moving on");
            continue;
        }

        Log("Sinmai.exe is up (pid " + std::to_string(pid) + "), waiting for it to exit");
        DWORD began = GetTickCount();
        HANDLE hGame = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!hGame) {
            Log("could not open the game process; waiting on the batch instead");
            if (hBat) { WaitForSingleObject(hBat, INFINITE); CloseHandle(hBat); hBat = nullptr; }
            Log("done");
            return 3;
        }
        WaitForSingleObject(hGame, INFINITE);
        CloseHandle(hGame);

        DWORD lived = (GetTickCount() - began) / 1000;
        Log("game exited after " + std::to_string(lived) + "s");

        if (hBat) {
            WaitForSingleObject(hBat, 10000);   // let the bat finish taskkill amdaemon
            CloseHandle(hBat);
            hBat = nullptr;
        }

        if (lived >= (DWORD)kMinHealthySeconds) {
            Log("launch looks healthy, done");
            return 0;
        }
        Log("that launch died too early, retrying the whole chain");
        Sleep(2000);
    }

    Log("gave up after all rounds");
    return 4;
}
