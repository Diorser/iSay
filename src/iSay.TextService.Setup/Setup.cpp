#include "../iSay.TextService/CompositionContract.h"
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iomanip>
#include <set>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace
{
    constexpr WCHAR ServiceKey[] = L"Software\\Classes\\CLSID\\{429952CD-D1B7-4769-858A-6D56E4EF488D}";
    class OutputRedirect
    {
        std::streambuf* output_ = std::cout.rdbuf();
        std::streambuf* error_ = std::cerr.rdbuf();
        bool active_ = false;
    public:
        void Use(std::streambuf* target) { std::cout.rdbuf(target); std::cerr.rdbuf(target); active_ = true; }
        ~OutputRedirect()
        {
            if (!active_) return;
            std::cout.flush(); std::cerr.flush();
            std::cout.rdbuf(output_); std::cerr.rdbuf(error_);
        }
    };
    std::vector<LANGID> Languages()
    {
        const auto count = GetKeyboardLayoutList(0, nullptr);
        if (count < 1 || count > 128) throw HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        std::vector<HKL> values(count);
        const auto actual = GetKeyboardLayoutList(count, values.data());
        if (actual < 1 || actual > count) throw HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        values.resize(actual);
        std::set<LANGID> languages;
        for (const auto layout : values) languages.insert(LOWORD(reinterpret_cast<ULONG_PTR>(layout)));
        return {languages.begin(), languages.end()};
    }
    std::vector<LANGID> ParseLanguages(const std::wstring& text)
    {
        // 语言列表来自原用户的只读查询，禁止空项、别名、符号、重复及超长输入进入提权分支。
        constexpr size_t MaximumLanguages = 128;
        constexpr size_t MaximumLength = MaximumLanguages * 6 - 1;
        if (text.empty() || text.size() > MaximumLength) throw E_INVALIDARG;
        std::set<LANGID> languages;
        size_t start = 0;
        while (start < text.size())
        {
            const auto end = text.find(L',', start);
            const auto length = (end == std::wstring::npos ? text.size() : end) - start;
            if (length < 1 || length > 5 || languages.size() >= MaximumLanguages) throw E_INVALIDARG;
            unsigned long value = 0;
            for (size_t index = start; index < start + length; ++index)
            {
                if (text[index] < L'0' || text[index] > L'9') throw E_INVALIDARG;
                value = value * 10 + static_cast<unsigned long>(text[index] - L'0');
            }
            if (value == 0 || value >= 0xffff || PRIMARYLANGID(static_cast<LANGID>(value)) == LANG_NEUTRAL ||
                !IsValidLocale(MAKELCID(static_cast<LANGID>(value), SORT_DEFAULT), LCID_SUPPORTED) ||
                !languages.insert(static_cast<LANGID>(value)).second) throw E_INVALIDARG;
            if (end == std::wstring::npos) break;
            start = end + 1;
            if (start == text.size()) throw E_INVALIDARG;
        }
        return {languages.begin(), languages.end()};
    }
    void Require(HRESULT hr) { if (FAILED(hr)) throw hr; }
    void Step(const char* name, HRESULT hr)
    {
        std::cout << "{\"step\":\"" << name << "\",\"hresult\":\"0x" << std::hex << std::setw(8) << std::setfill('0')
            << static_cast<unsigned long>(hr) << "\"}" << std::dec << std::endl;
        Require(hr);
    }
    void LanguageStep(const char* name, LANGID language, HRESULT hr)
    {
        std::cout << "{\"step\":\"" << name << "\",\"language\":" << language << ",\"hresult\":\"0x"
            << std::hex << std::setw(8) << std::setfill('0') << static_cast<unsigned long>(hr) << "\"}" << std::dec << std::endl;
        Require(hr);
    }
    void SetString(HKEY key, const WCHAR* name, const std::wstring& value)
    {
        auto status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(WCHAR)));
        Require(HRESULT_FROM_WIN32(status));
    }
    void RegisterCom(const std::filesystem::path& dll, REGSAM view)
    {
        if (!std::filesystem::is_regular_file(dll)) throw HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        HKEY key = nullptr;
        auto status = RegCreateKeyExW(HKEY_CURRENT_USER, ServiceKey, 0, nullptr, 0, KEY_WRITE | view, nullptr, &key, nullptr);
        Require(HRESULT_FROM_WIN32(status));
        try
        {
            SetString(key, nullptr, L"iSay 语音组合输入");
            HKEY server = nullptr;
            status = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE | view, nullptr, &server, nullptr);
            Require(HRESULT_FROM_WIN32(status));
            try { SetString(server, nullptr, dll.wstring()); SetString(server, L"ThreadingModel", L"Apartment"); }
            catch (...) { RegCloseKey(server); throw; }
            RegCloseKey(server);
        }
        catch (...) { RegCloseKey(key); throw; }
        RegCloseKey(key);
    }
    void ValidateComponent(const std::filesystem::path& dll, WORD machine, WORD optionalMagic)
    {
        if (!std::filesystem::is_regular_file(dll)) throw HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        std::ifstream file(dll, std::ios::binary);
        IMAGE_DOS_HEADER dos{};
        if (!file.read(reinterpret_cast<char*>(&dos), sizeof(dos)) || dos.e_magic != IMAGE_DOS_SIGNATURE ||
            dos.e_lfanew < sizeof(dos) || dos.e_lfanew > 16 * 1024 * 1024) throw HRESULT_FROM_WIN32(ERROR_BAD_EXE_FORMAT);
        file.seekg(dos.e_lfanew);
        DWORD signature = 0; IMAGE_FILE_HEADER header{}; WORD magic = 0;
        if (!file.read(reinterpret_cast<char*>(&signature), sizeof(signature)) ||
            !file.read(reinterpret_cast<char*>(&header), sizeof(header)) ||
            !file.read(reinterpret_cast<char*>(&magic), sizeof(magic)) || signature != IMAGE_NT_SIGNATURE ||
            header.Machine != machine || !(header.Characteristics & IMAGE_FILE_DLL) || magic != optionalMagic)
            throw HRESULT_FROM_WIN32(ERROR_BAD_EXE_FORMAT);
    }
    void RemoveCom(REGSAM view)
    {
        HKEY classes = nullptr;
        auto status = RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Classes\\CLSID", 0,
            KEY_WRITE | KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE | DELETE | view, &classes);
        if (status == ERROR_FILE_NOT_FOUND) return;
        Require(HRESULT_FROM_WIN32(status));
        // 只删除本应用的固定 CLSID，不枚举或处理其他输入法。
        status = RegDeleteTreeW(classes, L"{429952CD-D1B7-4769-858A-6D56E4EF488D}");
        RegCloseKey(classes);
        if (status != ERROR_FILE_NOT_FOUND) Require(HRESULT_FROM_WIN32(status));
    }
    TF_INPUTPROCESSORPROFILE KeyboardProfile(ITfInputProcessorProfileMgr* profiles)
    {
        TF_INPUTPROCESSORPROFILE value{};
        const auto hr = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &value);
        // 无活动键盘配置时保留全零快照，仍比较前后是否被改变。
        if (FAILED(hr) || hr == S_FALSE) return {};
        return value;
    }
    bool SameKeyboard(const TF_INPUTPROCESSORPROFILE& a, const TF_INPUTPROCESSORPROFILE& b)
    {
        return a.dwProfileType == b.dwProfileType && a.langid == b.langid && a.clsid == b.clsid &&
            a.guidProfile == b.guidProfile && a.hkl == b.hkl;
    }
    std::string GuidText(REFGUID value);
    void RequireSpeechProfile(ITfInputProcessorProfileMgr* profiles, LANGID language)
    {
        TF_INPUTPROCESSORPROFILE value{};
        auto hr = profiles->GetProfile(TF_PROFILETYPE_INPUTPROCESSOR, language, CLSID_IsayTextService,
            GUID_IsayLanguageProfile, nullptr, &value);
        LanguageStep("query-speech-profile", language, hr);
        if (hr != S_OK) throw FAILED(hr) ? hr : E_FAIL;
        // 激活前核对真实类别，不能先误切键盘再靠事后快照发现。
        const auto serviceMatches = value.clsid == CLSID_IsayTextService;
        const auto profileMatches = value.guidProfile == GUID_IsayLanguageProfile;
        const auto categoryMatches = value.catid == GUID_TFCAT_TIP_SPEECH;
        const auto categoryResult = serviceMatches && profileMatches && categoryMatches ? S_OK : E_UNEXPECTED;
        std::cout << "{\"step\":\"verify-speech-profile-category\",\"language\":" << language
            << ",\"category\":\"" << GuidText(value.catid) << "\",\"expectedCategory\":\"" << GuidText(GUID_TFCAT_TIP_SPEECH)
            << "\",\"serviceMatches\":" << (serviceMatches ? "true" : "false")
            << ",\"profileMatches\":" << (profileMatches ? "true" : "false") << ",\"hresult\":\"0x"
            << std::hex << std::setw(8) << std::setfill('0') << static_cast<unsigned long>(categoryResult) << "\"}" << std::dec << std::endl;
        Require(categoryResult);
    }
    std::string GuidText(REFGUID value)
    {
        WCHAR buffer[40]{};
        if (!StringFromGUID2(value, buffer, static_cast<int>(std::size(buffer)))) throw E_UNEXPECTED;
        std::string result;
        for (const auto character : buffer)
        {
            if (!character) break;
            result.push_back(static_cast<char>(character));
        }
        return result;
    }
}

int wmain(int count, WCHAR** arguments)
{
    std::ofstream log;
    // 日志流先构造，重定向守卫后构造；所有提前返回都先恢复标准流，再关闭日志。
    OutputRedirect redirect;
    if (count >= 4 && std::wstring(arguments[count - 2]) == L"--log")
    {
        log.open(std::filesystem::path(arguments[count - 1]), std::ios::out | std::ios::trunc);
        if (!log) return 4;
        redirect.Use(log.rdbuf()); count -= 2;
    }
    if (count < 2) { std::cerr << "用法：--install 组件目录 | --prepare-system 组件目录 [--languages 十进制语言列表] | --install-user 组件目录 | --validate-install 组件目录 | --list-languages | --uninstall-user | --prepare-categories | --uninstall | --activate | --deactivate | --status [--log 日志文件]\n"; return 2; }
    const std::wstring operation(arguments[1]);
    const bool directoryOperation = operation == L"--install" || operation == L"--prepare-system" || operation == L"--install-user" || operation == L"--validate-install";
    const bool knownOperation = directoryOperation || operation == L"--list-languages" || operation == L"--uninstall-user" ||
        operation == L"--prepare-categories" || operation == L"--uninstall" || operation == L"--activate" || operation == L"--deactivate" || operation == L"--status";
    if (!knownOperation) return 2;
    std::vector<LANGID> requestedLanguages;
    if (operation == L"--prepare-system")
    {
        if (count != 3 && count != 5) return 2;
        if (count == 5)
        {
            if (std::wstring(arguments[3]) != L"--languages") return 2;
            try { requestedLanguages = ParseLanguages(arguments[4]); }
            catch (...) { return 2; }
        }
    }
    else if ((directoryOperation && count != 3) || (!directoryOperation && count != 2)) return 2;
    if (directoryOperation && !arguments[2][0]) return 2;
    if (operation == L"--list-languages")
    {
        // 只读取调用账户的键盘语言，不初始化 COM、注册 TSF 或查询其他用户。
        try
        {
            const auto languages = Languages();
            for (size_t index = 0; index < languages.size(); ++index)
                std::cout << (index == 0 ? "" : ",") << languages[index];
            std::cout << std::endl;
            return 0;
        }
        catch (...) { return 1; }
    }
    if (operation == L"--prepare-system")
    {
        // 无目录时在 COM 初始化前退出，便于安全预检语言参数，也避免留下半次系统准备。
        std::error_code error;
        if (!std::filesystem::is_directory(std::filesystem::path(arguments[2]), error))
        {
            std::cerr << "{\"step\":\"validate-system-directory\",\"valid\":false}" << std::endl;
            return 1;
        }
    }
    if (operation == L"--validate-install")
    {
        // 只校验双架构发行文件，不初始化 TSF 或写注册表，供安装前与后台回归使用。
        try
        {
            const auto directory = std::filesystem::canonical(arguments[2]);
            ValidateComponent(directory / L"x64" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_AMD64, IMAGE_NT_OPTIONAL_HDR64_MAGIC);
            ValidateComponent(directory / L"x86" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_I386, IMAGE_NT_OPTIONAL_HDR32_MAGIC);
            if (!std::filesystem::is_regular_file(directory / L"iSay.ico")) return 1;
            std::cout << "{\"validated\":true}" << std::endl;
            return 0;
        }
        catch (...) { return 1; }
    }
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 3;
    int exitCode = 0;
    try
    {
        ComPtr<ITfInputProcessorProfiles> profiles;
        Step("create-profiles", CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles)));
        ComPtr<ITfInputProcessorProfileMgr> manager; Step("profile-manager", profiles.As(&manager));
        ComPtr<ITfCategoryMgr> categories;
        Step("category-manager", CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&categories)));
        if (operation == L"--prepare-categories")
        {
            // 后台安装验收只预备本服务类别；不注册 DLL、不添加全局语言配置、不激活到其他应用。
            Step("register-service", profiles->Register(CLSID_IsayTextService));
            Step("register-speech-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_TIP_SPEECH, CLSID_IsayTextService));
            Step("register-display-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER, CLSID_IsayTextService));
        }
        else if (operation == L"--prepare-system")
        {
            const auto directory = std::filesystem::canonical(arguments[2]);
            ValidateComponent(directory / L"x64" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_AMD64, IMAGE_NT_OPTIONAL_HDR64_MAGIC);
            ValidateComponent(directory / L"x86" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_I386, IMAGE_NT_OPTIONAL_HDR32_MAGIC);
            if (!std::filesystem::is_regular_file(directory / L"iSay.ico")) throw HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
            // 此分支仅在固定的提权子进程中登记系统服务、类别及语言描述，不写 HKCU 或启用用户配置。
            Step("register-service", profiles->Register(CLSID_IsayTextService));
            Step("register-speech-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_TIP_SPEECH, CLSID_IsayTextService));
            Step("register-display-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER, CLSID_IsayTextService));
            const auto icon = (directory / L"iSay.ico").wstring();
            const std::wstring description = L"iSay Voice input";
            // 安装器显式传入原用户的语言；旧入口保留全语言约定，不使用提权账户的键盘列表。
            if (requestedLanguages.empty()) requestedLanguages.push_back(static_cast<LANGID>(-1));
            for (const auto language : requestedLanguages)
                LanguageStep("add-system-language-profile", language, profiles->AddLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile,
                    description.c_str(), static_cast<ULONG>(description.size()), icon.c_str(), static_cast<ULONG>(icon.size()), 0));
        }
        else if (operation == L"--install-user")
        {
            const auto directory = std::filesystem::canonical(arguments[2]);
            ValidateComponent(directory / L"x64" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_AMD64, IMAGE_NT_OPTIONAL_HDR64_MAGIC);
            ValidateComponent(directory / L"x86" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_I386, IMAGE_NT_OPTIONAL_HDR32_MAGIC);
            const auto languages = Languages();
            const auto before = KeyboardProfile(manager.Get());
            const auto beforeLayout = GetKeyboardLayout(0);
            // 系统准备缺失时先失败，不以原用户令牌重试系统注册或悄悄请求提权。
            for (const auto language : languages) RequireSpeechProfile(manager.Get(), language);
            RegisterCom(directory / L"x64" / L"iSay.TextService.dll", KEY_WOW64_64KEY);
            RegisterCom(directory / L"x86" / L"iSay.TextService.dll", KEY_WOW64_32KEY);
            for (const auto language : languages)
                Step("enable-user-language-profile", profiles->EnableLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile, TRUE));
            Step("keyboard-profile-unchanged", SameKeyboard(before, KeyboardProfile(manager.Get())) ? S_OK : E_UNEXPECTED);
            Step("keyboard-layout-unchanged", beforeLayout == GetKeyboardLayout(0) ? S_OK : E_UNEXPECTED);
        }
        else if (operation == L"--install")
        {
            if (count != 3) throw E_INVALIDARG;
            const auto directory = std::filesystem::canonical(arguments[2]);
            // 任何注册表写入前同时检查两种架构，缺少一份不能留下半套 COM 路径。
            ValidateComponent(directory / L"x64" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_AMD64, IMAGE_NT_OPTIONAL_HDR64_MAGIC);
            ValidateComponent(directory / L"x86" / L"iSay.TextService.dll", IMAGE_FILE_MACHINE_I386, IMAGE_NT_OPTIONAL_HDR32_MAGIC);
            if (!std::filesystem::is_regular_file(directory / L"iSay.ico")) throw HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
            const auto languages = Languages();
            RegisterCom(directory / L"x64" / L"iSay.TextService.dll", KEY_WOW64_64KEY);
            RegisterCom(directory / L"x86" / L"iSay.TextService.dll", KEY_WOW64_32KEY);
            Step("register-service", profiles->Register(CLSID_IsayTextService));
            Step("register-speech-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_TIP_SPEECH, CLSID_IsayTextService));
            Step("register-display-category", categories->RegisterCategory(CLSID_IsayTextService, GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER, CLSID_IsayTextService));
            const auto icon = (directory / L"iSay.ico").wstring();
            const std::wstring description = L"iSay 语音编辑";
            for (const auto language : languages)
            {
                Step("add-language-profile", profiles->AddLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile,
                    description.c_str(), static_cast<ULONG>(description.size()), icon.c_str(), static_cast<ULONG>(icon.size()), 0));
                Step("enable-language-profile", profiles->EnableLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile, TRUE));
            }
        }
        else if (operation == L"--activate" || operation == L"--deactivate")
        {
            const auto before = KeyboardProfile(manager.Get());
            const auto beforeLayout = GetKeyboardLayout(0);
            const auto languages = Languages();
            for (const auto language : languages) RequireSpeechProfile(manager.Get(), language);
            for (const auto language : languages)
            {
                // 仅激活语音类别，不注册键盘类别、不修改用户默认键盘布局。
                const auto hr = operation == L"--activate" ? manager->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, language,
                    CLSID_IsayTextService, GUID_IsayLanguageProfile, nullptr, TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE)
                    : manager->DeactivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, language, CLSID_IsayTextService, GUID_IsayLanguageProfile, nullptr, TF_IPPMF_FORSESSION);
                Step(operation == L"--activate" ? "activate-speech-profile" : "deactivate-speech-profile", hr);
                if (hr != S_OK) throw E_FAIL;
            }
            Step("keyboard-profile-unchanged", SameKeyboard(before, KeyboardProfile(manager.Get())) ? S_OK : E_UNEXPECTED);
            Step("keyboard-layout-unchanged", beforeLayout == GetKeyboardLayout(0) ? S_OK : E_UNEXPECTED);
        }
        else if (operation == L"--uninstall-user")
        {
            // 只禁用并移除当前用户的入口；共享系统类别不随单个用户卸载。
            for (const auto language : Languages())
            {
                TF_INPUTPROCESSORPROFILE profile{};
                if (manager->GetProfile(TF_PROFILETYPE_INPUTPROCESSOR, language, CLSID_IsayTextService, GUID_IsayLanguageProfile, nullptr, &profile) == S_OK)
                    Step("disable-user-language-profile", profiles->EnableLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile, FALSE));
            }
            RemoveCom(KEY_WOW64_64KEY); RemoveCom(KEY_WOW64_32KEY);
        }
        else if (operation == L"--uninstall")
        {
            Step("unregister-display-category", categories->UnregisterCategory(CLSID_IsayTextService, GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER, CLSID_IsayTextService));
            Step("unregister-speech-category", categories->UnregisterCategory(CLSID_IsayTextService, GUID_TFCAT_TIP_SPEECH, CLSID_IsayTextService));
            Step("unregister-service", profiles->Unregister(CLSID_IsayTextService));
            RemoveCom(KEY_WOW64_64KEY); RemoveCom(KEY_WOW64_32KEY);
        }
        else if (operation == L"--status")
        {
            // GetActiveProfile 仅支持键盘类别；这里查询自家配置是否选中，不混称目标进程服务运行态。
            LANGID selectedLanguage = 0; GUID selectedProfile{};
            const auto selectedResult = profiles->GetActiveLanguageProfile(CLSID_IsayTextService, &selectedLanguage, &selectedProfile);
            for (const auto language : Languages())
            {
                BOOL enabled = FALSE;
                TF_INPUTPROCESSORPROFILE profile{};
                const auto hr = manager->GetProfile(TF_PROFILETYPE_INPUTPROCESSOR, language, CLSID_IsayTextService, GUID_IsayLanguageProfile, nullptr, &profile);
                const auto registered = hr == S_OK && profile.clsid == CLSID_IsayTextService && profile.guidProfile == GUID_IsayLanguageProfile;
                if (registered) profiles->IsEnabledLanguageProfile(CLSID_IsayTextService, language, GUID_IsayLanguageProfile, &enabled);
                const auto speech = registered && profile.catid == GUID_TFCAT_TIP_SPEECH;
                const auto selected = selectedResult == S_OK && selectedLanguage == language && selectedProfile == GUID_IsayLanguageProfile;
                std::cout << "{\"language\":" << language << ",\"registered\":" << (registered ? "true" : "false")
                    << ",\"enabled\":" << (enabled ? "true" : "false") << ",\"category\":\"" << GuidText(profile.catid)
                    << "\",\"speech\":" << (speech ? "true" : "false") << ",\"profileSelected\":" << (selected ? "true" : "false")
                    << ",\"selectionKnown\":" << (selectedResult == S_OK || selectedResult == S_FALSE ? "true" : "false")
                    << ",\"selectionHResult\":\"0x" << std::hex << std::setw(8) << std::setfill('0')
                    << static_cast<unsigned long>(selectedResult) << "\"}" << std::dec << std::endl;
            }
        }
        else throw E_INVALIDARG;
    }
    catch (HRESULT hr) { std::cerr << "{\"error\":\"0x" << std::hex << static_cast<unsigned long>(hr) << "\"}" << std::endl; exitCode = 1; }
    catch (const std::exception& error) { std::cerr << error.what() << std::endl; exitCode = 1; }
    CoUninitialize();
    return exitCode;
}
