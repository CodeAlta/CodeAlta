using CodeAlta.Agent;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI;

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

    /// <summary>Gets workspace services.</summary>
    IPluginWorkspaceService Workspace { get; }

    /// <summary>Gets thread services.</summary>
    IPluginThreadService Threads { get; }

    /// <summary>Gets prompt services.</summary>
    IPluginPromptService Prompts { get; }

    /// <summary>Gets agent/backend services.</summary>
    IPluginAgentService Agents { get; }
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
    ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a custom dialog request and returns a response when the host supports result-bearing dialogs.</summary>
    /// <param name="request">The dialog request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The dialog response, or <see langword="null"/> when cancelled or unsupported.</returns>
    ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default);
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
/// Provides selected-thread operations for plugins.
/// </summary>
public interface IPluginThreadService
{
    /// <summary>Gets the selected thread identifier, when known.</summary>
    string? SelectedThreadId { get; }

    /// <summary>Gets a value indicating whether the selected thread is busy.</summary>
    bool IsSelectedThreadBusy { get; }

    /// <summary>Sends a prompt to the selected thread.</summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous send work.</returns>
    ValueTask SendPromptAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Enqueues a prompt for the selected thread.</summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing asynchronous enqueue work.</returns>
    ValueTask EnqueuePromptAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Attempts to steer an active thread.</summary>
    /// <param name="text">The steering text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when steering was accepted.</returns>
    ValueTask<bool> TrySteerAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Requests compaction for the selected thread.</summary>
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
/// Provides read-only agent/backend information.
/// </summary>
public interface IPluginAgentService
{
    /// <summary>Gets the active backend identifier, when known.</summary>
    AgentBackendId? ActiveBackendId { get; }

    /// <summary>Gets the active backend display name, when known.</summary>
    string? ActiveBackendDisplayName { get; }

    /// <summary>Gets the active model, when known.</summary>
    string? ActiveModel { get; }

    /// <summary>Gets a value indicating whether the current backend is CodeAlta-managed local/raw.</summary>
    bool IsCodeAltaManagedBackend { get; }

    /// <summary>Determines whether a named backend capability is available.</summary>
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

    /// <summary>Thread-scoped plugin state.</summary>
    Thread,
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
public sealed record PluginDialogRequest
{
    /// <summary>Gets the dialog title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets optional dialog text.</summary>
    public string? Message { get; init; }

    /// <summary>Gets optional custom dialog content.</summary>
    public Visual? Content { get; init; }

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
