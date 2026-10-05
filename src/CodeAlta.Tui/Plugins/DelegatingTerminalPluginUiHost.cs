using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.Plugins;

/// <summary>
/// The window of the terminal application as its plugins use it, given by the application as callbacks
/// over its own owners.
/// </summary>
internal sealed class DelegatingTerminalPluginUiHost : ITerminalPluginUiHost
{
    /// <inheritdoc />
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>Gets the callback that resolves the area a dialog is sized for.</summary>
    public required Func<Rectangle?> DialogBounds { get; init; }

    /// <summary>Gets the callback that resolves the control that takes the focus back.</summary>
    public required Func<Visual?> FocusTarget { get; init; }

    /// <summary>Gets the callback that shows a message in the status line.</summary>
    public required Action<string, bool> Status { get; init; }

    /// <summary>Gets the callback that reads the selected session.</summary>
    public required Func<string?> SelectedSession { get; init; }

    /// <summary>Gets the callback that says whether the selected session is running.</summary>
    public required Func<bool> SelectedSessionBusy { get; init; }

    /// <summary>Gets the callback that reads the prompt.</summary>
    public required Func<string?> ReadPrompt { get; init; }

    /// <summary>Gets the callback that replaces the prompt.</summary>
    public required Action<string> WritePrompt { get; init; }

    /// <summary>Gets the callback that sends or steers a prompt.</summary>
    public required Func<string, bool, Task> SendPrompt { get; init; }

    /// <summary>Gets the callback that queues a prompt and says whether a session took it.</summary>
    public required Func<string, bool> Enqueue { get; init; }

    /// <summary>Gets the callback that compacts the selected session and says whether there was one.</summary>
    public required Func<Task<bool>> Compact { get; init; }

    /// <inheritdoc />
    public Rectangle? GetDialogBounds() => DialogBounds();

    /// <inheritdoc />
    public Visual? GetFocusTarget() => FocusTarget();

    /// <inheritdoc />
    public void ShowMessage(string message, bool warning)
    {
        // A toast when the application shows them; the status line otherwise.
        if (ToastService.Show(message, warning ? ToastSeverity.Warning : ToastSeverity.Info) is null) Status(message, warning);
    }

    /// <inheritdoc />
    public string? SelectedSessionId => SelectedSession();

    /// <inheritdoc />
    public bool IsSelectedSessionBusy => SelectedSessionBusy();

    /// <inheritdoc />
    public string? PromptText
    {
        get => ReadPrompt();
        set => WritePrompt(value ?? string.Empty);
    }

    /// <inheritdoc />
    public Task SendPromptAsync(string text, bool steer) => SendPrompt(text, steer);

    /// <inheritdoc />
    public bool EnqueuePrompt(string text) => Enqueue(text);

    /// <inheritdoc />
    public Task<bool> CompactAsync() => Compact();
}
