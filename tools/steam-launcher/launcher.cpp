// MaimaiSteam.exe -- Steam entry point / supervisor for maimai DX (SDEZ)
//
// Build: tools/steam-launcher/build.ps1 (MSVC) or build_wsl.sh (zig cross compile)
// Install: put this exe next to Sinmai.exe, add it as a Steam non-Steam game.
// Details / gotchas: tools/steam-launcher/README.md
//
// Why this exists: Steam can only point at one exe, and the game must be started
// with amdaemon.exe running *and* amdaemon must have mai2hook.dll injected, plus
// the OPENSSL_ia32cap env var. That is what start.bat does -- but adding a .bat
// to Steam leaves a console window on screen.
//
// So this launcher does the whole chain itself, with no cmd.exe and no console:
//   round (up to 2):
//     attempt (up to 3):
//        inject.exe -d -k mai2hook.dll amdaemon.exe -f -c configs...
//          (output captured to inject-out.txt; killed if it hangs)
//        wait ~8s, is amdaemon.exe alive?  no -> kill leftovers, retry
//     amdaemon is up -> start Sinmai.exe -monitor 2
//     game alive after 15s and amdaemon still there -> healthy: wait for exit
//     game died early / amdaemon gone -> kill leftovers, next round
//   after the game exits: taskkill amdaemon.exe (cleanup), then quit
//
// Steam special case (see README): Steam pushes its overlay
// (gameoverlayrenderer64.dll) into *every* process of the tree it launched --
// measured in steam/logs/gameoverlay_renderer.txt: even a plain taskkill.exe we
// spawned got it. That overlay lands in amdaemon.exe at creation time, while
// inject.exe is still doing its "create -> remote LoadLibrary" dance, and the
// remote LoadLibrary then fails:
//     mai2hook.dll: DLL failed to load inside target process
// (the very same chain works in ~1s when started by hand). Getting out of
// Steam's job object and clearing the Steam* env vars did not change that, so
// the injection is handed to the Task Scheduler instead: that process is a
// child of svchost, not of anything Steam tracks, so nothing gets injected into
// it and the chain behaves exactly like a hand-started one. This process keeps
// running as Steam's tracked "game", so playtime / "playing" still work.
//
// Flags (only used for testing / troubleshooting):
//   --inject-only   the helper the scheduled task runs: inject, wait, exit
//   --no-detach     never use the Task Scheduler detour, always inject in-tree
//   --force-detach  always take the Task Scheduler detour (for testing it by hand)
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
#include <string>
#include <vector>

namespace fs = std::filesystem;

static fs::path g_dir;                 // directory holding MaimaiSteam.exe (== game dir)
static std::wstring g_self;            // full path of this exe
static bool g_injectOnly = false;      // running as the scheduled-task helper
static bool g_noDetach = false;        // --no-detach
static bool g_forceDetach = false;     // --force-detach

static void Log(const std::string& msg)
{
    std::ofstream f(g_dir / L"maimaiDX.log", std::ios::app);
    if (!f.is_open()) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    char ts[32];
    wsprintfA(ts, "%02d/%02d %02d:%02d:%02d", st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    f << ts << "  " << (g_injectOnly ? "[helper] " : "") << msg << std::endl;
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

// Is Steam's overlay DLL resident in *this* process? If yes, Steam is actively
// injecting into this process tree, and therefore into every child we create
// (amdaemon.exe included) -- the case the Task Scheduler detour exists for.
static bool OverlayLoaded()
{
    return GetModuleHandleW(L"gameoverlayrenderer64.dll") != nullptr
        || GetModuleHandleW(L"gameoverlayrenderer.dll") != nullptr;
}

static bool EnvSet(const wchar_t* name)
{
    wchar_t buf[64];
    return GetEnvironmentVariableW(name, buf, 64) > 0;
}

static bool StartedBySteam()
{
    return EnvSet(L"SteamClientLaunch") || EnvSet(L"SteamAppId") || EnvSet(L"SteamGameId");
}

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
    // Steam puts the games it launches into a job object; try to get our
    // children out of it (measured: allowed on this machine, but on its own it
    // does not fix the injection -- see the header).
    BOOL ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, TRUE,
                             flags | CREATE_BREAKAWAY_FROM_JOB, nullptr, g_dir.c_str(), &si, &pi);
    if (!ok) {
        ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, TRUE,
                            flags, nullptr, g_dir.c_str(), &si, &pi);
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

static bool RunAndWait(const std::wstring& cmdline, DWORD timeoutMs = 10000)
{
    HANDLE h = StartHidden(cmdline, nullptr);
    if (!h) return false;
    bool done = WaitForSingleObject(h, timeoutMs) == WAIT_OBJECT_0;
    if (done) {
        DWORD code = 1;
        GetExitCodeProcess(h, &code);
        done = (code == 0);
    }
    CloseHandle(h);
    return done;
}

static bool WaitForAmdaemonUp(int seconds)
{
    for (int i = 0; i < seconds * 4; ++i) {
        if (FindProcessId(L"amdaemon.exe") != 0) return true;
        Sleep(250);
    }
    return false;
}

static const wchar_t* kInjectArgs =
    L"-d -k mai2hook.dll amdaemon.exe -f -c config_common.json config_server.json config_client.json";

// ---------------------------------------------------------------------------
// Detached injection: let the Task Scheduler start this same exe with
// --inject-only. That process is a child of svchost (= not part of Steam's
// process tree), so Steam's overlay never gets into it, and amdaemon.exe is
// created clean -- exactly like a hand-started start.bat.
// ---------------------------------------------------------------------------
static const wchar_t* kInjectTask = L"MaimaiSteamInject";

// Quote one argv element the way CommandLineToArgvW expects to read it back.
static std::wstring QuoteArg(const std::wstring& s)
{
    std::wstring out = L"\"";
    for (wchar_t c : s) {
        if (c == L'"') out += L'\\';
        out += c;
    }
    out += L"\"";
    return out;
}

static void RemoveInjectTask()
{
    RunAndWait(L"schtasks.exe /delete /tn " + std::wstring(kInjectTask) + L" /f", 15000);
}

// The helper reports through this little file, so the supervising process knows
// that *this* injection finished (a left-over amdaemon.exe from an earlier run
// must not be mistaken for a fresh one).
static fs::path InjectResultFile() { return g_dir / L"_maimai-steam-inject.txt"; }

static void WriteInjectResult(const char* text)
{
    std::ofstream f(InjectResultFile(), std::ios::trunc);
    if (f.is_open()) f << text << std::endl;
}

static bool WaitInjectResult(int seconds, bool& ok)
{
    for (int i = 0; i < seconds * 4; ++i) {
        if (fs::exists(InjectResultFile())) {
            std::ifstream f(InjectResultFile());
            std::string line;
            std::getline(f, line);
            ok = line.rfind("ok", 0) == 0;
            return true;
        }
        Sleep(250);
    }
    return false;
}

static bool StartDetachedInject()
{
    std::wstring what = L"\"" + g_self + L"\" --inject-only";
    std::wstring create = L"schtasks.exe /create /tn " + std::wstring(kInjectTask)
        + L" /tr " + QuoteArg(what) + L" /sc once /st 00:00 /f";
    if (!RunAndWait(create, 20000)) {
        Log("detach: schtasks /create failed");
        return false;
    }
    if (!RunAndWait(L"schtasks.exe /run /tn " + std::wstring(kInjectTask), 20000)) {
        Log("detach: schtasks /run failed");
        return false;
    }
    Log("detach: injection handed to the Task Scheduler (outside Steam's process tree)");
    return true;
}

// The --inject-only side: bring amdaemon up (or not) and exit. inject.exe is
// deliberately *not* killed on success -- it has to stay alive for the session.
static int RunInjectOnly()
{
    Log("running the injection chain outside Steam's process tree");
    for (int attempt = 1; attempt <= 3; ++attempt) {
        KillProcess(L"amdaemon.exe");
        KillProcess(L"inject.exe");
        Sleep(300);

        fs::path out = g_dir / L"inject-out.txt";
        HANDLE h = StartHidden(L"inject.exe " + std::wstring(kInjectArgs), &out);
        if (!h) {
            Log("attempt " + std::to_string(attempt) + ": could not start inject.exe");
            continue;
        }
        if (WaitForAmdaemonUp(10)) {
            Log("attempt " + std::to_string(attempt) + ": amdaemon.exe is up");
            CloseHandle(h);
            WriteInjectResult("ok");
            return 0;
        }
        CloseHandle(h);
        KillProcess(L"inject.exe");
        Log("attempt " + std::to_string(attempt) + ": amdaemon did not come up (see inject-out.txt)");
    }
    Log("gave up");
    WriteInjectResult("failed");
    return 1;
}

// ---------------------------------------------------------------------------
// In-tree injection (used when we were not started by Steam, and as fallback)
// ---------------------------------------------------------------------------
static bool AmdaemonInjectionRound(int round, int attempts)
{
    for (int attempt = 1; attempt <= attempts; ++attempt) {
        KillProcess(L"amdaemon.exe");
        KillProcess(L"inject.exe");
        Sleep(300);

        fs::path out = g_dir / L"inject-out.txt";
        HANDLE hInject = StartHidden(L"inject.exe " + std::wstring(kInjectArgs), &out);
        Log("round " + std::to_string(round) + " attempt " + std::to_string(attempt)
            + ": inject started" + (hInject ? "" : " FAILED"));
        if (!hInject) continue;

        // inject.exe stays alive for the whole session (that is normal) -- what
        // we care about is whether amdaemon came up.
        if (WaitForAmdaemonUp(8)) {
            CloseHandle(hInject);
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
    BOOL ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE,
                             CREATE_BREAKAWAY_FROM_JOB, nullptr, g_dir.c_str(), &si, &pi);
    if (!ok) {
        ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, 0,
                            nullptr, g_dir.c_str(), &si, &pi);
    }
    if (!ok) {
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

// Steam exports SteamAppId / SteamGameId / SteamOverlayGameId / SteamClientLaunch
// into the process it launches, and everything we start inherits them. They
// turned out not to be the reason the injection fails (measured both ways), but
// the chain works with them unset, so drop them for our children. (Steam tracks
// the process it launched, so this does not affect the "playing" state.)
static void ClearSteamEnv()
{
    const wchar_t* vars[] = {
        L"SteamAppId", L"SteamGameId", L"SteamOverlayGameId", L"SteamClientLaunch",
        L"SteamAppUser", L"SteamUser", L"SteamPath", L"SteamClientDll",
        L"SteamClientDll64", L"SteamGameIdFile",
    };
    for (const wchar_t* v : vars) SetEnvironmentVariableW(v, nullptr);
}

// Dump the things that differ between "launched by Steam" and "launched by hand".
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
    Log("cwd = " + Narrow(fs::current_path().wstring()));
}

static void ReadInjectOutToLog()
{
    std::ifstream f(g_dir / L"inject-out.txt", std::ios::binary);
    if (!f.is_open()) return;
    std::string line;
    while (std::getline(f, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
        if (!line.empty()) Log("  inject says: " + line);
    }
}

static int Fail(int code, const std::wstring& message)
{
    Log("gave up");
    ReadInjectOutToLog();
    MessageBoxW(nullptr, message.c_str(), L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
    return code;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv) {
        for (int i = 1; i < argc; ++i) {
            if (_wcsicmp(argv[i], L"--inject-only") == 0) g_injectOnly = true;
            else if (_wcsicmp(argv[i], L"--no-detach") == 0) g_noDetach = true;
            else if (_wcsicmp(argv[i], L"--force-detach") == 0) g_forceDetach = true;
        }
        LocalFree(argv);
    }

    wchar_t self[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, self, MAX_PATH) == 0) return 1;
    g_self = self;
    g_dir = fs::path(self).parent_path();

    const bool overlay = OverlayLoaded();
    const bool bySteam = StartedBySteam();

    Log(std::string("launcher start")
        + (g_injectOnly ? " (inject-only helper)" : "")
        + (bySteam ? " [started by Steam]" : "")
        + (overlay ? " [Steam overlay DLL is inside this process]" : ""));
    DefineEnv();
    ClearSteamEnv();
    if (!g_injectOnly) {
        LogContext();
        if (bySteam || overlay) {
            Log("Steam is in this process tree -> injection goes to the Task Scheduler");
        }
    }

    if (!fs::exists(g_dir / L"Sinmai.exe")) {
        if (!g_injectOnly) {
            MessageBoxW(nullptr, (L"Sinmai.exe was not found next to this launcher:\n\n" +
                                  (g_dir / L"Sinmai.exe").wstring()).c_str(),
                        L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
        }
        Log("ERROR: Sinmai.exe not found");
        return 1;
    }
    if (!fs::exists(g_dir / L"inject.exe") || !fs::exists(g_dir / L"mai2hook.dll")) {
        if (!g_injectOnly) {
            MessageBoxW(nullptr, L"inject.exe / mai2hook.dll not found next to this launcher.",
                        L"MaimaiSteam launcher", MB_OK | MB_ICONERROR);
        }
        Log("ERROR: inject.exe or mai2hook.dll missing");
        return 1;
    }

    if (g_injectOnly) return RunInjectOnly();

    const int kRounds = 2;
    const int kAttempts = 3;
    bool detachTried = false;
    bool detachOk = false;

    for (int round = 1; round <= kRounds; ++round) {
        DWORD pid = FindProcessId(L"Sinmai.exe");
        if (pid != 0) {
            Log("round " + std::to_string(round) + ": Sinmai.exe already running, just waiting");
        } else {
            if (round == 1 && !g_noDetach && (bySteam || overlay || g_forceDetach) && !detachTried) {
                detachTried = true;
                fs::remove(InjectResultFile());
                if (StartDetachedInject()) {
                    bool ok = false;
                    if (WaitInjectResult(35, ok) && ok) {
                        Log("detached injection: amdaemon.exe is up");
                        detachOk = true;
                    } else if (ok) {
                        Log("detached injection reported failure");
                    } else {
                        Log("detached injection timed out, falling back");
                    }
                }
                fs::remove(InjectResultFile());
                RemoveInjectTask();
            }
            if (!detachOk && !AmdaemonInjectionRound(round, kAttempts)) {
                Log("round " + std::to_string(round) + ": injection failed");
                continue;
            }
            std::wstring err;
            if (!StartGame(err)) {
                Log("ERROR: " + std::string(err.begin(), err.end()));
                KillProcess(L"amdaemon.exe");
                return Fail(1, err);
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
        RemoveInjectTask();
        Sleep(1500);
        Log("done");
        return 0;
    }

    return Fail(4, L"maimai could not be started (amdaemon.exe never came up).\n\n"
                   L"See maimaiDX.log and inject-out.txt next to this launcher.\n\n"
                   L"If maimaiDX.log says the Steam overlay DLL is inside this process,\n"
                   L"turn the overlay off for this shortcut (Steam: library -> maimai DX ->\n"
                   L"properties -> \"Enable the Steam Overlay while in-game\") and try again.");
}
