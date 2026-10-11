#pragma once

// Folder colors: a colored folder is a folder whose desktop.ini points at one of the icons installed in
// <app>\Colors (IconResource), like Properties > Customize > Change Icon does.

#include <windows.h>

#include <string>

namespace FolderColor
{
    struct Color
    {
        PCWSTR Name;  // file name in <app>\Colors, and name on the rundll32 command line (case ignored)
        UINT Title;   // menu label (string table)
    };

    // In the order of the menu. Same names and order as scripts\make-icons.ps1 and installer\FolderTools.iss.
    extern const Color Colors[];
    constexpr int Count = 8;

    // "No color": the folder's own icon.
    constexpr int Default = -1;

    // Set once by DllMain: the folder of FolderToolsMenu.dll, with its trailing backslash.
    void Initialize(const std::wstring& moduleFolder);

    std::wstring IconPath(int color);

    // The color of a folder, Default when it has none of ours (no icon, or an icon set by someone else).
    int Current(const std::wstring& folder);

    // Gives the folder a color, or takes ours away (Default). Another icon set by the user or Windows (Downloads,
    // Documents...) is kept aside when a color is applied, and comes back with Default.
    bool Apply(const std::wstring& folder, int color);

    // "red", "Blue"... or "default"; -2 when unknown.
    int Parse(PCWSTR name);
}
