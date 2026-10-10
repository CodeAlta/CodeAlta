using System.Text.Json;
using CodeAlta.LiveTool;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The canvases of plugins for <c>alta canvas</c>: what plugins declare, the tabs that are open, a request to the window for a
/// tab, and the actions of a canvas. It owns nothing: the broker keeps the instances and the page keeps the tabs, and nothing
/// here is a setting of the user.
/// </summary>
internal sealed class DesktopAltaCanvases(DesktopCanvases canvases) : IAltaCanvasView
{
    /// <inheritdoc />
    public bool HasWindow => canvases.HasPage;

    /// <inheritdoc />
    public IReadOnlyList<AltaCanvasDeclaration> List()
        => [.. canvases.Declarations().Select(static declaration =>
        {
            var canvas = declaration.Canvas;
            return new AltaCanvasDeclaration(declaration.PluginKey, declaration.Plugin.Descriptor.DisplayName ?? declaration.PluginKey, canvas.Id, canvas.Title,
                string.IsNullOrWhiteSpace(canvas.Description) ? null : canvas.Description, string.IsNullOrWhiteSpace(canvas.Icon) ? null : canvas.Icon, ScopeName(canvas.Scope),
                string.IsNullOrWhiteSpace(canvas.InputSchema) ? null : canvas.InputSchema, canvas.Describe is not null,
                [.. canvas.Actions.Select(static action => new AltaCanvasAction(action.Name, string.IsNullOrWhiteSpace(action.Description) ? null : action.Description,
                    string.IsNullOrWhiteSpace(action.InputSchema) ? null : action.InputSchema))]);
        })];

    /// <inheritdoc />
    public IReadOnlyList<AltaCanvasInstance> ListOpen()
        => [.. canvases.OpenInstances().Select(static instance => new AltaCanvasInstance(instance.InstanceId, instance.Identity.PluginKey, instance.Identity.CanvasId,
            instance.Identity.SpaceId, instance.Identity.ProjectId, instance.Identity.SessionId, instance.Identity.Key, instance.Title, instance.Visible))];

    /// <inheritdoc />
    public async ValueTask<AltaCanvasOpened> OpenAsync(AltaCanvasTarget target, JsonElement? input, bool focus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!canvases.HasPage) return new("unavailable", null, null, false);
        if (canvases.Find(target.PluginKey, target.CanvasId) is not { } declaration)
        {
            return new(canvases.IsActive(target.PluginKey) ? "unknown_canvas" : "plugin_stopped", null, null, false);
        }

        // The project and the session are the ones the command resolved: nothing is taken from the pane the user looks at.
        var result = canvases.RequestOpen(target.PluginKey, target.CanvasId, new PluginCanvasOpenOptions
        {
            SpaceId = target.SpaceId, ProjectId = target.ProjectId, SessionId = target.SessionId, Key = target.Key, Input = input, Focus = focus,
        }, fromPane: false);
        switch (result.Status)
        {
            case PluginCanvasOpenStatus.Requested:
                break;
            case PluginCanvasOpenStatus.Unavailable:
                return new("unavailable", null, null, false);
            case PluginCanvasOpenStatus.UnknownCanvas:
                return new("unknown_canvas", null, null, false);
            default:
                return new("invalid_request", null, null, false);
        }

        // A tab of a space that is not shown has no page to open its instance until the user shows that space: the instance opens now,
        // hidden, so that the canvas is listed, can be described and closed, and the tab finds it when the space is shown.
        if (!result.Shown && DesktopCanvases.Normalize(new CanvasIdentity(target.PluginKey, target.CanvasId, result.SpaceId, target.ProjectId, target.SessionId, target.Key), declaration.Canvas.Scope) is { } identity)
        {
            await canvases.OpenAsync(identity, visible: false, cancellationToken).ConfigureAwait(false);
        }

        return new("requested", result.InstanceId, result.SpaceId, result.Shown);
    }

    /// <inheritdoc />
    public async ValueTask<bool> CloseAsync(AltaCanvasTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (canvases.Find(target.PluginKey, target.CanvasId) is not { } declaration) return false;
        var space = target.SpaceId;
        // The window says which space it shows only after it said so: without a space the instance of the shown one is meant.
        var identity = DesktopCanvases.Normalize(new CanvasIdentity(target.PluginKey, target.CanvasId, space, target.ProjectId, target.SessionId, target.Key), declaration.Canvas.Scope);
        if (identity is not { } named) return false;
        var open = canvases.OpenInstances().FirstOrDefault(instance => Same(instance.Identity, named)
            || space is null && Same(instance.Identity with { SpaceId = null }, named));
        return open is not null && await canvases.CloseAsync(open.InstanceId, notifyPage: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AltaCanvasDescription> DescribeAsync(AltaCanvasTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var (status, markdown) = await canvases.DescribeAsync(Identity(target), cancellationToken).ConfigureAwait(false);
        return new(status, markdown);
    }

    /// <inheritdoc />
    public async ValueTask<AltaCanvasInvocation> InvokeAsync(AltaCanvasTarget target, string action, JsonElement? input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(action);
        var (status, result) = await canvases.InvokeAsync(Identity(target), action, input, cancellationToken).ConfigureAwait(false);
        return new(status, result);
    }

    private static CanvasIdentity Identity(AltaCanvasTarget target)
        => new(target.PluginKey, target.CanvasId, target.SpaceId, target.ProjectId, target.SessionId, target.Key);

    // Two identities that name the same instance: a session names its instance whatever the project that is said of it.
    private static bool Same(CanvasIdentity left, CanvasIdentity right)
        => string.Equals(DesktopCanvases.InstanceId(left), DesktopCanvases.InstanceId(right), StringComparison.Ordinal);

    private static string ScopeName(PluginCanvasScope scope)
        => scope switch
        {
            PluginCanvasScope.Project => AltaCanvasScopes.Project,
            PluginCanvasScope.Session => AltaCanvasScopes.Session,
            _ => AltaCanvasScopes.Application,
        };
}
