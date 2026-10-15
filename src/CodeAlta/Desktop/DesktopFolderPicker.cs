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
/// Lets the user choose a folder with the dialog of the operating system: NeoAstra's folder dialog, which on Windows
/// is the Explorer file dialog in folder mode, with the address bar, the search box and a field a path can be pasted
/// into (the folder tree of <c>SHBrowseForFolder</c> only where that dialog cannot be created).
/// </summary>
internal static class DesktopFolderPicker
{
    /// <summary>Shows the dialog over a window and returns the chosen folder.</summary>
    /// <param name="window">The window the dialog belongs to.</param>
    /// <param name="dialogs">NeoAstra's dialogs; null where there are none.</param>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="initialDirectory">The folder shown first; ignored unless it is an existing absolute folder.</param>
    /// <param name="cancellationToken">Cancels the wait where the platform dialog can be canceled.</param>
    /// <returns>The pick; this method does not throw for a dialog that fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="title"/> is empty.</exception>
    internal static async Task<DesktopFolderPick> PickAsync(NeoWindow window, INeoDialogs? dialogs, string title, string? initialDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return dialogs is null
            ? new(DesktopFolderPickStatus.Unavailable)
            : await ShowNeoAstraDialogAsync(window, dialogs, title, ExistingDirectory(initialDirectory), cancellationToken).ConfigureAwait(false);
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
            var result = await dialogs.OpenFoldersAsync(new NeoFileDialogOptions
            {
                Owner = window, Title = title, InitialDirectory = initial, Scope = new NeoFileScope(Roots(initial)),
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

    // The dialog may return any folder of the machine: the scope is every root, and the share of a first folder on the network.
    internal static IReadOnlyList<string> Roots(string? initial)
    {
        List<string> roots = OperatingSystem.IsWindows()
            ? [.. DriveInfo.GetDrives().Where(static drive => drive.IsReady).Select(static drive => drive.RootDirectory.FullName)]
            : ["/"];
        if (initial is not null && Path.GetPathRoot(initial) is { Length: > 0 } root && Directory.Exists(root)
            && !roots.Any(known => string.Equals(Path.TrimEndingDirectorySeparator(known), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)))
            roots.Add(root);
        return roots;
    }
}
