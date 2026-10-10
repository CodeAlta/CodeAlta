using CodeAlta.Agent;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Aggregates stable host services available to plugins.
/// </summary>
public interface IPluginServices
{
    /// <summary>Gets the plugin logger.</summary>
    Logger Logger { get; }

    /// <summary>Gets UI services.</summary>
    IPluginUiService Ui { get; }

    /// <summary>Gets durable plugin state services.</summary>
    IPluginStateStore State { get; }

    /// <summary>
    /// Gets the tables the plugin keeps in the SQLite database of the application. A host that was written before plugins had
    /// tables there has no database for them, and this member says so (<see cref="IPluginDatabase.HasDatabase"/> is false).
    /// </summary>
    IPluginDatabase Database => NoopPluginDatabase.Instance;

    /// <summary>Gets workspace services.</summary>
    IPluginWorkspaceService Workspace { get; }

    /// <summary>Gets session services.</summary>
    IPluginSessionService Sessions { get; }

    /// <summary>Gets prompt services.</summary>
    IPluginPromptService Prompts { get; }

    /// <summary>Gets agent/provider services.</summary>
    IPluginAgentService Agents { get; }

    /// <summary>Gets plugin-lifetime task services.</summary>
    IPluginTaskService Tasks { get; }

    /// <summary>Gets in-process <c>alta</c> command services.</summary>
    IPluginAltaService Alta { get; }

    /// <summary>
    /// Gets canvas services: opening, closing and listing the tabs that the plugin provides. A host that was written before
    /// canvases existed has no window for them, and this member says so.
    /// </summary>
    IPluginCanvasService Canvases => NoopPluginCanvasService.Instance;
}

/// <summary>
/// Schedules plugin-owned background work that the runtime can track for plugin lifetime management.
/// </summary>
public interface IPluginTaskService
{
    /// <summary>Gets a value indicating whether the plugin has running background tasks.</summary>
    bool HasRunningTasks { get; }

    /// <summary>Gets the number of currently running background tasks.</summary>
    int RunningTaskCount { get; }

    /// <summary>
    /// Schedules plugin-owned background work and returns a runtime-trackable task handle.
    /// </summary>
    /// <param name="name">The task name used for diagnostics and unload blocking.</param>
    /// <param name="work">The work to run. The supplied token is cancelled when the plugin is deactivated or the handle is cancelled.</param>
    /// <param name="options">Optional task metadata and scheduling hints.</param>
    /// <returns>A handle that exposes task completion and cancellation.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="work"/> is <see langword="null"/>.</exception>
    PluginTaskHandle Run(string name, Func<CancellationToken, ValueTask> work, PluginTaskOptions? options = null);

    /// <summary>
    /// Waits until all currently tracked plugin tasks complete.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>A task that completes when no tracked tasks are running.</returns>
    ValueTask WhenIdleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata and scheduling hints for a plugin-owned background task.
/// </summary>
public sealed record PluginTaskOptions
{
    /// <summary>Gets a human-readable task description for diagnostics.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the work is expected to run for a long time.</summary>
    public bool LongRunning { get; init; }
}

/// <summary>
/// Runtime-trackable handle for plugin-owned background work.
/// </summary>
public sealed class PluginTaskHandle
{
    private readonly Action _requestCancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTaskHandle"/> class.
    /// </summary>
    /// <param name="name">The task name.</param>
    /// <param name="description">The task description.</param>
    /// <param name="longRunning">A value indicating whether this task is expected to run for a long time.</param>
    /// <param name="startedAt">The task start time.</param>
    /// <param name="completion">The task completion.</param>
    /// <param name="requestCancellation">The callback used to request task cancellation.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="completion"/> or <paramref name="requestCancellation"/> is <see langword="null"/>.</exception>
    public PluginTaskHandle(
        string name,
        string? description,
        bool longRunning,
        DateTimeOffset startedAt,
        Task completion,
        Action requestCancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(requestCancellation);
        Name = name;
        Description = description;
        LongRunning = longRunning;
        StartedAt = startedAt;
        Completion = completion;
        _requestCancellation = requestCancellation;
    }

    /// <summary>Gets the task name.</summary>
    public string Name { get; }

    /// <summary>Gets the task description.</summary>
    public string? Description { get; }

    /// <summary>Gets a value indicating whether this task is expected to run for a long time.</summary>
    public bool LongRunning { get; }

    /// <summary>Gets the task start time.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the task completion.</summary>
    public Task Completion { get; }

    /// <summary>Gets a value indicating whether the task has completed.</summary>
    public bool IsCompleted => Completion.IsCompleted;

    /// <summary>Requests cancellation for the task.</summary>
    public void RequestCancellation() => _requestCancellation();
}

/// <summary>
/// Provides mode-aware UI operations for plugins.
/// </summary>
public interface IPluginUiService
{
    /// <summary>Gets a value indicating whether interactive UI is available.</summary>
    bool HasInteractiveUi { get; }

    /// <summary>Shows a transient notification.</summary>
    /// <param name="message">The message to show.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous UI work.</returns>
    ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Asks the user to confirm an action.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The dialog message.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if confirmed; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    /// <summary>Prompts the user for text input.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="initialText">The initial text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The entered text, or <see langword="null"/> when cancelled or unsupported.</returns>
    ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default);

    /// <summary>Prompts the user to edit a text block.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="text">The initial text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The edited text, or <see langword="null"/> when cancelled or unsupported.</returns>
    ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default);

    /// <summary>Prompts the user to select an item.</summary>
    /// <typeparam name="T">The item value type.</typeparam>
    /// <param name="title">The dialog title.</param>
    /// <param name="items">The selectable items.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The selected value, or <see langword="default"/> when cancelled or unsupported.</returns>
    ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default);

    /// <summary>Shows a custom dialog request.</summary>
    /// <param name="request">The dialog request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous UI work.</returns>
    /// <remarks>Unsupported hosts may complete without presenting UI. Completion is not confirmation that the dialog was shown.</remarks>
    ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a custom dialog request and returns a response when the host supports result-bearing dialogs.</summary>
    /// <param name="request">The dialog request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The dialog response, or <see langword="null"/> when cancelled or unsupported.</returns>
    ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the host that what the buttons of the plugin say may have changed, so it reads their state again
    /// (<see cref="PluginButtonContribution.GetState"/>). A command of the plugin that ends does it already.
    /// </summary>
    /// <remarks>A host that draws no buttons ignores it. It is cheap and may be called often: the host reads once for a burst.</remarks>
    void InvalidateButtons()
    {
    }
}

/// <summary>
/// Stores plugin-owned durable state in host-provided namespaces.
/// </summary>
public interface IPluginStateStore
{
    /// <summary>Gets the directory for a state scope.</summary>
    /// <param name="scope">The state scope.</param>
    /// <returns>The directory path.</returns>
    string GetDirectory(PluginStateScope scope);

    /// <summary>Reads a JSON state value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="scope">The state scope.</param>
    /// <param name="name">The state item name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The value, or <see langword="default"/> when missing.</returns>
    ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default);

    /// <summary>Writes a JSON state value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="scope">The state scope.</param>
    /// <param name="name">The state item name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous state work.</returns>
    ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a state value.</summary>
    /// <param name="scope">The state scope.</param>
    /// <param name="name">The state item name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous state work.</returns>
    ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides read-only workspace and path helpers.
/// </summary>
public interface IPluginWorkspaceService
{
    /// <summary>Gets the selected project identifier, when known.</summary>
    string? SelectedProjectId { get; }

    /// <summary>Gets the selected project path, when known.</summary>
    string? SelectedProjectPath { get; }

    /// <summary>
    /// Gets the identifier of the space the window showed when the running operation started (a command run from a button
    /// or a menu, for example), or <see langword="null"/> when the host has no spaces or no operation is running.
    /// </summary>
    string? SelectedSpaceId => null;

    /// <summary>Gets known project paths.</summary>
    IReadOnlyList<string> ProjectPaths { get; }

    /// <summary>Combines a project-relative path with the selected project root.</summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The absolute path, or <see langword="null"/> when no project is selected.</returns>
    string? GetSelectedProjectPath(string relativePath);

    /// <summary>Determines whether a path is inside the selected project.</summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns><see langword="true"/> when the path is inside the selected project.</returns>
    bool IsInsideSelectedProject(string path);
}

/// <summary>
/// Provides selected-session operations for plugins.
/// </summary>
public interface IPluginSessionService
{
    /// <summary>Gets the selected session identifier, when known.</summary>
    string? SelectedSessionId { get; }

    /// <summary>Gets a value indicating whether the selected session is busy.</summary>
    bool IsSelectedSessionBusy { get; }

    /// <summary>Sends a prompt to the selected session.</summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous send work.</returns>
    ValueTask SendPromptAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Enqueues a prompt for the selected session.</summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous enqueue work.</returns>
    ValueTask EnqueuePromptAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Attempts to steer an active session.</summary>
    /// <param name="text">The steering text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when steering was accepted.</returns>
    ValueTask<bool> TrySteerAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Requests compaction for the selected session.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when compaction was requested.</returns>
    ValueTask<bool> RequestCompactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides prompt draft and attachment operations.
/// </summary>
public interface IPluginPromptService
{
    /// <summary>Gets the current prompt draft, when available.</summary>
    string? DraftText { get; }

    /// <summary>Sets the current prompt draft.</summary>
    /// <param name="text">The draft text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous draft work.</returns>
    ValueTask SetDraftTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Adds an attachment to the prompt draft.</summary>
    /// <param name="attachment">The attachment to add.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous attachment work.</returns>
    ValueTask AddAttachmentAsync(PluginPromptAttachment attachment, CancellationToken cancellationToken = default);

    /// <summary>Gets current draft attachments.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The draft attachments.</returns>
    ValueTask<IReadOnlyList<PluginPromptAttachment>> GetAttachmentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides read-only agent/provider information.
/// </summary>
public interface IPluginAgentService
{
    /// <summary>Gets the active model provider identifier, when known.</summary>
    ModelProviderId? ActiveProviderId { get; }

    /// <summary>Gets the active provider display name, when known.</summary>
    string? ActiveProviderDisplayName { get; }

    /// <summary>Gets the active model, when known.</summary>
    string? ActiveModel { get; }

    /// <summary>Gets a value indicating whether the current provider is CodeAlta-managed local/raw.</summary>
    bool IsCodeAltaManagedProvider { get; }

    /// <summary>Determines whether a named provider capability is available.</summary>
    /// <param name="capabilityName">The capability name.</param>
    /// <returns><see langword="true"/> when the capability is available.</returns>
    bool HasCapability(string capabilityName);
}

/// <summary>
/// Identifies the scope of durable plugin state.
/// </summary>
public enum PluginStateScope
{
    /// <summary>User-wide plugin state.</summary>
    User,

    /// <summary>Project-scoped plugin state.</summary>
    Project,

    /// <summary>Session-scoped plugin state.</summary>
    Session,
}

/// <summary>
/// Describes an item in a plugin selection dialog.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed record PluginSelectItem<T>
{
    /// <summary>Gets the display label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the item value.</summary>
    public required T Value { get; init; }

    /// <summary>Gets an optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether this item is initially selected.</summary>
    public bool IsSelected { get; init; }
}

/// <summary>
/// Describes a dialog request supplied by a plugin.
/// </summary>
public record PluginDialogRequest
{
    /// <summary>Gets the dialog title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets optional dialog text.</summary>
    public string? Message { get; init; }

    /// <summary>Gets initial text for input or editor dialogs.</summary>
    public string? InitialText { get; init; }

    /// <summary>Gets selection item labels for selection dialogs.</summary>
    public IReadOnlyList<string> SelectionItems { get; init; } = [];

    /// <summary>Gets custom dialog buttons.</summary>
    public IReadOnlyList<PluginDialogButton> Buttons { get; init; } = [];

    /// <summary>Gets the dialog kind.</summary>
    public PluginDialogKind Kind { get; init; } = PluginDialogKind.Custom;

    /// <summary>Gets custom metadata for runtime-specific dialogs.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the HTML fragment shown as the content of a custom dialog in the desktop application.
    /// </summary>
    /// <remarks>
    /// Fields that have a <c>name</c> attribute are returned in <see cref="PluginDialogResponse.Values"/>.
    /// See <see cref="PluginHtml"/> for what a fragment can contain.
    /// </remarks>
    public string? Html { get; init; }

    /// <summary>
    /// Gets the script of <see cref="Html"/>, or <see langword="null"/> for a dialog that is its fragment alone. The module draws the content
    /// of the dialog (see <see cref="PluginScript"/>); it reaches the plugin's commands and the window through the <c>alta</c> object.
    /// </summary>
    public PluginScript? Script { get; init; }

    /// <summary>
    /// Gets the handler called when an element with <c>data-alta-action</c> is activated in <see cref="Html"/>.
    /// The dialog stays open while the handler runs; its result updates the content or closes the dialog.
    /// </summary>
    public PluginDialogActionHandler? OnAction { get; init; }
}

/// <summary>Handles an action raised by the HTML content of an open dialog.</summary>
/// <param name="action">The action and the current values of the fields of the dialog.</param>
/// <param name="cancellationToken">A token cancelled when the dialog is closed.</param>
/// <returns>What the dialog does next.</returns>
public delegate ValueTask<PluginDialogActionResult> PluginDialogActionHandler(PluginDialogAction action, CancellationToken cancellationToken);

/// <summary>
/// Describes an action raised by the HTML content of an open dialog.
/// </summary>
public sealed record PluginDialogAction
{
    /// <summary>Gets the action name: the value of the <c>data-alta-action</c> attribute of the element.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the value of the <c>data-alta-value</c> attribute of the element, when it has one.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the current values of the named fields of the dialog.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Describes what an open dialog does after an action.
/// </summary>
public sealed record PluginDialogActionResult
{
    /// <summary>Gets a result that leaves the dialog as it is.</summary>
    public static PluginDialogActionResult KeepOpen { get; } = new();

    /// <summary>Gets the HTML fragment that replaces the dialog content, or <see langword="null"/> to keep it.</summary>
    public string? Html { get; init; }

    /// <summary>Gets a value indicating whether the dialog closes.</summary>
    public bool Close { get; init; }

    /// <summary>Gets the button name reported by <see cref="PluginDialogResponse.ButtonName"/> when the dialog closes.</summary>
    public string? ButtonName { get; init; }

    /// <summary>Creates a result that replaces the dialog content.</summary>
    /// <param name="html">The new HTML fragment.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="html"/> is null.</exception>
    public static PluginDialogActionResult Update(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new PluginDialogActionResult { Html = html };
    }

    /// <summary>Creates a result that closes the dialog.</summary>
    /// <param name="buttonName">The name reported as the activated button, when any.</param>
    /// <returns>The result.</returns>
    public static PluginDialogActionResult CloseDialog(string? buttonName = null)
        => new() { Close = true, ButtonName = buttonName };
}

/// <summary>
/// Describes a button in a plugin dialog.
/// </summary>
public sealed record PluginDialogButton
{
    /// <summary>Gets the stable button name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the button label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets a value indicating whether this is the default button.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets a value indicating whether this button cancels the dialog.</summary>
    public bool IsCancel { get; init; }
}

/// <summary>
/// Describes the result of a plugin dialog.
/// </summary>
public sealed record PluginDialogResponse
{
    /// <summary>Gets the activated button name, when available.</summary>
    public string? ButtonName { get; init; }

    /// <summary>Gets a value indicating whether the dialog was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Gets text entered by the user.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the selected item index.</summary>
    public int? SelectedIndex { get; init; }

    /// <summary>Gets response metadata supplied by the host.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the values of the named fields of an HTML dialog when it was closed.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Identifies a plugin dialog kind.
/// </summary>
public enum PluginDialogKind
{
    /// <summary>A notification dialog.</summary>
    Notification,

    /// <summary>A confirmation dialog.</summary>
    Confirmation,

    /// <summary>A text input dialog.</summary>
    Input,

    /// <summary>A selection dialog.</summary>
    Selection,

    /// <summary>A text editor dialog.</summary>
    TextEditor,

    /// <summary>A custom visual dialog.</summary>
    Custom,
}
