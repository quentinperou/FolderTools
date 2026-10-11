# Windows 11 context menu handler (IExplorerCommand) for FolderTools: "FolderTools >" and its two sub-entries.
# Built by scripts/build.ps1; can also be opened directly in Visual Studio ("Open folder").
cmake_minimum_required(VERSION 3.21)
project(FolderToolsMenu LANGUAGES CXX RC)

# Static CRT: the DLL must not depend on the Visual C++ redistributable being installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(FolderToolsMenu SHARED dllmain.cpp FolderColor.cpp FolderToolsMenu.def FolderToolsMenu.rc)
target_compile_features(FolderToolsMenu PRIVATE cxx_std_20)
# No WIN32_LEAN_AND_MEAN: exdisp.h (IShellWindows) needs the OLE declarations it strips.
target_compile_definitions(FolderToolsMenu PRIVATE UNICODE _UNICODE NOMINMAX)
# C4324 (structure padded due to alignment) comes from the WRL headers themselves.
target_compile_options(FolderToolsMenu PRIVATE /W4 /WX /wd4324 /permissive- /EHsc /utf-8)
target_link_libraries(FolderToolsMenu PRIVATE runtimeobject shell32 shlwapi ole32 oleaut32 uuid)
