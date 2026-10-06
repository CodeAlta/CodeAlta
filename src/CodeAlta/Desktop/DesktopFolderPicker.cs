using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;

namespace CodeAlta.Desktop;

/// <summary>How a folder pick ended.</summary>
internal enum DesktopFolderPickStatus
{
    /// <summary>The user chose a folder.</summary>
    Ok,

    /// <summary>The user closed the dialog without choosing.</summary>
    Canceled,

    /// <summary>This system has no folder dialog.</summary>
    Unavailable,

    /// <summary>The dialog could not be shown, or what it returned is not an existing folder.</summary>
    Failed,
}

/// <param name="Status">How the pick ended.</param>
/// <param name="Path">The full path of the chosen folder; null unless <see cref="DesktopFolderPickStatus.Ok"/>.</param>
internal readonly record struct DesktopFolderPick(DesktopFolderPickStatus Status, string? Path = null);

/// <summary>
/// Lets the user choose a folder with the dialog of the operating system.
/// </summary>
/// <remarks>
/// On Windows this is the Explorer file dialog in folder mode: it has the address bar, the search box and a
/// field a path can be pasted into, unlike the folder tree of <c>SHBrowseForFolder</c> that NeoAstra shows.
/// That tree is still the fallback when the Explorer dialog cannot be created. On macOS and Linux NeoAstra's
/// folder dialog is the system one.
/// </remarks>
internal static class DesktopFolderPicker
{
    // Windows remembers the last folder of a dialog by this identity: the picker opens where it was last used.
    private static readonly Guid DialogIdentity = new("6f0f4c0e-9d0b-4a5e-8a6f-2b0c1d9e7a41");
    private static readonly Guid ShellItemIdentity = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const uint PickFolders = 0x20;       // FOS_PICKFOLDERS
    private const uint ForceFileSystem = 0x40;   // FOS_FORCEFILESYSTEM
    private const uint NoChangeDirectory = 0x8;  // FOS_NOCHANGEDIR
    private const uint PathMustExist = 0x800;    // FOS_PATHMUSTEXIST
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
    private const int Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    /// <summary>Shows the dialog over a window and returns the chosen folder.</summary>
    /// <param name="window">The window the dialog belongs to.</param>
    /// <param name="dispatcher">The dispatcher of the window's thread.</param>
    /// <param name="dialogs">NeoAstra's dialogs, used where they are the system dialog and as the Windows fallback.</param>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="initialDirectory">The folder shown first; ignored unless it is an existing absolute folder.</param>
    /// <param name="cancellationToken">Cancels the wait where the platform dialog can be canceled.</param>
    /// <returns>The pick; this method does not throw for a dialog that fails.</returns>
    internal static async Task<DesktopFolderPick> PickAsync(NeoWindow window, NeoDispatcher dispatcher, INeoDialogs? dialogs, string title,
        string? initialDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var initial = ExistingDirectory(initialDirectory);
        if (OperatingSystem.IsWindows())
        {
            var pick = await ShowExplorerDialogAsync(window, dispatcher, title, initial).ConfigureAwait(false);
            if (pick.Status != DesktopFolderPickStatus.Unavailable) return pick;
        }

        return dialogs is null ? new(DesktopFolderPickStatus.Unavailable) : await ShowNeoAstraDialogAsync(window, dialogs, title, initial, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps a path only when it names an existing folder by its full path.</summary>
    internal static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0')) return null;
        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed) && Directory.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Shows NeoAstra's folder dialog and reads its answer: one existing folder, or why there is none.</summary>
    internal static async Task<DesktopFolderPick> ShowNeoAstraDialogAsync(NeoWindow? window, INeoDialogs dialogs, string title, string? initial, CancellationToken cancellationToken)
    {
        try
        {
            // The dialog may return any folder of the machine: the scope is every root.
            string[] roots = OperatingSystem.IsWindows()
                ? [.. DriveInfo.GetDrives().Where(static drive => drive.IsReady).Select(static drive => drive.RootDirectory.FullName)]
                : ["/"];
            if (roots.Length == 0) return new(DesktopFolderPickStatus.Unavailable);
            var result = await dialogs.OpenFoldersAsync(new NeoFileDialogOptions
            {
                Owner = window, Title = title, InitialDirectory = initial, Scope = new NeoFileScope(roots),
            }, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                NeoDesktopStatus.Success when result.Value is [var path, ..] && ExistingDirectory(path) is { } folder => new(DesktopFolderPickStatus.Ok, folder),
                NeoDesktopStatus.Canceled => new(DesktopFolderPickStatus.Canceled),
                NeoDesktopStatus.Unsupported => new(DesktopFolderPickStatus.Unavailable),
                _ => new(DesktopFolderPickStatus.Failed),
            };
        }
        catch (OperationCanceledException)
        {
            return new(DesktopFolderPickStatus.Canceled);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(DesktopFolderPickStatus.Failed);
        }
    }

    // The dialog is modal to the window and runs its own message loop on the window's thread.
    [SupportedOSPlatform("windows")]
    private static Task<DesktopFolderPick> ShowExplorerDialogAsync(NeoWindow window, NeoDispatcher dispatcher, string title, string? initial)
    {
        var shown = new TaskCompletionSource<DesktopFolderPick>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (dispatcher.CheckAccess()) Show();
        else dispatcher.Post(Show);
        return shown.Task;

        [SupportedOSPlatform("windows")]
        void Show()
        {
            try
            {
                shown.TrySetResult(window.IsClosed ? new(DesktopFolderPickStatus.Canceled) : ShowExplorerDialog(window, title, initial));
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException
                or InvalidOperationException or NotSupportedException or PlatformNotSupportedException or MarshalDirectiveException)
            {
                shown.TrySetResult(new(DesktopFolderPickStatus.Unavailable));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static DesktopFolderPick ShowExplorerDialog(NeoWindow window, string title, string? initial)
    {
        var owner = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
        var dialog = (IFileDialog)new FileOpenDialog();
        IShellItem? folder = null, chosen = null;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | ForceFileSystem | NoChangeDirectory | PathMustExist);
            dialog.SetClientGuid(DialogIdentity);
            dialog.SetTitle(title);
            // A folder the caller names is shown now; without one the dialog opens where it was last used.
            if (initial is not null && SHCreateItemFromParsingName(initial, 0, ShellItemIdentity, out folder) == 0 && folder is not null) dialog.SetFolder(folder);
            var shown = dialog.Show(owner);
            if (shown == Cancelled) return new(DesktopFolderPickStatus.Canceled);
            if (shown != 0) return new(DesktopFolderPickStatus.Failed);
            dialog.GetResult(out chosen);
            if (chosen.GetDisplayName(FileSystemPath, out var text) != 0 || text == 0) return new(DesktopFolderPickStatus.Failed);
            try
            {
                return ExistingDirectory(Marshal.PtrToStringUni(text)) is { } path ? new(DesktopFolderPickStatus.Ok, path) : new(DesktopFolderPickStatus.Failed);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        finally
        {
            if (chosen is not null) Marshal.ReleaseComObject(chosen);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceIdentity, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialog;

    // IFileDialog, with IModalWindow's Show first: the methods are in the order of the interface, and the ones
    // after SetClientGuid are not declared because nothing here calls them.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, nint types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(in Guid identity);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid interfaceIdentity, out nint value);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
