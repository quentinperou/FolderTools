// FolderTools - "FolderTools ›" entry of the Windows 11 File Explorer context menu, on folders and the background of
// an open folder: "Folder sizes", "Delete empty folders", then, on folders, the folder colors (and "Default color").
//
// Explorer finds this COM class through the sparse package (package/AppxManifest.xml), loads the DLL in a
// surrogate process and calls IExplorerCommand. The first two sub-entries start FolderTools.exe, installed next
// to this DLL, as "FolderTools.exe size|clean <folder>", once per selected folder; the colors are applied here
// (FolderColor.cpp). The classic menu applies them through the SetColor export, with rundll32.

#include <windows.h>
#include <exdisp.h>
#include <shldisp.h>
#include <shlguid.h>
#include <shlobj_core.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>

#include <string>
#include <vector>

#include "FolderColor.h"
#include "resource.h"

using namespace Microsoft::WRL;

// Returns the HRESULT of a call that failed.
#define RETURN_IF_FAILED_HR(expression) \
    do \
    { \
        HRESULT failed_ = (expression); \
        if (FAILED(failed_)) \
            return failed_; \
    } while (false)

namespace
{
    HMODULE g_module = nullptr;

    // Guard against an accidental "select all" in a folder full of folders: one window per folder.
    constexpr DWORD MaxFoldersPerInvoke = 8;

    // A color costs no window: a whole selection can get one.
    constexpr DWORD MaxFoldersPerColor = 500;

    // Written by the app's settings (AppSettings): 1 hides the whole entry, or one sub-entry, from this menu.
    // Everything stays in the classic menu ("Show more options"), which is registered by the installer.
    constexpr wchar_t SettingsKey[] = L"Software\\FolderTools";
    constexpr wchar_t HideValue[] = L"HideInNewContextMenu";
    constexpr wchar_t HideSizeValue[] = L"HideSizeEntry";
    constexpr wchar_t HideCleanValue[] = L"HideCleanEntry";
    constexpr wchar_t HidePhotosValue[] = L"HidePhotosEntry";
    constexpr wchar_t HideFlattenValue[] = L"HideFlattenEntry";
    constexpr wchar_t HideColorValue[] = L"HideColorEntry";

    enum class Action
    {
        Size,
        Clean,
        Photos,
        Flatten,
    };

    // What each tool entry shows and starts: title, icon (resource id of this DLL), app verb, settings value.
    struct ActionInfo
    {
        UINT Title;
        PCWSTR Icon;
        PCWSTR Verb;
        PCWSTR HideValue;
    };

    const ActionInfo& InfoOf(Action action)
    {
        static const ActionInfo infos[] = {
            {IDS_MENU_SIZE, L",-201", L"size ", HideSizeValue},
            {IDS_MENU_CLEAN, L",-202", L"clean ", HideCleanValue},
            {IDS_MENU_PHOTOS, L",-204", L"photos ", HidePhotosValue},
            {IDS_MENU_FLATTEN, L",-205", L"flatten ", HideFlattenValue},
        };
        return infos[static_cast<int>(action)];
    }

    std::wstring LoadText(UINT id)
    {
        PCWSTR text = nullptr;
        int length = LoadStringW(g_module, id, reinterpret_cast<PWSTR>(&text), 0);
        return length > 0 ? std::wstring(text, length) : std::wstring(L"FolderTools");
    }

    std::wstring ModuleFolder()
    {
        std::wstring path(MAX_PATH, L'\0');
        for (;;)
        {
            DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
            if (length == 0)
                return {};
            if (length < path.size())
            {
                path.resize(length);
                break;
            }
            path.resize(path.size() * 2);
        }
        path.resize(path.find_last_of(L'\\') + 1);
        return path;
    }

    std::wstring AppPath() { return ModuleFolder() + L"FolderTools.exe"; }

    std::wstring ModulePath() { return ModuleFolder() + L"FolderToolsMenu.dll"; }

    bool HiddenBySettings(PCWSTR value = HideValue)
    {
        DWORD data = 0;
        DWORD size = sizeof(data);
        return RegGetValueW(HKEY_CURRENT_USER, SettingsKey, value, RRF_RT_REG_DWORD, nullptr, &data, &size) == ERROR_SUCCESS
            && data != 0;
    }

    // "C:\" must become "C:\\" inside quotes: otherwise \" reads as an escaped quote.
    std::wstring Quote(const std::wstring& path)
    {
        std::wstring quoted = L"\"" + path;
        if (!path.empty() && path.back() == L'\\')
            quoted += L'\\';
        return quoted + L"\"";
    }

    // File system folders of the selection: none for "This PC", Control Panel or a library root.
    std::vector<std::wstring> FolderPaths(IShellItemArray* items, DWORD max = MaxFoldersPerInvoke)
    {
        std::vector<std::wstring> paths;
        DWORD count = 0;
        if (!items || FAILED(items->GetCount(&count)))
            return paths;
        for (DWORD i = 0; i < count && paths.size() < max; ++i)
        {
            ComPtr<IShellItem> item;
            SFGAOF attributes = 0;
            PWSTR path = nullptr;
            if (SUCCEEDED(items->GetItemAt(i, &item))
                && SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, &attributes))
                && (attributes & (SFGAO_FILESYSTEM | SFGAO_FOLDER)) == (SFGAO_FILESYSTEM | SFGAO_FOLDER)
                && SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            {
                paths.emplace_back(path);
                CoTaskMemFree(path);
            }
        }
        return paths;
    }

    // On the background of an open folder, the selection may be empty: the folder is the one the window shows.
    std::wstring FolderFromSite(IUnknown* site)
    {
        ComPtr<IShellBrowser> browser;
        ComPtr<IShellView> view;
        ComPtr<IFolderView> folderView;
        ComPtr<IShellItem> folder;
        PWSTR path = nullptr;
        if (!site
            || FAILED(IUnknown_QueryService(site, SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))
            || FAILED(browser->QueryActiveShellView(&view))
            || FAILED(view.As(&folderView))
            || FAILED(folderView->GetFolder(IID_PPV_ARGS(&folder)))
            || FAILED(folder->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            return {};
        std::wstring result(path);
        CoTaskMemFree(path);
        return result;
    }

    // Asks Explorer to start the app (Raymond Chen's "launch from the desktop" technique). The app then
    // belongs to explorer.exe instead of this surrogate: otherwise it would inherit the package identity,
    // because FolderTools.exe sits in the package's external location, and Windows would show the package
    // logo (a blank tile) in the taskbar. Started by Explorer, the window may also take the foreground.
    bool LaunchThroughExplorer(const std::wstring& app, const std::wstring& arguments)
    {
        ComPtr<IShellWindows> windows;
        if (FAILED(CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows))))
            return false;

        VARIANT empty{};
        long hwnd = 0;
        ComPtr<IDispatch> desktop;
        if (windows->FindWindowSW(&empty, &empty, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, &desktop) != S_OK || !desktop)
            return false;

        ComPtr<IServiceProvider> provider;
        ComPtr<IShellBrowser> browser;
        ComPtr<IShellView> view;
        ComPtr<IDispatch> background;
        ComPtr<IShellFolderViewDual> folderView;
        ComPtr<IDispatch> application;
        ComPtr<IShellDispatch2> shell;
        if (FAILED(desktop.As(&provider)) ||
            FAILED(provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser))) ||
            FAILED(browser->QueryActiveShellView(&view)) ||
            FAILED(view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&background))) ||
            FAILED(background.As(&folderView)) ||
            FAILED(folderView->get_Application(&application)) ||
            FAILED(application.As(&shell)))
            return false;

        BSTR file = SysAllocString(app.c_str());
        VARIANT args{};
        args.vt = VT_BSTR;
        args.bstrVal = SysAllocString(arguments.c_str());
        VARIANT directory{};
        VARIANT operation{};
        VARIANT show{};
        show.vt = VT_I4;
        show.lVal = SW_SHOWNORMAL;
        HRESULT hr = shell->ShellExecute(file, args, directory, operation, show);
        SysFreeString(file);
        VariantClear(&args);
        return SUCCEEDED(hr);
    }

    // Fallback when Explorer cannot be reached: start the app ourselves (it then keeps the package identity).
    void LaunchDirectly(const std::wstring& app, const std::wstring& arguments)
    {
        std::wstring commandLine = Quote(app) + L" " + arguments;
        // The surrogate process hosting this DLL is started hidden: without an explicit show
        // command the app window would inherit that state and open minimized.
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_SHOWNORMAL;
        PROCESS_INFORMATION process{};
        if (CreateProcessW(app.c_str(), commandLine.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
        {
            AllowSetForegroundWindow(process.dwProcessId);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
    }

    void Launch(Action action, const std::wstring& folder)
    {
        std::wstring app = AppPath();
        std::wstring arguments = InfoOf(action).Verb + Quote(folder);
        if (!LaunchThroughExplorer(app, arguments))
            LaunchDirectly(app, arguments);
    }

    // What every command of this menu shares: no tooltip, keeps the site Explorer gives.
    template <typename T>
    class CommandBase : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
    {
    public:
        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
        {
            *tip = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP SetSite(IUnknown* site) override
        {
            m_site = site;
            return S_OK;
        }

        IFACEMETHODIMP GetSite(REFIID riid, void** site) override
        {
            if (!m_site)
            {
                *site = nullptr;
                return E_FAIL;
            }
            return m_site.CopyTo(riid, site);
        }

    protected:
        ComPtr<IUnknown> m_site;
    };

    // "Folder sizes", "Delete empty folders", "Sort photos by date" or "Flatten folder".
    class SubCommand final : public CommandBase<SubCommand>
    {
    public:
        HRESULT RuntimeClassInitialize(Action action, IUnknown* site)
        {
            m_action = action;
            m_site = site;
            return S_OK;
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
        {
            return SHStrDupW(LoadText(InfoOf(m_action).Title).c_str(), name);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            std::wstring resource = ModulePath() + InfoOf(m_action).Icon;
            return SHStrDupW(resource.c_str(), icon);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* guid) override
        {
            *guid = GUID_NULL;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
        {
            *state = HiddenBySettings(InfoOf(m_action).HideValue) ? ECS_HIDDEN : ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            *flags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            *commands = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            auto folders = FolderPaths(items);
            if (folders.empty())
            {
                std::wstring folder = FolderFromSite(m_site.Get());
                if (!folder.empty())
                    folders.push_back(folder);
            }
            for (const auto& folder : folders)
                Launch(m_action, folder);
            return S_OK;
        }

    private:
        Action m_action = Action::Size;
    };

    // The sub-entries of a menu, as File Explorer reads them.
    class CommandList final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
    {
    public:
        HRESULT RuntimeClassInitialize(std::vector<ComPtr<IExplorerCommand>> commands)
        {
            m_commands = std::move(commands);
            return S_OK;
        }

        IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override
        {
            ULONG done = 0;
            for (; done < count && m_index < m_commands.size(); ++done, ++m_index)
                m_commands[m_index].CopyTo(&commands[done]);
            if (fetched)
                *fetched = done;
            return done == count ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Skip(ULONG count) override
        {
            m_index = (std::min)(m_index + count, m_commands.size());
            return m_index < m_commands.size() ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Reset() override
        {
            m_index = 0;
            return S_OK;
        }

        IFACEMETHODIMP Clone(IEnumExplorerCommand** clone) override
        {
            *clone = nullptr;
            return E_NOTIMPL;
        }

    private:
        std::vector<ComPtr<IExplorerCommand>> m_commands;
        size_t m_index = 0;
    };

    // A folder color (or Default) of "FolderTools ›", checked when every selected folder has it.
    class ColorCommand final : public CommandBase<ColorCommand>
    {
    public:
        HRESULT RuntimeClassInitialize(int color)
        {
            m_color = color;
            return S_OK;
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
        {
            UINT id = m_color == FolderColor::Default ? IDS_COLOR_DEFAULT : FolderColor::Colors[m_color].Title;
            return SHStrDupW(LoadText(id).c_str(), name);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            if (m_color != FolderColor::Default)
                return SHStrDupW(FolderColor::IconPath(m_color).c_str(), icon);
            // The ordinary folder icon of Windows.
            wchar_t system[MAX_PATH];
            UINT length = GetSystemDirectoryW(system, MAX_PATH);
            std::wstring resource = std::wstring(system, length) + L"\\imageres.dll,-3";
            return SHStrDupW(resource.c_str(), icon);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* guid) override
        {
            *guid = GUID_NULL;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
        {
            // Hidden by the user, on the background of an open folder (no items) and on drives, which have their own
            // icon. Checked when every selected folder has this color.
            auto folders = FolderPaths(items, MaxFoldersPerColor);
            bool hidden = HiddenBySettings(HideColorValue) || folders.empty();
            bool all = !folders.empty();
            for (const auto& folder : folders)
            {
                hidden = hidden || PathIsRootW(folder.c_str());
                all = all && FolderColor::Current(folder) == m_color;
            }
            *state = hidden ? ECS_HIDDEN : all ? ECS_CHECKED : ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            // A separator between the tools and the colors.
            *flags = m_color == 0 ? ECF_SEPARATORBEFORE : ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            *commands = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            for (const auto& folder : FolderPaths(items, MaxFoldersPerColor))
                FolderColor::Apply(folder, m_color);
            return S_OK;
        }

    private:
        int m_color = FolderColor::Default;
    };

}

// "FolderTools ›": the entry File Explorer creates from the package manifest.
class __declspec(uuid("8119219C-28DF-42B9-B5B3-9B2904535DDC")) FolderToolsCommand final
    : public CommandBase<FolderToolsCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
    {
        return SHStrDupW(LoadText(IDS_MENU_TITLE).c_str(), name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        std::wstring resource = AppPath() + L",0";
        return SHStrDupW(resource.c_str(), icon);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        *guid = __uuidof(FolderToolsCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        DWORD count = 0;
        bool hasItems = items && SUCCEEDED(items->GetCount(&count)) && count > 0;
        // Hidden by the user (the whole menu, or all its sub-entries), or on items that are not folders of the file
        // system (This PC, Control Panel...).
        bool allHidden = HiddenBySettings(HideSizeValue) && HiddenBySettings(HideCleanValue)
            && HiddenBySettings(HidePhotosValue) && HiddenBySettings(HideFlattenValue)
            && (HiddenBySettings(HideColorValue) || !hasItems);
        *state = HiddenBySettings() || allHidden || (hasItems && FolderPaths(items).empty()) ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_HASSUBCOMMANDS;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        // The sub-entries need the site too, to find the open folder when clicked on its background.
        ComPtr<IExplorerCommand> size;
        ComPtr<IExplorerCommand> clean;
        ComPtr<IExplorerCommand> photos;
        ComPtr<IExplorerCommand> flatten;
        RETURN_IF_FAILED_HR(MakeAndInitialize<SubCommand>(&size, Action::Size, m_site.Get()));
        RETURN_IF_FAILED_HR(MakeAndInitialize<SubCommand>(&clean, Action::Clean, m_site.Get()));
        RETURN_IF_FAILED_HR(MakeAndInitialize<SubCommand>(&photos, Action::Photos, m_site.Get()));
        RETURN_IF_FAILED_HR(MakeAndInitialize<SubCommand>(&flatten, Action::Flatten, m_site.Get()));
        std::vector<ComPtr<IExplorerCommand>> list{size, clean, photos, flatten};
        // The colors right in this sub-menu: File Explorer does not show a sub-menu inside a sub-menu (remarks of
        // IExplorerCommand::EnumSubCommands), and a second entry of the package, or a second package, gets gathered
        // with this one in an icon-less "FolderTools ›" flyout made by Windows.
        for (int color = 0; color < FolderColor::Count; ++color)
        {
            ComPtr<IExplorerCommand> command;
            RETURN_IF_FAILED_HR(MakeAndInitialize<ColorCommand>(&command, color));
            list.push_back(command);
        }
        ComPtr<IExplorerCommand> reset;
        RETURN_IF_FAILED_HR(MakeAndInitialize<ColorCommand>(&reset, FolderColor::Default));
        list.push_back(reset);
        return MakeAndInitialize<CommandList>(commands, std::move(list));
    }

    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override
    {
        return S_OK; // only opens the sub-menu
    }
};

CoCreatableClass(FolderToolsCommand)

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** instance)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, instance);
}

// Classic menu ("Show more options", Windows 10): rundll32.exe "<app>\FolderToolsMenu.dll",SetColor red "<folder>"
// (or "default"). rundll32 calls this Unicode version for "SetColor".
extern "C" void CALLBACK SetColorW(HWND, HINSTANCE, LPWSTR commandLine, int)
{
    int count = 0;
    LPWSTR* args = CommandLineToArgvW(commandLine, &count);
    if (!args)
        return;
    int color = count >= 2 ? FolderColor::Parse(args[0]) : -2;
    if (color != -2)
    {
        HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
        for (int i = 1; i < count; ++i)
            if (!PathIsRootW(args[i]))
                FolderColor::Apply(args[i], color);
        if (SUCCEEDED(com))
            CoUninitialize();
    }
    LocalFree(args);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        FolderColor::Initialize(ModuleFolder());
    }
    return TRUE;
}
