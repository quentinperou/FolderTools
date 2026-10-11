#include "FolderColor.h"

#include <shlobj_core.h>
#include <shlwapi.h>

#include <vector>

#include "resource.h"

namespace FolderColor
{
    const Color Colors[] = {
        {L"Red", IDS_COLOR_RED},       {L"Orange", IDS_COLOR_ORANGE}, {L"Yellow", IDS_COLOR_YELLOW},
        {L"Green", IDS_COLOR_GREEN},   {L"Blue", IDS_COLOR_BLUE},     {L"Purple", IDS_COLOR_PURPLE},
        {L"Pink", IDS_COLOR_PINK},     {L"Gray", IDS_COLOR_GRAY},
    };

    namespace
    {
        std::wstring g_moduleFolder;

        constexpr wchar_t ShellSection[] = L".ShellClassInfo";
        constexpr wchar_t IconKey[] = L"IconResource";
        // Our own section: the icon the folder had before its color, put back by Default.
        constexpr wchar_t OwnSection[] = L"FolderTools";
        constexpr wchar_t PreviousKey[] = L"PreviousIconResource";

        std::wstring IniPath(const std::wstring& folder)
        {
            return folder + (!folder.empty() && folder.back() == L'\\' ? L"" : L"\\") + L"desktop.ini";
        }

        std::wstring ReadIni(const std::wstring& ini, PCWSTR section, PCWSTR key)
        {
            std::vector<wchar_t> value(2048);
            DWORD length = GetPrivateProfileStringW(section, key, L"", value.data(), static_cast<DWORD>(value.size()), ini.c_str());
            return std::wstring(value.data(), length);
        }

        // desktop.ini is hidden and system: some writes fail on such a file, so it is made normal for the time of
        // the write and gets its attributes back.
        bool WriteIni(const std::wstring& ini, PCWSTR section, PCWSTR key, PCWSTR value)
        {
            DWORD attributes = GetFileAttributesW(ini.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES)
                SetFileAttributesW(ini.c_str(), FILE_ATTRIBUTE_NORMAL);
            BOOL ok = WritePrivateProfileStringW(section, key, value, ini.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES)
                SetFileAttributesW(ini.c_str(), attributes);
            return ok != FALSE;
        }

        // The icon path without its ",index", environment variables expanded.
        std::wstring IconFile(const std::wstring& resource)
        {
            std::wstring file = resource;
            size_t comma = file.find_last_of(L',');
            if (comma != std::wstring::npos)
                file.resize(comma);
            std::vector<wchar_t> expanded(4096);
            DWORD length = ExpandEnvironmentStringsW(file.c_str(), expanded.data(), static_cast<DWORD>(expanded.size()));
            return length > 0 && length <= expanded.size() ? std::wstring(expanded.data()) : file;
        }

        // "%ProgramFiles%\FolderTools\Colors\Red.ico" rather than "C:\Program Files\...": a folder colored on one PC
        // keeps its color on another one where FolderTools is installed (USB drive, synced folder).
        std::wstring Unexpanded(const std::wstring& path)
        {
            wchar_t shorter[MAX_PATH];
            return PathUnExpandEnvStringsW(path.c_str(), shorter, MAX_PATH) ? std::wstring(shorter) : path;
        }

        bool IniIsEmpty(const std::wstring& ini)
        {
            std::vector<wchar_t> sections(4096);
            DWORD length = GetPrivateProfileSectionNamesW(sections.data(), static_cast<DWORD>(sections.size()), ini.c_str());
            for (PCWSTR section = sections.data(); section < sections.data() + length && *section; section += wcslen(section) + 1)
            {
                std::vector<wchar_t> content(4096);
                if (GetPrivateProfileSectionW(section, content.data(), static_cast<DWORD>(content.size()), ini.c_str()) > 0)
                    return false;
            }
            return true;
        }

        void Notify(const std::wstring& folder)
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, folder.c_str(), nullptr);
        }
    }

    void Initialize(const std::wstring& moduleFolder) { g_moduleFolder = moduleFolder; }

    std::wstring IconPath(int color) { return g_moduleFolder + L"Colors\\" + Colors[color].Name + L".ico"; }

    int Current(const std::wstring& folder)
    {
        std::wstring resource = ReadIni(IniPath(folder), ShellSection, IconKey);
        if (resource.empty())
            return Default;
        std::wstring file = IconFile(resource);
        for (int color = 0; color < Count; ++color)
            if (_wcsicmp(file.c_str(), IconPath(color).c_str()) == 0)
                return color;
        return Default;
    }

    bool Apply(const std::wstring& folder, int color)
    {
        std::wstring ini = IniPath(folder);
        int current = Current(folder);
        if (color == current)
            return true;

        if (color != Default)
        {
            // Keep the folder's own icon (Downloads, Documents...) aside, once, to give it back with Default.
            std::wstring own = ReadIni(ini, ShellSection, IconKey);
            if (current == Default && !own.empty())
                WriteIni(ini, OwnSection, PreviousKey, own.c_str());

            std::wstring icon = Unexpanded(IconPath(color));
            SHFOLDERCUSTOMSETTINGS settings{};
            settings.dwSize = sizeof(settings);
            settings.dwMask = FCSM_ICONFILE;
            settings.pszIconFile = icon.data();
            settings.iIconIndex = 0;
            // Writes IconResource, marks desktop.ini hidden + system and the folder read-only (Explorer only reads
            // the desktop.ini of such folders).
            bool ok = SUCCEEDED(SHGetSetFolderCustomSettings(&settings, folder.c_str(), FCS_FORCEWRITE));
            Notify(folder);
            return ok;
        }

        // Default: our color goes, the previous icon comes back. The API cannot remove the entry cleanly (it leaves
        // "IconResource=,0"), so the file is edited directly; other settings of the folder are left untouched.
        std::wstring previous = ReadIni(ini, OwnSection, PreviousKey);
        bool ok = WriteIni(ini, ShellSection, IconKey, previous.empty() ? nullptr : previous.c_str());
        WriteIni(ini, OwnSection, nullptr, nullptr);
        if (IniIsEmpty(ini))
        {
            // Nothing left: no desktop.ini, and the folder is an ordinary one again.
            SetFileAttributesW(ini.c_str(), FILE_ATTRIBUTE_NORMAL);
            DeleteFileW(ini.c_str());
            PathUnmakeSystemFolderW(folder.c_str());
        }
        Notify(folder);
        return ok;
    }

    int Parse(PCWSTR name)
    {
        if (_wcsicmp(name, L"default") == 0)
            return Default;
        for (int color = 0; color < Count; ++color)
            if (_wcsicmp(name, Colors[color].Name) == 0)
                return color;
        return -2;
    }
}
