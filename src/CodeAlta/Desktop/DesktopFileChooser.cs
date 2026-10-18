using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;

namespace CodeAlta.Desktop;

/// <summary>
/// Answers the file input of the page (the files attached to a prompt) with the file dialog of the operating system.
/// WebView2 shows its own dialog and does not ask; the web views of macOS and Linux ask, and cancel what nobody answers.
/// The page gets the content of the chosen files, as from a drop, never their paths.
/// </summary>
internal static class DesktopFileChooser
{
    /// <summary>Shows the dialog over a window and returns the files the user chose.</summary>
    /// <param name="window">The window the dialog belongs to.</param>
    /// <param name="dialogs">NeoAstra's dialogs; null where there are none.</param>
    /// <param name="request">What the page asks: whether several files may be chosen.</param>
    /// <returns>The chosen files, or a cancellation; this method does not throw for a dialog that fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> or <paramref name="request"/> is null.</exception>
    internal static async ValueTask<NeoFileChooserDecision> ChooseAsync(NeoWindow window, INeoDialogs? dialogs, NeoFileChooserRequest request)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(request);
        if (dialogs is null) return NeoFileChooserDecision.Cancel;
        try
        {
            var result = await dialogs.OpenFilesAsync(new NeoFileDialogOptions
            {
                Owner = window, Title = "Attach files", AllowMultiple = request.AllowsMultipleSelection,
                Scope = new NeoFileScope(DesktopFolderPicker.Roots(null)),
            }).ConfigureAwait(false);
            return Decide(result.Status, result.Value);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return NeoFileChooserDecision.Cancel;
        }
    }

    /// <summary>The answer to the page for what the dialog returned: the existing files among the chosen paths.</summary>
    internal static NeoFileChooserDecision Decide(NeoDesktopStatus status, IReadOnlyList<string>? paths)
    {
        if (status != NeoDesktopStatus.Success || paths is null) return NeoFileChooserDecision.Cancel;
        var files = paths.Where(static path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path)).ToArray();
        return files.Length == 0 ? NeoFileChooserDecision.Cancel : NeoFileChooserDecision.Select(files);
    }
}
