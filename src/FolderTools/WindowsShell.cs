using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using FolderTools.Core;

namespace FolderTools;

/// <summary>What the app asks of Windows: recycle bin, File Explorer, administrator rights.</summary>
public static class WindowsShell
{
    private const int ErrorCancelled = 1223; // UAC prompt declined

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Starts the app again as administrator, on the same folder. Returns false if the UAC prompt was declined.
    /// </summary>
    public static bool RestartAsAdministrator(LaunchRequest request)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, request.ToArguments())
            {
                UseShellExecute = true,
                Verb = "runas",
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    public static void OpenInExplorer(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", LaunchRequest.QuotePath(folder)) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Sends one folder to the recycle bin, with File Explorer's own progress and error dialogs (a big folder may
    /// take a while; a locked file is explained by Windows). Windows asks before deleting for good when the drive
    /// has no recycle bin. Returns true when the folder is gone.
    /// </summary>
    public static bool RecycleWithDialogs(string folder, Window owner)
    {
        Recycle([folder], new WindowInteropHelper(owner).Handle,
            FileOperationFlags.AllowUndo | FileOperationFlags.NoConfirmation | FileOperationFlags.WantNukeWarning);
        return !System.IO.Directory.Exists(folder);
    }

    /// <summary>
    /// Sends folders to the recycle bin without any dialog: for empty folders, which hold nothing to lose (on a
    /// drive without recycle bin they are simply deleted). Callers check afterwards which ones are gone.
    /// </summary>
    public static void RecycleSilently(IReadOnlyList<string> folders) =>
        Recycle(folders, IntPtr.Zero,
            FileOperationFlags.AllowUndo | FileOperationFlags.NoConfirmation | FileOperationFlags.Silent | FileOperationFlags.NoErrorUI);

    private static void Recycle(IReadOnlyList<string> paths, IntPtr owner, FileOperationFlags flags)
    {
        if (paths.Count == 0)
            return;
        // Several paths in one call: each ends with a NUL, the list with a second one.
        var operation = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)flags,
        };
        // The shell file operations expect a single-threaded apartment.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            SHFileOperation(ref operation);
        else
            RunOnSta(() => SHFileOperation(ref operation));
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    /// <summary>Unit names in the UI language, for <see cref="SizeFormat"/>.</summary>
    public static string[] SizeUnits => Strings.Get("SizeUnits").Split(',');

    public static string FormatSize(long bytes) => SizeFormat.Format(bytes, SizeUnits, CultureInfo.CurrentCulture);

    public static string FormatCount(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    // ---------------------------------------------------------------- Win32

    private const uint FO_DELETE = 3;

    [Flags]
    private enum FileOperationFlags : ushort
    {
        Silent = 0x0004,
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoErrorUI = 0x0400,
        WantNukeWarning = 0x4000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);
}
