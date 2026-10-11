// FolderTools - "FolderTools ›" entry of the Windows 11 File Explorer context menu, on folders, drives and
// the background of an open folder, with two sub-entries: "Folder sizes" and "Delete empty folders".
//
// Explorer finds this COM class through the sparse package (package/AppxManifest.xml), loads the DLL in a
// surrogate process and calls IExplorerCommand. A sub-entry starts FolderTools.exe, installed next to this
// DLL, as "FolderTools.exe size|clean <folder>", once per selected folder.

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

#include "resource.h"

using namespace Microsoft::WRL;

namespace
{
    HMODULE g_module = nullptr;

    // Guard against an accidental "select all" in a folder full of folders.
    constexpr DWORD MaxFoldersPerInvoke = 8;

    // Written by the app's settings (AppSettings.ShowInNewContextMenu): 1 hides the entry from this menu.
    // It stays in the classic menu ("Show more options"), which is registered by the installer.
    constexpr wchar_t SettingsKey[] = L"Software\\FolderTools";
    constexpr wchar_t HideValue[] = L"HideInNewContextMenu";

    enum class Action
    {
        Size,
        Clean,
    };

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

    bool HiddenBySettings()
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        return RegGetValueW(HKEY_CURRENT_USER, SettingsKey, HideValue, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS
            && value != 0;
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
    std::vector<std::wstring> FolderPaths(IShellItemArray* items)
    {
        std::vector<std::wstring> paths;
        DWORD count = 0;
        if (!items || FAILED(items->GetCount(&count)))
            return paths;
        for (DWORD i = 0; i < count && paths.size() < MaxFoldersPerInvoke; ++i)
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
        std::wstring arguments = std::wstring(action == Action::Size ? L"size " : L"clean ") + Quote(folder);
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

    // "Folder sizes" or "Delete empty folders".
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
            return SHStrDupW(LoadText(m_action == Action::Size ? IDS_MENU_SIZE : IDS_MENU_CLEAN).c_str(), name);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            std::wstring resource = ModulePath() + (m_action == Action::Size ? L",-201" : L",-202");
            return SHStrDupW(resource.c_str(), icon);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* guid) override
        {
            *guid = GUID_NULL;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
        {
            *state = ECS_ENABLED;
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

    class SubCommands final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
    {
    public:
        HRESULT RuntimeClassInitialize(IUnknown* site)
        {
            ComPtr<IExplorerCommand> size;
            ComPtr<IExplorerCommand> clean;
            HRESULT hr = MakeAndInitialize<SubCommand>(&size, Action::Size, site);
            if (SUCCEEDED(hr))
                hr = MakeAndInitialize<SubCommand>(&clean, Action::Clean, site);
            if (FAILED(hr))
                return hr;
            m_commands.push_back(size);
            m_commands.push_back(clean);
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
        // Hidden by the user, or on items that are not folders of the file system (This PC, Control Panel...).
        *state = HiddenBySettings() || (hasItems && FolderPaths(items).empty()) ? ECS_HIDDEN : ECS_ENABLED;
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
        return MakeAndInitialize<SubCommands>(commands, m_site.Get());
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

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
