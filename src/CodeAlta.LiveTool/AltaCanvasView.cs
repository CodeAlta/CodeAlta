using System.Text.Json;

namespace CodeAlta.LiveTool;

/// <summary>
/// The canvases that plugins provide, in a host that has a window to show them in. A host without one registers no
/// such service, and <c>alta canvas</c> is then not part of its commands.
/// </summary>
/// <remarks>
/// A canvas is a tab that a plugin declares. Listing, describing and invoking the actions of a canvas need no window;
/// opening, focusing and closing a tab need one. Nothing here changes a setting of the user.
/// </remarks>
public interface IAltaCanvasView
{
    /// <summary>Gets a value indicating whether a window is there to show tabs.</summary>
    bool HasWindow { get; }

    /// <summary>Lists the canvases that the active plugins declare, in the order of their contributions.</summary>
    /// <returns>The declarations.</returns>
    IReadOnlyList<AltaCanvasDeclaration> List();

    /// <summary>Lists the instances that are open now, in the order they were opened.</summary>
    /// <returns>The open instances, of every space.</returns>
    IReadOnlyList<AltaCanvasInstance> ListOpen();

    /// <summary>Asks the window for a tab of a canvas: it opens in the space the target names, or is brought to the front.</summary>
    /// <param name="target">The canvas and where to show it. The space is the one of the tab; it need not be the one the window shows.</param>
    /// <param name="input">The JSON input of the canvas, given to the instance the tab creates; null for none.</param>
    /// <param name="focus">Whether to bring the tab to the front of its space.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    ValueTask<AltaCanvasOpened> OpenAsync(AltaCanvasTarget target, JsonElement? input, bool focus, CancellationToken cancellationToken);

    /// <summary>Closes the instance of a canvas, and its tab. The state of the canvas stays with its plugin.</summary>
    /// <param name="target">The canvas and the context of the instance.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>False when no such instance is open.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    ValueTask<bool> CloseAsync(AltaCanvasTarget target, CancellationToken cancellationToken);

    /// <summary>Asks a canvas what it shows now, in Markdown. The instance need not be open.</summary>
    /// <param name="target">The canvas and its context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status (<c>ok</c>, <c>unknown_canvas</c>, <c>plugin_stopped</c>, <c>invalid_request</c>, <c>failed</c>) and the text, which is null when the canvas cannot describe itself.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    ValueTask<AltaCanvasDescription> DescribeAsync(AltaCanvasTarget target, CancellationToken cancellationToken);

    /// <summary>Runs an action that a canvas declares for agents. The instance need not be open, and no window is needed.</summary>
    /// <param name="target">The canvas and its context.</param>
    /// <param name="action">The name of the action.</param>
    /// <param name="input">The JSON input of the action, or null.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status (<c>ok</c>, <c>unknown_canvas</c>, <c>unknown_action</c>, <c>plugin_stopped</c>, <c>invalid_request</c>, <c>failed</c>) and the JSON result, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="action"/> is null.</exception>
    ValueTask<AltaCanvasInvocation> InvokeAsync(AltaCanvasTarget target, string action, JsonElement? input, CancellationToken cancellationToken);
}

/// <summary>The scope of a canvas: what one instance of it is about.</summary>
public static class AltaCanvasScopes
{
    /// <summary>One instance for the application.</summary>
    public const string Application = "application";

    /// <summary>One instance for each project.</summary>
    public const string Project = "project";

    /// <summary>One instance for each session.</summary>
    public const string Session = "session";
}

/// <summary>A canvas that an active plugin declares.</summary>
/// <param name="PluginKey">The runtime key of the plugin.</param>
/// <param name="Plugin">The display name of the plugin.</param>
/// <param name="Id">The identifier of the canvas, unique within its plugin.</param>
/// <param name="Title">The title of its tabs.</param>
/// <param name="Description">What it shows and does, written for agents, or null.</param>
/// <param name="Icon">The name of its icon, or null.</param>
/// <param name="Scope">One of <see cref="AltaCanvasScopes"/>.</param>
/// <param name="InputSchema">The JSON Schema of the input of <c>open</c>, or null when the canvas takes none.</param>
/// <param name="Describes">The canvas can describe what it shows in Markdown.</param>
/// <param name="Actions">The actions it declares for agents.</param>
public sealed record AltaCanvasDeclaration(string PluginKey, string Plugin, string Id, string Title, string? Description, string? Icon, string Scope,
    string? InputSchema, bool Describes, IReadOnlyList<AltaCanvasAction> Actions);

/// <summary>An action that a canvas declares for agents.</summary>
/// <param name="Name">The name of the action.</param>
/// <param name="Description">What it does, or null.</param>
/// <param name="InputSchema">The JSON Schema of its input, or null.</param>
public sealed record AltaCanvasAction(string Name, string? Description, string? InputSchema);

/// <summary>An instance of a canvas that is open: a tab of a space shows it, or did.</summary>
/// <param name="InstanceId">The identifier of the instance.</param>
/// <param name="PluginKey">The runtime key of the plugin.</param>
/// <param name="CanvasId">The canvas.</param>
/// <param name="SpaceId">The space of the tab, or null in a window without spaces.</param>
/// <param name="ProjectId">The project of the instance, or null.</param>
/// <param name="SessionId">The session of the instance, or null.</param>
/// <param name="Key">The key that tells apart instances in the same context, or null.</param>
/// <param name="Title">The title of the tab now.</param>
/// <param name="Visible">A tab shows the instance now.</param>
public sealed record AltaCanvasInstance(string InstanceId, string PluginKey, string CanvasId, string? SpaceId, string? ProjectId, string? SessionId, string? Key,
    string Title, bool Visible);

/// <summary>Names an instance of a canvas: the canvas, and where and about what it is shown.</summary>
/// <param name="PluginKey">The runtime key of the plugin.</param>
/// <param name="CanvasId">The canvas.</param>
/// <param name="SpaceId">The space of the tab, or null for the one the window shows.</param>
/// <param name="ProjectId">The identifier of the project (the one the window uses, not its slug) of a project canvas, or of the session of a session canvas.</param>
/// <param name="SessionId">The session of a session canvas.</param>
/// <param name="Key">The key that tells apart several instances in the same context.</param>
public sealed record AltaCanvasTarget(string PluginKey, string CanvasId, string? SpaceId, string? ProjectId, string? SessionId, string? Key);

/// <summary>How a request for a tab ended.</summary>
/// <param name="Status"><c>requested</c> (the window was asked), <c>unavailable</c> (no window), <c>unknown_canvas</c>, <c>plugin_stopped</c>, <c>invalid_request</c> (a context is missing or ill-formed).</param>
/// <param name="InstanceId">The identifier the instance has, when the window was asked.</param>
/// <param name="SpaceId">The space the tab was added to, when the window was asked.</param>
/// <param name="Shown">The window shows that space: the tab is in front of the user. False when the tab waits in a space that is not shown.</param>
public sealed record AltaCanvasOpened(string Status, string? InstanceId, string? SpaceId, bool Shown);

/// <summary>What a canvas shows, in Markdown.</summary>
/// <param name="Status">The status code: <c>ok</c> or a refusal.</param>
/// <param name="Markdown">The text; null when the canvas cannot describe itself.</param>
public sealed record AltaCanvasDescription(string Status, string? Markdown);

/// <summary>The answer of an action of a canvas.</summary>
/// <param name="Status">The status code: <c>ok</c> or a refusal.</param>
/// <param name="Result">The JSON result, or null.</param>
public sealed record AltaCanvasInvocation(string Status, JsonElement? Result);
