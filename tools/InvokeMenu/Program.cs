using System.Runtime.InteropServices;

// Calls the installed "FolderTools ›" context menu (FolderToolsMenu.dll, through its COM class) like File Explorer
// does: title, icon, state and flags of the entry, then its sub-entries, then Invoke on the chosen one.
// Useful to test the DLL without clicking in File Explorer. FolderTools must be installed.
//
//   dotnet run --project tools\InvokeMenu -- C:\temp\folder            (lists, invokes nothing)
//   dotnet run --project tools\InvokeMenu -- C:\temp\folder size|clean
//
// Then check the started FolderTools.exe: its parent must be explorer.exe and it must have no package identity.

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: InvokeMenu <folder> [size|clean]");
    return 1;
}

return Program.Run(Path.GetFullPath(args[0]), args.Length == 2 ? args[1] : null);

static partial class Program
{
    // Same CLSID as src/FolderTools.Shell/dllmain.cpp and package/AppxManifest.xml.
    private static readonly Guid CommandClsid = new("8119219C-28DF-42B9-B5B3-9B2904535DDC");

    public static int Run(string folder, string? action)
    {
        var type = Type.GetTypeFromCLSID(CommandClsid)!;
        var command = (IExplorerCommand)Activator.CreateInstance(type)!;

        Guid shellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        Guid itemArrayId = new("b63ea76d-1f85-456f-a19c-48159efa858b");
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(folder, IntPtr.Zero, ref shellItemId, out var item));
        Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, ref itemArrayId, out var items));

        Describe("Entry", command, items);
        command.GetFlags(out uint flags);
        Console.WriteLine($"  Flags: {flags} (1 = has sub-entries)");

        Marshal.ThrowExceptionForHR(command.EnumSubCommands(out var enumPointer));
        var subCommands = (IEnumExplorerCommand)Marshal.GetObjectForIUnknown(enumPointer);
        var found = new List<IExplorerCommand>();
        while (subCommands.Next(1, out var sub, out uint fetched) == 0 && fetched == 1)
            found.Add(sub);
        for (int i = 0; i < found.Count; i++)
            Describe($"Sub-entry {i + 1}", found[i], items);

        if (action is null)
            return 0;
        var chosen = found[action.Equals("clean", StringComparison.OrdinalIgnoreCase) ? 1 : 0];
        int hr = chosen.Invoke(items, IntPtr.Zero);
        Console.WriteLine($"Invoke {action}: 0x{hr:X8}");
        return hr == 0 ? 0 : 1;
    }

    private static void Describe(string label, IExplorerCommand command, IntPtr items)
    {
        command.GetTitle(items, out var title);
        command.GetIcon(items, out var icon);
        command.GetState(items, false, out var state);
        Console.WriteLine($"{label}: {title}");
        Console.WriteLine($"  Icon:  {icon}");
        Console.WriteLine($"  State: {state} (0 = enabled, 2 = hidden)");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromShellItem(IntPtr item, ref Guid riid, out IntPtr array);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
    [PreserveSig] int GetIcon(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
    [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
    [PreserveSig] int GetCanonicalName(out Guid guid);
    [PreserveSig] int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
    [PreserveSig] int Invoke(IntPtr items, IntPtr bindContext);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out IntPtr commands);
}

[ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint count, out IExplorerCommand command, out uint fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IntPtr clone);
}
