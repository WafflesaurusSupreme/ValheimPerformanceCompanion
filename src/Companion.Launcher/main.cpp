#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <iterator>

// Native startup boundary: does not require the .NET or Visual C++ redistributable.
namespace {
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE handle) : value(handle) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value != nullptr) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
std::wstring system_error(DWORD code) {
    wchar_t message[2048]{};
    FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr,
        code, 0, message, static_cast<DWORD>(std::size(message)), nullptr);
    return std::wstring(message) + L" (Windows error " + std::to_wstring(code) + L")";
}
std::wstring quote(const std::wstring& argument) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : argument) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'\"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0; result += c;
    }
    result.append(slashes * 2, L'\\');
    return result + L"\"";
}
bool regular_file(const std::wstring& path) {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}
void output(const std::wstring& message, bool check_only) {
    if (check_only) {
        int length = WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::vector<char> bytes(static_cast<size_t>(length));
        WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1, bytes.data(), length, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), bytes.data(), static_cast<DWORD>(length - 1), &written, nullptr);
    } else MessageBoxW(nullptr, message.c_str(), L"Valheim Performance Companion · Startup check", MB_OK | MB_ICONERROR);
}
}
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr) return 1;
    std::vector<std::wstring> arguments;
    for (int i = 1; i < argc; ++i) arguments.emplace_back(argv[i]);
    LocalFree(argv);
    const bool check_only = arguments.size() == 1 && arguments[0] == L"--check-startup";
    wchar_t module[32768]{};
    DWORD count = GetModuleFileNameW(nullptr, module, static_cast<DWORD>(std::size(module)));
    if (count == 0 || count >= std::size(module)) { output(L"Cannot locate the portable app folder.", check_only); return 1; }
    std::wstring root(module, count);
    root.resize(root.find_last_of(L"\\/") + 1);
    // Generated from the actual publish output, so transitive/runtime DLLs are also checked.
    const wchar_t* required[] = {
#include "required-files.inc"
    };
    std::wstring missing;
    for (const wchar_t* name : required) if (!regular_file(root + name)) missing += L"\n  • " + std::wstring(name);
    if (!missing.empty()) {
        output(L"The portable app is missing required components:" + missing
            + L"\n\nAction: Extract the COMPLETE release ZIP into one folder, then run ValheimPerformanceCompanionLauncher.exe from that folder."
              L"\n\nThe .NET desktop runtime and native runtime are included. A separate .NET Framework or C# runtime installation is not needed."
              L"\n\nApp folder: " + root, check_only);
        return 2;
    }
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
    for (const wchar_t* name : { L"vcruntime140_cor3.dll", L"hostfxr.dll", L"PresentationNative_cor3.dll" }) {
        HMODULE library = LoadLibraryExW((root + name).c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (library == nullptr) {
            DWORD error = GetLastError();
            output(L"A required runtime component could not load: " + std::wstring(name) + L"\n\n" + system_error(error)
                + L"\nAction: Re-extract the complete Windows x64 release. If this persists, check Windows Update and antivirus quarantine."
                  L"\nThe app includes its .NET/native runtime; do not install an unrelated .NET Framework version.\n\nApp folder: " + root, check_only);
            return 3;
        }
        FreeLibrary(library);
    }
    if (check_only) { output(L"PASS: bundled application, .NET desktop runtime, native runtime, recorder and PerfView startup checks.\n", true); return 0; }
    std::wstring executable = root + L"ValheimPerformanceCompanion.exe";
    std::wstring command = quote(executable);
    for (const auto& argument : arguments) command += L" " + quote(argument);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, root.c_str(), &startup, &process)) {
        output(L"The desktop application could not start.\n\n" + system_error(GetLastError())
            + L"\nAction: Re-extract the complete release and check antivirus quarantine.\nApp folder: " + root, false);
        return 4;
    }
    Handle thread(process.hThread), child(process.hProcess);
    return 0;
}
