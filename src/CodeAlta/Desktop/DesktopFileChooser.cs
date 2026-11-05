using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Answers the file input of the page (the files attached to a prompt) with the file dialog of the operating system.
/// WebView2 shows its own dialog and does not ask; the web views of macOS and Linux ask, and cancel what nobody answers.
/// The page gets the content of the chosen files, as from a drop, never their paths. A desktop without a file dialog, and a
/// dialog that fails, cancel the input too: they are logged and the page is told, so that the button never does nothing.
/// </summary>
internal static class DesktopFileChooser
{
    /// <summary>Shows the dialog over a window and returns the files the user chose.</summary>
    /// <param name="window">The window the dialog belongs to; null for none.</param>
    /// <param name="dialogs">NeoAstra's dialogs; null where there are none.</param>
    /// <param name="request">What the page asks: whether several files may be chosen.</param>
    /// <param name="problem">
    /// Told <c>unavailable</c> when this desktop has no file dialog and <c>failed</c> when the dialog could not be shown;
    /// a cancel of the user tells nothing.
    /// </param>
    /// <returns>The chosen files, or a cancellation; this method does not throw for a dialog that fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="problem"/> is null.</exception>
    internal static async ValueTask<NeoFileChooserDecision> ChooseAsync(NeoWindow? window, INeoDialogs? dialogs, NeoFileChooserRequest request, Action<string> problem)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(problem);
        if (dialogs is null) return Report(problem, "unavailable", "there are no desktop dialogs");
        try
        {
            var result = await dialogs.OpenFilesAsync(new NeoFileDialogOptions
            {
                Owner = window, Title = "Attach files", AllowMultiple = request.AllowsMultipleSelection,
                Scope = new NeoFileScope(DesktopFolderPicker.Roots(null)),
            }).ConfigureAwait(false);
            return result.Status switch
            {
                NeoDesktopStatus.Unsupported => Report(problem, "unavailable", result.Code ?? "unsupported"),
                NeoDesktopStatus.Success or NeoDesktopStatus.Canceled => Decide(result.Status, result.Value),
                _ => Report(problem, "failed", $"{result.Status} {result.Code}".Trim()),
            };
        }
        catch (OperationCanceledException)
        {
            return NeoFileChooserDecision.Cancel;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return Report(problem, "failed", exception.Message);
        }
    }

    // The file input is canceled, and the page and the log say why.
    private static NeoFileChooserDecision Report(Action<string> problem, string kind, string reason)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop").Warn(kind == "unavailable"
            ? $"No file dialog is available on this desktop ({reason}): the files to attach were not asked for."
            : $"The file dialog could not be shown ({reason}): the files to attach were not asked for.");
        problem(kind);
        return NeoFileChooserDecision.Cancel;
    }

    /// <summary>The answer to the page for what the dialog returned: the existing files among the chosen paths.</summary>
    internal static NeoFileChooserDecision Decide(NeoDesktopStatus status, IReadOnlyList<string>? paths)
    {
        if (status != NeoDesktopStatus.Success || paths is null) return NeoFileChooserDecision.Cancel;
        var files = paths.Where(static path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path)).ToArray();
        return files.Length == 0 ? NeoFileChooserDecision.Cancel : NeoFileChooserDecision.Select(files);
    }
}
