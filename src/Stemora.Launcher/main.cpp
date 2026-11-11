#include <string>
#include <vector>
#include <windows.h>

// A small native entry point keeps the portable folder easy to browse. The WinUI
// application and its runtime stay together in app/, without changing data/ or cli/.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR arguments, int) {
    std::vector<wchar_t> path(32768);
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) {
        return 1;
    }
    const std::wstring module(path.data(), length);
    const auto separator = module.find_last_of(L"\\/");
    if (separator == std::wstring::npos) {
        return 1;
    }
    const auto executable = module.substr(0, separator + 1) + L"app\\Stemora.App.exe";
    auto command = L"\"" + executable + L"\"";
    if (arguments && *arguments) {
        command += L" ";
        command += arguments;
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup,
                        &process)) {
        const auto error = GetLastError();
        const bool chinese = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE;
        const auto message =
            chinese ? L"无法启动 Stemora。请完整解压便携包，并保留 app 文件夹。\n\n"
                    : L"Could not start Stemora. Extract the complete portable package and keep the app folder.\n\n";
        wchar_t detail[1024]{};
        FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0, detail, 1024,
                       nullptr);
        MessageBoxW(nullptr, (std::wstring(message) + detail).c_str(), L"Stemora", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD result = 1;
    GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hProcess);
    return static_cast<int>(result);
}
