// MaimaiSteam.exe -- Steam entry point / supervisor for maimai DX (SDEZ)
//
// Build: tools/steam-launcher/build.ps1 (MSVC) or build_wsl.sh (zig cross compile)
// Install: put this exe next to Sinmai.exe, add it as a Steam non-Steam game.
// Details / gotchas: tools/steam-launcher/README.md
//
// Why this exists: Steam can only point at one exe, and the game must be started
// with amdaemon.exe running *and* amdaemon must have mai2hook.dll injected, plus
// the OPENSSL_ia32cap env var. That is what start.bat does -- but adding a .bat
// to Steam leaves a console window on screen, and (measured) running "inject"
// through cmd from inside Steam's job object fails / hangs.
//
// So this launcher does the whole chain itself, with no cmd.exe and no console:
//   round (up to 3):
//     attempt (up to 5):
//        inject.exe -d -k mai2hook.dll amdaemon.exe -f -c configs...
//          (output captured to inject-out.txt; killed if it hangs)
//        wait ~3s, is amdaemon.exe alive?  no -> kill leftovers, retry
//     amdaemon is up -> start Sinmai.exe -monitor 2
//     game alive after 15s and amdaemon still there -> healthy: wait for exit
//     game died early / amdaemon gone -> kill leftovers, next round
//   after the game exits: taskkill amdaemon.exe (cleanup), then quit
//
// Log: maimaiDX.log (plus inject-out.txt for inject's own output).

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
#include <sstream>
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

// Are we running inside a job object that lets children break away? Steam puts
// the games it launches into a job, and processes inside it cannot load
// mai2hook.dll into amdaemon (measured: "DLL failed to load inside target
// process" on every attempt, while the exact same exe works outside Steam).
// So we first try CREATE_BREAKAWAY_FROM_JOB and remember whether it worked.
static bool g_breakawayOk = false;
static bool g_breakawayTried = false;

// Start a program with no console window; optionally capture its output to a
// file. Returns the process handle (caller closes it) or nullptr.
// Note: inject.exe is EXPECTED to stay alive for the whole session, so this
// must not wait for it.
static HANDLE StartHidden(const std::wstring& cmdline, const fs::path* captureTo)
{
    HANDLE hOut = INVALID_HANDLE_VALUE;
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    if (captureTo != nullptr) {
        SECURITY_ATTRIBUTES sa{};
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = TRUE;
        hOut = CreateFileW(captureTo->c_str(), GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, &sa,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hOut != INVALID_HANDLE_VALUE) {
            si.dwFlags |= STARTF_USESTDHANDLES;
            si.hStdOutput = hOut;
            si.hStdError = hOut;
            si.hStdInput = INVALID_HANDLE_VALUE;
        }
    }

    std::vector<wchar_t> buf(cmdline.begin(), cmdline.end());
    buf.push_back(L'\0');
    PROCESS_INFORMATION pi{};
    DWORD flags = CREATE_NO_WINDOW;
    if (!g_breakawayTried || g_breakawayOk) flags |= CREATE_BREAKAWAY_FROM_JOB;

    BOOL ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, TRUE,
                             flags, nullptr, g_dir.c_str(), &si, &pi);
    if (!ok && (flags & CREATE_BREAKAWAY_FROM_JOB)) {
        // the job does not allow breakaway - retry inside the job
        if (!g_breakawayTried) {
            g_breakawayTried = true;
            g_breakawayOk = false;
            Log("CREATE_BREAKAWAY_FROM_JOB not allowed (error "
                + std::to_string(GetLastError()) + "), staying inside the job");
        }
        ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, TRUE,
                            CREATE_NO_WINDOW, nullptr, g_dir.c_str(), &si, &pi);
    } else if (ok && !g_breakawayTried) {
        g_breakawayTried = true;
        g_breakawayOk = true;
        Log("children will start outside the job (breakaway ok)");
    }
    if (hOut != INVALID_HANDLE_VALUE) CloseHandle(hOut);
    if (!ok) return nullptr;
    CloseHandle(pi.hThread);
    return pi.hProcess;
}

static void KillProcess(const std::wstring& name)
{
    std::wstring cmd = L"cmd.exe /c taskkill /f /im " + name;
    HANDLE h = StartHidden(cmd, nullptr);
    if (h) { WaitForSingleObject(h, 5000); CloseHandle(h); }
}

static bool WaitForAmdaemonUp(int seconds)
{
    for (int i = 0; i < seconds * 4; ++i) {
        if (FindProcessId(L"amdaemon.exe") != 0) return true;
        Sleep(250);
    }
    return false;
}

static bool StartGame(std::wstring& err)
{
    std::wstring cmd = L"Sinmai.exe -monitor 2";
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');
    STARTUPINFOW si{};
    PROCESS_INFORMATION pi{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_SHOWNORMAL;
    if (!CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, g_dir.c_str(), &si, &pi)) {
        err = L"could not start Sinmai.exe (error " + std::to_wstring(GetLastError()) + L")";
        return false;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return true;
}

static bool DefineEnv()
{
    // the same thing start.bat does; children inherit it
    return SetEnvironmentVariableW(L"OPENSSL_ia32cap", L":~0x20000000") != 0;
}

// Dump the things that differ between "launched by Steam" and "launched by hand"
// -- the exact same chain works outside Steam, so one of these must be it.
static std::string Narrow(const std::wstring& w)
{
    std::string out;
    for (wchar_t c : w) out.push_back(c < 128 ? (char)c : '?');
    return out;
}

static void LogContext()
{
    HANDLE tok = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) {
        TOKEN_ELEVATION el{};
        DWORD len = 0;
        if (GetTokenInformation(tok, TokenElevation, &el, sizeof(el), &len))
            Log(std::string("elevated = ") + (el.TokenIsElevated ? "YES" : "no"));
        CloseHandle(tok);
    }

    const wchar_t* vars[] = { L"__COMPAT_LAYER", L"SteamAppId", L"SteamGameId",
                              L"SteamOverlayGameId", L"SteamClientLaunch",
                              L"OPENSSL_ia32cap", L"PATH" };
    for (const wchar_t* v : vars) {
        wchar_t buf[4096];
        DWORD n = GetEnvironmentVariableW(v, buf, 4096);
        if (n == 0) {
            Log("env " + Narrow(v) + " = <unset>");
        } else {
            std::wstring val(buf, buf + (n < 4096 ? n : 4095));
            std::string s = Narrow(val);
            if (v[0] == L'P' && wcslen(v) == 4) {    // PATH: just log the length
                Log("env PATH length = " + std::to_string(s.size()));
            } else {
                Log("env " + Narrow(v) + " = " + s);
            }
        }
    }
    Log("Y: drive type = " + std::to_string((int)GetDriveTypeW(L"Y:\\"))
        + " (2=removable 3=fixed 4=remote 5=cdrom 6=ramdisk 1=no root)");
    Log("cwd = " + Narrow(fs::current_path().wstring()));
}

static bool AmdaemonInjectionRound(int round, const std::wstring& injectArgs)
{
    const int kAttempts = 3;
    for (int attempt = 1; attempt <= kAttempts; ++attempt) {
        KillProcess(L"amdaemon.exe");
        KillProcess(L"inject.exe");
        Sleep(300);

        fs::path out = g_dir / L"inject-out.txt";
        std::wstring cmd = L"inject.exe " + injectArgs;
        HANDLE hInject = StartHidden(cmd, &out);
        Log("round " + std::to_string(round) + " attempt " + std::to_string(attempt)
            + ": inject started" + (hInject ? "" : " FAILED"));
        if (!hInject) continue;

        // inject.exe stays alive for the whole session (that is normal) -- what
        // we care about is whether amdaemon came up.
        if (WaitForAmdaemonUp(8)) {
            if (hInject) CloseHandle(hInject);
            Log("round " + std::to_string(round) + " attempt " + std::to_string(attempt)
                + ": amdaemon.exe is up");
            return true;
        }
        CloseHandle(hInject);
        KillProcess(L"inject.exe");
        Log("round " + std::to_string(round) + " attempt " + std::to_string(attempt)
            + ": amdaemon did not come up (see inject-out.txt)");
    }
    return false;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    wchar_t self[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, self, MAX_PATH) == 0) return 1;
    g_dir = fs::path(self).parent_path();

    Log("launcher start");
    DefineEnv();
    LogContext();

    if (!fs::exists(g_dir / L"Sinmai.exe")) {
        MessageBoxW(nullptr, (L"Sinmai.exe was not found next to this launcher:\n\n" +
                              (g_dir / L"Sinmai.exe").wstring()).c_str(),
                    L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
        Log("ERROR: Sinmai.exe not found");
        return 1;
    }
    if (!fs::exists(g_dir / L"inject.exe") || !fs::exists(g_dir / L"mai2hook.dll")) {
        MessageBoxW(nullptr, L"inject.exe / mai2hook.dll not found next to this launcher.",
                    L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
        Log("ERROR: inject.exe or mai2hook.dll missing");
        return 1;
    }

    const std::wstring injectArgs =
        L"-d -k mai2hook.dll amdaemon.exe -f -c config_common.json config_server.json config_client.json";
    const int kRounds = 2;

    for (int round = 1; round <= kRounds; ++round) {
        DWORD pid = FindProcessId(L"Sinmai.exe");
        if (pid != 0) {
            Log("round " + std::to_string(round) + ": Sinmai.exe already running, just waiting");
        } else {
            if (!AmdaemonInjectionRound(round, injectArgs)) {
                Log("round " + std::to_string(round) + ": injection failed 5 times");
                continue;
            }
            std::wstring err;
            if (!StartGame(err)) {
                MessageBoxW(nullptr, err.c_str(), L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
                Log("ERROR: " + std::string(err.begin(), err.end()));
                KillProcess(L"amdaemon.exe");
                return 1;
            }
            Log("round " + std::to_string(round) + ": started Sinmai.exe -monitor 2");
            pid = 0;
            for (int i = 0; i < 120 && pid == 0; ++i) {   // up to 30s
                pid = FindProcessId(L"Sinmai.exe");
                if (pid == 0) Sleep(250);
            }
            if (pid == 0) {
                Log("round " + std::to_string(round) + ": Sinmai.exe never appeared");
                KillProcess(L"amdaemon.exe");
                continue;
            }
        }

        DWORD began = GetTickCount();
        Sleep(15000);                       // give it time to get past startup
        bool gameAlive = FindProcessId(L"Sinmai.exe") != 0;
        bool amdAlive  = FindProcessId(L"amdaemon.exe") != 0;
        Log(std::string("15s check: game=") + (gameAlive ? "up" : "gone")
            + " amdaemon=" + (amdAlive ? "up" : "gone"));

        if (!gameAlive || !amdAlive) {
            Log("round " + std::to_string(round) + ": launch failed (this is the black-screen case), retrying");
            KillProcess(L"Sinmai.exe");
            KillProcess(L"amdaemon.exe");
            KillProcess(L"inject.exe");
            Sleep(2000);
            continue;
        }

        Log("launch looks healthy, waiting for the game to exit");
        DWORD exitPid = FindProcessId(L"Sinmai.exe");
        HANDLE hGame = OpenProcess(SYNCHRONIZE, FALSE, exitPid);
        if (hGame) {
            WaitForSingleObject(hGame, INFINITE);
            CloseHandle(hGame);
        }
        Log("game exited after " + std::to_string((GetTickCount() - began) / 1000) + "s");
        KillProcess(L"amdaemon.exe");
        KillProcess(L"inject.exe");
        Sleep(1500);
        Log("done");
        return 0;
    }

    MessageBoxW(nullptr,
                L"maimai could not be started (amdaemon.exe never came up).\n\n"
                L"See maimaiDX.log and inject-out.txt next to this launcher.",
                L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
    Log("gave up after all rounds");
    return 4;
}
