using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The user interface of plugins in the window: their commands and shortcuts, the content they add around
/// the prompt, their prompt pickers, and the dialogs and notifications they ask for.
/// </summary>
/// <remarks>
/// A plugin never reaches the page: it returns text, Markdown or an HTML fragment, and the page shows it
/// with its own components after sanitizing it. What the page sends back is a command to run, a picker
/// query, or the answer to a dialog. Plugin exception text does not cross the bridge.
/// </remarks>
[NeoRpcService("pluginUi", Version = 1)]
internal sealed class PluginUiService
{
    /// <summary>Largest number of commands, pickers or region contents in one response.</summary>
    internal const int MaximumContributions = 256;

    // The MCP plugin's status content is shown by the status line itself, with its link to the settings.
    private const string McpRuntimeKey = "builtin:" + ComposerStatusService.McpPluginId;

    /// <summary>Largest number of items one picker search returns.</summary>
    internal const int MaximumPickerItems = 50;

    internal const int MaximumQueryUnits = 256;
    internal const int MaximumIdUnits = 512;
    private const int MaximumLabelUnits = 200;
    private const int MaximumDescriptionUnits = 1024;
    private const int MaximumInsertUnits = 4096;
    private const int MaximumFieldUnits = 64 * 1024;
    private const int MaximumFields = 128;

    private readonly ProjectCatalog? _projects;
    private readonly PluginRuntimeManager? _plugins;
    private readonly DesktopPluginUi? _ui;
    private readonly DesktopPluginModules? _modules;
    private readonly DesktopCanvases? _canvases;
    private readonly PluginIcons _icons = new();
    private readonly string? _epoch;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly List<Task> _commands = [];

    /// <summary>Creates an unavailable service for launches without plugins.</summary>
    internal PluginUiService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its folder.</param>
    /// <param name="plugins">The host's plugin runtime.</param>
    /// <param name="ui">The broker the host gave its plugins as their UI service.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="canvases">The canvas broker, which says whether a canvas that a button names exists; null leaves the buttons that open a canvas out.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    /// <param name="modules">The server of the scripts of what plugins show, or null: the one of the canvas broker is used.</param>
    internal PluginUiService(ProjectCatalog projects, PluginRuntimeManager plugins, DesktopPluginUi ui, string epoch, DesktopCanvases? canvases = null, DesktopPluginModules? modules = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _plugins = plugins;
        _ui = ui;
        _canvases = canvases;
        _modules = modules ?? canvases?.Modules;
        _epoch = epoch;
    }

    /// <summary>Lists the plugin commands and prompt pickers that apply to a project, or to no project.</summary>
    [NeoRpcMethod("contributions")]
    public async Task<PluginUiContributionsResponse> ContributionsAsync(PluginUiScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginUiContributionsResponse Failed(string status) => new(status, request.ProjectId, [], [], false);
        var scope = await ResolveAsync(request.ExpectedEpoch, request.ProjectId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return Failed(scope.Status);
        try
        {
            var active = _plugins!.ActivePlugins;
            var commands = new List<PluginUiCommand>();
            foreach (var registration in _plugins.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, scope.Options))
            {
                if (commands.Count == MaximumContributions) break;
                var command = (PluginCommandContribution)registration.Contribution;
                if (command.Placement == PluginCommandPlacement.None || !ValidName(command.Name)) continue;
                var availability = command.Availability ?? PluginCommandAvailability.Always;
                commands.Add(new(
                    registration.Handle.RuntimeContributionKey,
                    registration.Handle.PluginRuntimeKey,
                    PluginName(active, registration),
                    command.Name,
                    Line(string.IsNullOrWhiteSpace(command.Label) ? command.Name : command.Label, MaximumLabelUnits),
                    Line(command.Description ?? string.Empty, MaximumDescriptionUnits),
                    command.Group is null ? null : Line(command.Group, MaximumLabelUnits),
                    command.SearchText is null ? null : Line(command.SearchText, MaximumDescriptionUnits),
                    command.KeyBinding?.ToString(),
                    command.ShowInCommandPalette,
                    command.ShowInHelp,
                    availability.RequiresProject,
                    availability.RequiresSession || availability.RequiresIdleSession || availability.RequiresBusySession,
                    availability.RequiresIdleSession,
                    availability.RequiresBusySession));
            }

            var pickers = new List<PluginUiPicker>();
            foreach (var registration in _plugins.Adapter.GetContributions<PluginPromptPickerContribution>(PluginPoint.PromptPicker, scope.Options))
            {
                if (pickers.Count == MaximumContributions) break;
                var picker = (PluginPromptPickerContribution)registration.Contribution;
                // One picker per character: the first registered wins, and CodeAlta's own characters are not offered.
                if (picker.Trigger is '@' or '#' or '/' || pickers.Any(existing => existing.Trigger == picker.Trigger.ToString())) continue;
                pickers.Add(new(registration.Handle.RuntimeContributionKey, PluginName(active, registration), picker.Trigger.ToString(),
                    Line(picker.Title, MaximumLabelUnits), picker.PlaceholderText is null ? null : Line(picker.PlaceholderText, MaximumLabelUnits)));
            }

            // The page reads the regions of a composer only when a plugin has content for them.
            var regions = _plugins.Adapter.GetContributions<PluginContentContribution>(PluginPoint.Ui, scope.Options)
                .Any(static registration => !string.Equals(registration.Handle.PluginRuntimeKey, McpRuntimeKey, StringComparison.Ordinal));
            return new("ok", request.ProjectId, [.. commands], [.. pickers], regions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed"); // A plugin failing in a property getter must not break the page.
        }
    }

    /// <summary>
    /// Lists the buttons that plugins put in the window at one place (or at every place), with the state each one gave for
    /// the project, session and space of the context: the selected ones for the title bar and the rail, the ones of the row
    /// for a menu. A button that names a command or a canvas that its plugin does not have is left out.
    /// </summary>
    [NeoRpcMethod("buttons")]
    public async Task<PluginUiButtonsResponse> ButtonsAsync(PluginUiButtonsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginUiButtonsResponse Failed(string status) => new(status, request.Place, []);
        PluginButtonPlace? place = null;
        if (request.Place is { } named)
        {
            if (!Enum.TryParse<PluginButtonPlace>(named, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) return Failed("invalid_request");
            place = parsed;
        }

        if (request.SpaceId is { } space && (space.Length is 0 or > MaximumIdUnits || space.Any(char.IsControl))) return Failed("invalid_request");
        var scope = await ResolveAsync(request.ExpectedEpoch, request.ProjectId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return Failed(scope.Status);
        try
        {
            var active = _plugins!.ActivePlugins;
            var options = scope.Options! with { SessionId = request.SessionId };
            var commands = _plugins.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, options);
            var buttons = new List<PluginUiWindowButton>();
            foreach (var entry in _plugins.Adapter.GetButtonEntries(active, place, request.SpaceId, options))
            {
                if (buttons.Count == MaximumContributions) break;
                if (Button(entry, active, commands, request) is { } button) buttons.Add(button);
            }

            return new("ok", request.Place, [.. buttons]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed"); // A plugin failing in a property getter must not break the page.
        }
    }

    private PluginUiWindowButton? Button(PluginButtonEntry entry, IReadOnlyList<ActivePluginInstance> active, IReadOnlyList<PluginContributionRegistration> commands, PluginUiButtonsRequest request)
    {
        var registration = entry.Registration;
        var button = entry.Button;
        string? commandId = null;
        string? canvasScope = null;
        var disabled = entry.State.Disabled;
        if (!string.IsNullOrWhiteSpace(button.Command))
        {
            // A command of the same plugin, by its name as a status item names it.
            var command = commands.FirstOrDefault(candidate => string.Equals(candidate.Handle.PluginRuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal)
                && string.Equals(((PluginCommandContribution)candidate.Contribution).Name, button.Command, StringComparison.OrdinalIgnoreCase));
            if (command is null) return null;
            commandId = command.Handle.RuntimeContributionKey;
            var availability = ((PluginCommandContribution)command.Contribution).Availability ?? PluginCommandAvailability.Always;
            disabled |= availability.RequiresProject && request.ProjectId is null
                || (availability.RequiresSession || availability.RequiresIdleSession || availability.RequiresBusySession) && request.SessionId is null;
        }
        else
        {
            var canvas = _canvases?.Find(registration.Handle.PluginRuntimeKey, button.Canvas ?? string.Empty);
            if (canvas is null) return null;
            canvasScope = canvas.Canvas.Scope.ToString();
            // A canvas about a project or a session needs it: without one the button stays, and cannot be used.
            disabled |= canvas.Canvas.Scope == PluginCanvasScope.Project && request.ProjectId is null
                || canvas.Canvas.Scope == PluginCanvasScope.Session && request.SessionId is null;
        }

        var plugin = active.FirstOrDefault(candidate => string.Equals(candidate.Descriptor.RuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal));
        var icon = button.Icon.Trim();
        var iconData = PluginIcons.IsFile(icon) ? _icons.Read(plugin?.SourcePackage?.PackageDirectory, icon) : null;
        var state = entry.State;
        var (badge, count) = state.Badge.Kind switch
        {
            PluginButtonBadgeKind.Count => ("count", Math.Min(state.Badge.Count, 9999)),
            PluginButtonBadgeKind.Dot => ("dot", 0),
            PluginButtonBadgeKind.Busy => ("busy", 0),
            _ => ("none", 0),
        };
        return new(registration.Handle.RuntimeContributionKey, registration.Handle.PluginRuntimeKey, PluginId(registration.Handle.PluginRuntimeKey, plugin), PluginName(active, registration),
            button.Id, button.Place.ToString(), Line(icon, MaximumLabelUnits), iconData, Line(button.Label, MaximumLabelUnits), commandId, button.Canvas, canvasScope, badge, count,
            state.Tone.ToString(), state.Hidden, disabled, string.IsNullOrWhiteSpace(state.Tooltip) ? null : Line(state.Tooltip, MaximumDescriptionUnits));
    }

    // The id Settings lists a plugin under: the folder of a source package, the name after "builtin:" for a plugin of the application.
    private static string PluginId(string runtimeKey, ActivePluginInstance? plugin)
        => plugin?.SourcePackage?.PackageId ?? (runtimeKey.StartsWith("builtin:", StringComparison.Ordinal) ? runtimeKey["builtin:".Length..] : runtimeKey);

    /// <summary>Returns what plugins show around the prompt of a pane.</summary>
    [NeoRpcMethod("regions")]
    public async Task<PluginUiRegionsResponse> RegionsAsync(PluginUiScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginUiRegionsResponse Failed(string status) => new(status, request.ProjectId, request.SessionId, []);
        var scope = await ResolveAsync(request.ExpectedEpoch, request.ProjectId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return Failed(scope.Status);
        try
        {
            var active = _plugins!.ActivePlugins;
            var options = scope.Options! with { SessionId = request.SessionId };
            var items = new List<PluginUiContent>();
            // The regions of a plugin often share its package: its folder is looked at once for this read.
            var modules = _modules?.StartRead();
            foreach (var region in new[] { PluginUiRegion.SessionFooter, PluginUiRegion.CommandBar, PluginUiRegion.SessionStatus })
            {
                foreach (var entry in _plugins.Adapter.CreateContentEntries(active, region, options))
                {
                    if (items.Count == MaximumContributions) break;
                    if (string.Equals(entry.Registration.Handle.PluginRuntimeKey, McpRuntimeKey, StringComparison.Ordinal)) continue;
                    var content = entry.Content;
                    if (content.Html is null && content.Markdown is null && string.IsNullOrWhiteSpace(content.Text)) continue;
                    string? script = null, scriptProblem = null;
                    if (content.Html is not null && content.Script is { HasEntry: true } wanted)
                    {
                        script = modules?.PublishFor(entry.Registration.Handle.PluginRuntimeKey, wanted);
                        if (script is null) scriptProblem = "The script of the content could not be found.";
                    }

                    items.Add(new(entry.Registration.Handle.RuntimeContributionKey, entry.Registration.Handle.PluginRuntimeKey,
                        region switch { PluginUiRegion.SessionFooter => "footer", PluginUiRegion.CommandBar => "bar", _ => "status" },
                        content.Html is null ? null : DesktopPluginUi.Cut(content.Html, DesktopPluginUi.MaximumHtmlUnits),
                        content.Html is not null || content.Markdown is null ? null : DesktopPluginUi.Cut(content.Markdown, DesktopPluginUi.MaximumMessageUnits),
                        content.Html is not null || content.Markdown is not null || content.Text is null ? null : DesktopPluginUi.Cut(content.Text, DesktopPluginUi.MaximumMessageUnits))
                    { Script = script, ScriptProblem = scriptProblem });
                }
            }

            return new("ok", request.ProjectId, request.SessionId, [.. items]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed");
        }
    }

    /// <summary>
    /// Starts a plugin command for a pane. The answer says that it started; what the command shows or sends
    /// arrives on <see cref="Watch"/>, because a command can wait for the user in a dialog.
    /// </summary>
    [NeoRpcMethod("invokeCommand")]
    public async Task<PluginUiStatusResponse> InvokeCommandAsync(PluginUiInvokeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await ResolveAsync(request.ExpectedEpoch, request.ProjectId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return new(scope.Status);
        if (request.CommandId is not { Length: > 0 and <= MaximumIdUnits } id || request.DraftText is { Length: > DesktopPluginUi.MaximumTextUnits }
            || request.SpaceId is { } space && (space.Length is 0 or > MaximumIdUnits || space.Any(char.IsControl))) return new("invalid_request");
        var registration = _plugins!.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, scope.Options)
            .FirstOrDefault(candidate => string.Equals(candidate.Handle.RuntimeContributionKey, id, StringComparison.Ordinal));
        if (registration is null) return new("unknown_command");
        var command = (PluginCommandContribution)registration.Contribution;
        var availability = command.Availability ?? PluginCommandAvailability.Always;
        var needsSession = availability.RequiresSession || availability.RequiresIdleSession || availability.RequiresBusySession;
        if (availability.RequiresProject && request.ProjectId is null || needsSession && request.SessionId is null
            || availability.RequiresIdleSession && request.SessionBusy || availability.RequiresBusySession && !request.SessionBusy) return new("unavailable");

        var options = scope.Options! with { SessionId = request.SessionId };
        var pane = new DesktopPluginScope
        {
            ProjectId = request.ProjectId, ProjectPath = scope.Options!.ProjectPath, SessionId = request.SessionId,
            SessionBusy = request.SessionBusy, DraftText = request.DraftText, SpaceId = request.SpaceId,
        };
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return new("closed");
            _commands.RemoveAll(static task => task.IsCompleted);
            _commands.Add(Task.Run(() => RunCommandAsync(command, options, pane), CancellationToken.None));
        }

        return new("started");
    }

    /// <summary>Searches a prompt picker.</summary>
    [NeoRpcMethod("searchPicker")]
    public async Task<PluginUiPickerResponse> SearchPickerAsync(PluginUiPickerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginUiPickerResponse Failed(string status) => new(status, request.PickerId, []);
        var scope = await ResolveAsync(request.ExpectedEpoch, request.ProjectId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return Failed(scope.Status);
        if (request.PickerId is not { Length: > 0 and <= MaximumIdUnits } id || request.Query is not { Length: <= MaximumQueryUnits } query
            || query.Any(char.IsControl)) return Failed("invalid_request");
        var registration = _plugins!.Adapter.GetContributions<PluginPromptPickerContribution>(PluginPoint.PromptPicker, scope.Options)
            .FirstOrDefault(candidate => string.Equals(candidate.Handle.RuntimeContributionKey, id, StringComparison.Ordinal));
        if (registration is null) return Failed("unknown_picker");
        _ui!.Enter(new DesktopPluginScope { ProjectId = request.ProjectId, ProjectPath = scope.Options!.ProjectPath, SessionId = request.SessionId });
        var (items, diagnostics) = await _plugins.Adapter.SearchPromptPickerAsync(_plugins.ActivePlugins,
            (PluginPromptPickerContribution)registration.Contribution, query, scope.Options with { SessionId = request.SessionId }, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Count > 0) return Failed("failed");
        return new("ok", id, [.. items.Where(static item => item is not null && !string.IsNullOrEmpty(item.Label) && item.InsertText is not null)
            .Take(MaximumPickerItems)
            .Select(static item => new PluginUiPickerItem(Line(item.Label, MaximumLabelUnits),
                item.Description is null ? null : Line(item.Description, MaximumLabelUnits), DesktopPluginUi.Cut(item.InsertText, MaximumInsertUnits)))]);
    }

    /// <summary>What plugins ask of the page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<PluginUiEvent> Watch(PluginUiWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.PluginUiEvent);
    }

    internal async IAsyncEnumerable<PluginUiEvent> WatchAsync(PluginUiWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_ui is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // Dialogs wait for their answer, so nothing is dropped; a page that stops reading stops the plugins that ask.
        var events = Channel.CreateUnbounded<PluginUiEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var registration = _ui.Watch(value => events.Writer.TryWrite(value));
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    /// <summary>Answers a dialog or a prompt request.</summary>
    [NeoRpcMethod("respond")]
    public PluginUiStatusResponse Respond(PluginUiAnswer request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_ui is null) return new("unavailable");
        if (request.RequestId is not { Length: > 0 and <= 64 } || request.Text is { Length: > DesktopPluginUi.MaximumTextUnits } || !ValidFields(request.Values)) return new("invalid_request");
        return new(_ui.Respond(request) ? "ok" : "unknown");
    }

    /// <summary>Runs the action of an element of an open HTML dialog.</summary>
    [NeoRpcMethod("dialogAction")]
    public async Task<PluginUiActionResponse> DialogActionAsync(PluginUiActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_ui is null) return new("unavailable", null, false);
        if (request.RequestId is not { Length: > 0 and <= 64 } || request.Action is not { Length: > 0 and <= MaximumLabelUnits }
            || request.Value is { Length: > MaximumFieldUnits } || !ValidFields(request.Values)) return new("invalid_request", null, false);
        var (status, html, closed) = await _ui.ActionAsync(request.RequestId, request.Action, request.Value, request.Values, cancellationToken).ConfigureAwait(false);
        _ui.Refresh();
        return new(status, html, closed);
    }

    /// <summary>Cancels the commands still running and waits for them.</summary>
    internal async Task CloseAsync()
    {
        Task[] running;
        lock (_gate)
        {
            _lifetime.Cancel();
            running = [.. _commands];
        }

        _ui?.Dispose(); // A command waiting in a dialog gets no answer and ends.
        try { await Task.WhenAll(running).ConfigureAwait(false); } catch (Exception) { /* Each command already reported its own failure. */ }
    }

    private async Task RunCommandAsync(PluginCommandContribution command, PluginAdapterOperationOptions options, DesktopPluginScope pane)
    {
        var ui = _ui!;
        ui.Enter(pane);
        try
        {
            var (result, diagnostics) = await _plugins!.Adapter.ExecuteCommandAsync(_plugins.ActivePlugins, command, options, _lifetime.Token).ConfigureAwait(false);
            if (diagnostics.Count > 0)
            {
                ui.NotifyProblem($"The plugin command '{command.Name}' failed. See Application Logs.");
                return;
            }

            if (result.Disposition == PluginCommandDisposition.NotHandled)
            {
                ui.NotifyProblem($"The plugin command '{command.Name}' was not handled.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.UserMessage)) await ui.NotifyAsync(result.UserMessage, _lifetime.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.PromptText))
                await ui.PromptAsync(result.EnqueuePrompt ? "enqueue" : "send", result.PromptText, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* The application is closing. */ }
        catch (Exception exception)
        {
            LogManager.GetLogger("CodeAlta.Desktop.Plugins").Error(exception, $"Plugin command '{command.Name}' failed");
        }
        finally
        {
            // What the plugin shows usually changed with what its command did.
            ui.Refresh();
        }
    }

    private async ValueTask<(string Status, PluginAdapterOperationOptions? Options)> ResolveAsync(string? epoch, string? projectId, string? sessionId, CancellationToken cancellationToken)
    {
        if (_projects is null || _plugins is null || _ui is null) return ("unavailable", null);
        if (!string.Equals(epoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null);
        if (sessionId is { } session && (session.Length is 0 or > ComposerStatusService.MaximumSessionIdUnits || session.Any(char.IsControl))) return ("invalid_request", null);
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return (project.Status, null);
        return ("ok", new PluginAdapterOperationOptions { ProjectId = projectId, ProjectPath = project.Root, HasInteractiveUi = true });
    }

    private static string PluginName(IReadOnlyList<ActivePluginInstance> active, PluginContributionRegistration registration)
    {
        var plugin = active.FirstOrDefault(candidate => string.Equals(candidate.Descriptor.RuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal));
        return Line(plugin?.Descriptor.DisplayName ?? registration.Handle.PluginRuntimeKey, MaximumLabelUnits);
    }

    // A command is typed after a slash: letters, digits and separators only.
    private static bool ValidName(string? name)
        => name is { Length: > 0 and <= 64 } && name.All(static value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.');

    private static bool ValidFields(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count <= MaximumFields && values.All(static pair => pair.Key is { Length: > 0 and <= MaximumLabelUnits } && pair.Value is { Length: <= MaximumFieldUnits });

    private static string Line(string text, int maximum)
        => DesktopPluginUi.Cut(string.Concat(text.Select(static value => char.IsControl(value) ? ' ' : value)).Trim(), maximum);
}

/// <summary>Names the pane a request is for.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The pane's project, or null for a pane without one.</param>
/// <param name="SessionId">The pane's session, or null for a pane that has none yet.</param>
internal sealed record PluginUiScopeRequest(string? ExpectedEpoch, string? ProjectId, string? SessionId = null);

/// <summary><c>ok</c> with the commands and pickers, or a refusal code with none.</summary>
/// <param name="Regions">A plugin has content for the regions around the prompt.</param>
internal sealed record PluginUiContributionsResponse(string Status, string? ProjectId, PluginUiCommand[] Commands, PluginUiPicker[] Pickers, bool Regions);

/// <summary>One plugin command.</summary>
/// <param name="Id">The identity to give back to <c>invokeCommand</c>.</param>
/// <param name="PluginKey">The plugin, as named by <see cref="PluginUiContent.PluginKey"/>.</param>
/// <param name="Plugin">The display name of the plugin.</param>
/// <param name="Name">The slash name.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">The description.</param>
/// <param name="Group">An optional grouping label.</param>
/// <param name="Search">Extra words the palette matches.</param>
/// <param name="Keys">The key binding as text (<c>Ctrl+G Ctrl+Y</c>), or null.</param>
/// <param name="Palette">Shown in the command palette.</param>
/// <param name="Help">Shown in the shortcut help.</param>
/// <param name="NeedsProject">Runs only for a pane with a project.</param>
/// <param name="NeedsSession">Runs only for a pane with a session.</param>
/// <param name="NeedsIdle">Runs only while the session is idle.</param>
/// <param name="NeedsBusy">Runs only while the session is running.</param>
internal sealed record PluginUiCommand(string Id, string PluginKey, string Plugin, string Name, string Label, string Description, string? Group, string? Search,
    string? Keys, bool Palette, bool Help, bool NeedsProject, bool NeedsSession, bool NeedsIdle, bool NeedsBusy);

/// <summary>One prompt picker: the character that opens it and its title.</summary>
internal sealed record PluginUiPicker(string Id, string Plugin, string Trigger, string Title, string? Placeholder);

/// <summary>Asks for the buttons at one place, or at every place, for a context.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Place">The place (<c>TitleBar</c>, <c>Rail</c>, <c>ProjectMenu</c> or <c>SessionMenu</c>), or null for every place.</param>
/// <param name="SpaceId">The space the window shows, or null.</param>
/// <param name="ProjectId">The project of the context: the selected one, or the one of a row.</param>
/// <param name="SessionId">The session of the context: the selected one, or the one of a row.</param>
internal sealed record PluginUiButtonsRequest(string? ExpectedEpoch, string? Place, string? SpaceId, string? ProjectId, string? SessionId = null);

/// <summary><c>ok</c> with the buttons in the order of the contributions, or a refusal code with none.</summary>
internal sealed record PluginUiButtonsResponse(string Status, string? Place, PluginUiWindowButton[] Buttons);

/// <summary>One button of a plugin, with the state the plugin gave for the context.</summary>
/// <param name="Id">The identity of the contribution.</param>
/// <param name="PluginKey">The runtime key of the plugin, which the page keeps the user's choice to hide the button under.</param>
/// <param name="PluginId">The id Settings lists the plugin under.</param>
/// <param name="Plugin">The display name of the plugin.</param>
/// <param name="ButtonId">The identifier of the button in its plugin.</param>
/// <param name="Place"><c>TitleBar</c>, <c>Rail</c>, <c>ProjectMenu</c> or <c>SessionMenu</c>.</param>
/// <param name="Icon">The icon as the plugin named it: a Lucide icon, a brand logo or the path of a file of the package.</param>
/// <param name="IconData">For a file, the clean SVG as a data URL; null when the file is not usable, and for a name.</param>
/// <param name="Label">The tooltip, the accessible name and the text of a menu line.</param>
/// <param name="CommandId">The identity to give back to <c>invokeCommand</c> for a button that runs a command, or null.</param>
/// <param name="Canvas">The canvas a button opens, or null.</param>
/// <param name="CanvasScope"><c>Application</c>, <c>Project</c> or <c>Session</c> for a button that opens a canvas, or null.</param>
/// <param name="Badge"><c>none</c>, <c>count</c>, <c>dot</c> or <c>busy</c>.</param>
/// <param name="Count">The number of a <c>count</c> badge.</param>
/// <param name="Tone"><c>Info</c>, <c>Success</c>, <c>Warning</c>, <c>Error</c> or <c>Muted</c>.</param>
/// <param name="Hidden">The plugin leaves the button out for now.</param>
/// <param name="Disabled">The button cannot be used for now: the plugin says so, or the context lacks what the command or the canvas needs.</param>
/// <param name="Tooltip">A tooltip that replaces the label, or null.</param>
internal sealed record PluginUiWindowButton(string Id, string PluginKey, string PluginId, string Plugin, string ButtonId, string Place, string Icon, string? IconData, string Label,
    string? CommandId, string? Canvas, string? CanvasScope, string Badge, int Count, string Tone, bool Hidden, bool Disabled, string? Tooltip);

/// <summary><c>ok</c> with the contents in display order, or a refusal code with none.</summary>
internal sealed record PluginUiRegionsResponse(string Status, string? ProjectId, string? SessionId, PluginUiContent[] Items);

/// <summary>One content of a region: exactly one of the HTML fragment, the Markdown and the text is set.</summary>
/// <param name="Id">The contribution.</param>
/// <param name="PluginKey">The plugin, as named by <c>data-alta-command</c> lookups.</param>
/// <param name="Region"><c>footer</c> (above the prompt), <c>bar</c> or <c>status</c> (in the status line).</param>
/// <param name="Html">The HTML fragment, or null.</param>
/// <param name="Markdown">The Markdown, or null.</param>
/// <param name="Text">The text, or null.</param>
internal sealed record PluginUiContent(string Id, string PluginKey, string Region, string? Html, string? Markdown, string? Text)
{
    /// <summary>The path of the module that draws the fragment, on the origin of the application, or null for a fragment alone.</summary>
    public string? Script { get; init; }

    /// <summary>Why the script is not served, or null.</summary>
    public string? ScriptProblem { get; init; }
}

/// <param name="CommandId">The command, as listed by <c>contributions</c>.</param>
/// <param name="SessionBusy">The pane's session is running.</param>
/// <param name="DraftText">The pane's prompt draft.</param>
/// <param name="SpaceId">The space the window shows, or null; the command sees it as the space of its scope.</param>
internal sealed record PluginUiInvokeRequest(string? ExpectedEpoch, string? CommandId, string? ProjectId, string? SessionId, bool SessionBusy, string? DraftText, string? SpaceId = null);

/// <summary>A status code alone: <c>ok</c>, <c>started</c> or a refusal.</summary>
internal sealed record PluginUiStatusResponse(string Status);

internal sealed record PluginUiPickerRequest(string? ExpectedEpoch, string? PickerId, string? ProjectId, string? SessionId, string? Query);
internal sealed record PluginUiPickerResponse(string Status, string? PickerId, PluginUiPickerItem[] Items);
internal sealed record PluginUiPickerItem(string Label, string? Description, string InsertText);

internal sealed record PluginUiWatchRequest(string? ExpectedEpoch);

/// <summary>
/// One request from a plugin to the page.
/// </summary>
/// <param name="Kind">
/// <c>notify</c> (a message), <c>ask</c> (a dialog to show and answer), <c>close</c> (a request that no longer
/// waits), <c>prompt</c> (send, queue or steer a prompt, or compact; to answer), <c>draft</c> (replace a prompt draft)
/// or <c>refresh</c> (read again what plugins show: a command or a dialog action of a plugin ended).
/// </param>
internal sealed record PluginUiEvent(string Kind)
{
    /// <summary>The identity to answer with, for <c>ask</c>, <c>prompt</c> and <c>close</c>.</summary>
    public string? RequestId { get; init; }

    /// <summary>For <c>ask</c>: <c>message</c>, <c>confirm</c>, <c>input</c>, <c>edit</c>, <c>select</c> or <c>html</c>.</summary>
    public string? Dialog { get; init; }

    public string? Title { get; init; }

    public string? Message { get; init; }

    /// <summary>The initial text of an input or editor, or the prompt text.</summary>
    public string? Text { get; init; }

    /// <summary>The HTML fragment of an <c>html</c> dialog.</summary>
    public string? Html { get; init; }

    /// <summary>The path of the module that draws an <c>html</c> dialog, on the origin of the application, or null for a fragment alone.</summary>
    public string? Script { get; init; }

    /// <summary>Why the script a dialog asked for is not served, or null.</summary>
    public string? ScriptProblem { get; init; }

    public PluginUiChoice[]? Items { get; init; }

    public PluginUiButton[]? Buttons { get; init; }

    /// <summary>The dialog has an action handler: its <c>data-alta-action</c> elements call <c>dialogAction</c>.</summary>
    public bool Actions { get; init; }

    /// <summary>For <c>prompt</c>: <c>send</c>, <c>enqueue</c>, <c>steer</c> or <c>compact</c>.</summary>
    public string? Mode { get; init; }

    /// <summary>The session a prompt or a draft is for; null means the focused pane.</summary>
    public string? SessionId { get; init; }

    public string? ProjectId { get; init; }

    /// <summary>For <c>notify</c>: <c>warning</c>, or null for an ordinary message.</summary>
    public string? Tone { get; init; }
}

internal sealed record PluginUiChoice(string Label, string? Description, bool Selected);
internal sealed record PluginUiButton(string Name, string Label, bool IsDefault, bool IsCancel);

/// <summary>The page's answer to an <c>ask</c> or a <c>prompt</c>.</summary>
/// <param name="Button">The name of the activated button.</param>
/// <param name="Cancelled">The dialog was dismissed, or the prompt was not taken.</param>
/// <param name="Text">The text of an input or editor.</param>
/// <param name="SelectedIndex">The chosen item of a selection.</param>
/// <param name="Values">The named fields of an HTML dialog.</param>
internal sealed record PluginUiAnswer(string? RequestId, string? Button, bool Cancelled, string? Text, int? SelectedIndex, Dictionary<string, string>? Values);

internal sealed record PluginUiActionRequest(string? RequestId, string? Action, string? Value, Dictionary<string, string>? Values);

/// <param name="Status"><c>ok</c>, <c>unknown</c>, <c>unsupported</c>, <c>failed</c> or a refusal.</param>
/// <param name="Html">The new content of the dialog, or null to keep it.</param>
/// <param name="Closed">The dialog closed.</param>
internal sealed record PluginUiActionResponse(string Status, string? Html, bool Closed);
