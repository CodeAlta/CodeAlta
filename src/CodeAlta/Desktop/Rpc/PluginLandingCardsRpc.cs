using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The cards that plugins pin on the landing page.
internal sealed partial class PluginUiService
{
    /// <summary>The most cards one reading returns.</summary>
    internal const int MaximumLandingCards = 24;

    /// <summary>The most commands of its plugin that a card is told of.</summary>
    internal const int MaximumLandingCardCommands = 32;

    /// <summary>How long one card may take to say what it shows. A test shortens it.</summary>
    internal TimeSpan LandingCardTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks the plugins for the cards they pin on the landing page of a space: the title, the icon, the HTML fragment and the actions of
    /// each. A card that its plugin leaves out is not listed; one that failed is listed as <c>failed</c>, without content. An action that
    /// names a command or a canvas that its plugin does not have is left out.
    /// </summary>
    [NeoRpcMethod("landingCards", TimeoutMilliseconds = 20_000)]
    public async Task<PluginUiLandingCardsResponse> LandingCardsAsync(PluginUiLandingCardsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginUiLandingCardsResponse Failed(string status) => new(status, request.SpaceId, []);
        if (request.SpaceId is { } space && (space.Length is 0 or > MaximumIdUnits || space.Any(char.IsControl))) return Failed("invalid_request");
        var scope = await ResolveAsync(request.ExpectedEpoch, null, null, cancellationToken).ConfigureAwait(false);
        if (scope.Status != "ok") return Failed(scope.Status);
        try
        {
            var projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
            // Filter before calling plugins, not just before rendering: invisible projects must not start expensive callbacks.
            // The default space, and a window without spaces, include every existing, unarchived project.
            var inSpace = projects.Where(project => !project.Archived && (request.SpaceId is null
                    || string.Equals(request.SpaceId, SpaceDescriptor.DefaultId, StringComparison.Ordinal)
                    || project.Spaces.Contains(request.SpaceId, StringComparer.Ordinal)))
                .Select(static project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var active = _plugins!.ActivePlugins;
            var entries = await _plugins.Adapter.GetLandingCardEntriesAsync(active, request.SpaceId, inSpace, LandingCardTimeout, cancellationToken).ConfigureAwait(false);
            var cards = new List<PluginUiLandingCard>();
            foreach (var entry in entries)
            {
                if (cards.Count == MaximumLandingCards) break;
                var project = entry.Context.ProjectId is { } projectId ? projects.FirstOrDefault(candidate => string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase)) : null;
                // The card of a plugin of a project that is archived, or no longer in the catalog, has nothing to be about on the page.
                if (entry.Context.ProjectId is not null && project is not { Archived: false }) continue;
                try
                {
                    cards.Add(LandingCard(entry, active, project));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // What a plugin gave could not be read (a list of actions that throws): its card failed, the others are listed.
                    cards.Add(LandingCard(entry with { Card = null, Failed = true }, active, project));
                }
            }

            return new("ok", request.SpaceId, [.. cards]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed("read_failed"); // A plugin failing in a property getter must not break the page.
        }
    }

    private PluginUiLandingCard LandingCard(PluginLandingCardEntry entry, IReadOnlyList<ActivePluginInstance> active, ProjectDescriptor? project)
    {
        var registration = entry.Registration;
        var pluginKey = registration.Handle.PluginRuntimeKey;
        var plugin = active.FirstOrDefault(candidate => string.Equals(candidate.Descriptor.RuntimeKey, pluginKey, StringComparison.Ordinal));
        var folder = plugin?.SourcePackage?.PackageDirectory;
        var icon = entry.Contribution.Icon?.Trim() is { Length: > 0 } named ? Line(named, MaximumLabelUnits) : null;
        var shown = entry.Card;
        var actions = new List<PluginUiLandingCardAction>();
        var runnable = new List<PluginUiLandingCardCommand>();
        if (shown is not null)
        {
            var options = new PluginAdapterOperationOptions { ProjectId = project?.Id, ProjectPath = project?.ProjectPath, HasInteractiveUi = true };
            var commands = _plugins!.Adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command, options);
            foreach (var action in shown.Actions ?? [])
            {
                if (actions.Count == PluginLandingCardLimits.Actions) break;
                if (action is null || action.Validate() is not null) continue;
                if (LandingAction(action, pluginKey, folder, commands, project) is { } listed) actions.Add(listed);
            }

            // The commands an element of the fragment can name (data-alta-command): those of the plugin of the card, for the project of
            // the card, whatever project the window has selected. A name never reaches a command of another plugin.
            foreach (var command in commands)
            {
                if (runnable.Count == MaximumLandingCardCommands) break;
                var name = ((PluginCommandContribution)command.Contribution).Name;
                if (!string.Equals(command.Handle.PluginRuntimeKey, pluginKey, StringComparison.Ordinal) || !ValidName(name)
                    || runnable.Any(known => string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                runnable.Add(new(name, command.Handle.RuntimeContributionKey));
            }
        }

        return new(registration.Handle.RuntimeContributionKey, pluginKey, PluginId(pluginKey, plugin), PluginName(active, registration), entry.Contribution.Id,
            Line(entry.Contribution.Title, PluginLandingCardLimits.MaximumTitleLength), icon, icon is not null && PluginIcons.IsFile(icon) ? _icons.Read(folder, icon) : null,
            entry.Failed || shown is null ? "failed" : "ok",
            shown is null ? null : DesktopPluginUi.Cut(shown.Html ?? string.Empty, PluginLandingCardLimits.MaximumHtmlLength),
            string.IsNullOrWhiteSpace(shown?.Status) ? null : Line(shown.Status, PluginLandingCardLimits.MaximumLabelLength),
            (shown?.Tone ?? PluginStatusTone.Info).ToString(), project?.Id, project is null ? null : Line(project.DisplayName, MaximumLabelUnits), [.. actions], [.. runnable]);
    }

    private PluginUiLandingCardAction? LandingAction(PluginLandingCardAction action, string pluginKey, string? folder, IReadOnlyList<PluginContributionRegistration> commands, ProjectDescriptor? project)
    {
        string? commandId = null, canvasScope = null;
        var disabled = false;
        if (!string.IsNullOrWhiteSpace(action.Command))
        {
            // A command of the same plugin, by its name as a button names it.
            var command = commands.FirstOrDefault(candidate => string.Equals(candidate.Handle.PluginRuntimeKey, pluginKey, StringComparison.Ordinal)
                && string.Equals(((PluginCommandContribution)candidate.Contribution).Name, action.Command, StringComparison.OrdinalIgnoreCase));
            if (command is null) return null;
            commandId = command.Handle.RuntimeContributionKey;
            var availability = ((PluginCommandContribution)command.Contribution).Availability ?? PluginCommandAvailability.Always;
            // The landing page has no session; it has the project of a plugin of a project.
            disabled = availability.RequiresProject && project is null || availability.RequiresSession || availability.RequiresIdleSession || availability.RequiresBusySession;
        }
        else
        {
            var canvas = _canvases?.Find(pluginKey, action.Canvas ?? string.Empty);
            if (canvas is null || action.Key is { } key && !DesktopCanvases.ValidLine(key, DesktopCanvases.MaximumKeyUnits)) return null;
            canvasScope = canvas.Canvas.Scope.ToString();
            disabled = canvas.Canvas.Scope == PluginCanvasScope.Project && project is null || canvas.Canvas.Scope == PluginCanvasScope.Session;
        }

        var icon = action.Icon?.Trim() is { Length: > 0 } named ? Line(named, MaximumLabelUnits) : null;
        return new(Line(action.Label, PluginLandingCardLimits.MaximumLabelLength), icon, icon is not null && PluginIcons.IsFile(icon) ? _icons.Read(folder, icon) : null,
            commandId, commandId is null ? action.Canvas : null, canvasScope, commandId is null ? action.Key : null, action.Primary, disabled);
    }
}

/// <summary>Asks for the cards that plugins pin on the landing page of a space.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SpaceId">The space the landing page is shown in, or null.</param>
internal sealed record PluginUiLandingCardsRequest(string? ExpectedEpoch, string? SpaceId);

/// <summary><c>ok</c> with the cards in the order of the contributions, or a refusal code with none.</summary>
internal sealed record PluginUiLandingCardsResponse(string Status, string? SpaceId, PluginUiLandingCard[] Cards);

/// <summary>One card of a plugin on the landing page, with what the plugin gave for the space.</summary>
/// <param name="Id">The identity of the contribution.</param>
/// <param name="PluginKey">The runtime key of the plugin: the commands of the fragment are looked up in it.</param>
/// <param name="PluginId">The id Settings lists the plugin under.</param>
/// <param name="Plugin">The display name of the plugin.</param>
/// <param name="CardId">The identifier of the card in its plugin.</param>
/// <param name="Title">The title.</param>
/// <param name="Icon">The icon as the plugin named it, or null.</param>
/// <param name="IconData">For a file, the clean SVG as a data URL; null when the file is not usable, and for a name.</param>
/// <param name="State"><c>ok</c>, or <c>failed</c> when the plugin threw or did not answer in time: the card then has no content.</param>
/// <param name="Html">The HTML fragment, or null for a card that failed.</param>
/// <param name="StatusText">The short text beside the title, or null.</param>
/// <param name="Tone"><c>Info</c>, <c>Success</c>, <c>Warning</c>, <c>Error</c> or <c>Muted</c>: the tone of the status text.</param>
/// <param name="ProjectId">The project of a plugin that belongs to one: its commands and canvases are about it. Null otherwise.</param>
/// <param name="ProjectName">The name of that project, or null.</param>
/// <param name="Actions">The actions at the bottom of the card.</param>
/// <param name="Commands">The commands of the plugin that an element of the fragment can name, for the project of the card.</param>
internal sealed record PluginUiLandingCard(string Id, string PluginKey, string PluginId, string Plugin, string CardId, string Title, string? Icon, string? IconData, string State,
    string? Html, string? StatusText, string Tone, string? ProjectId, string? ProjectName, PluginUiLandingCardAction[] Actions, PluginUiLandingCardCommand[] Commands);

/// <summary>A command of the plugin of a card, as an element of its fragment names it.</summary>
/// <param name="Name">The name of the command.</param>
/// <param name="Id">The identity to give back to <c>invokeCommand</c>.</param>
internal sealed record PluginUiLandingCardCommand(string Name, string Id);

/// <summary>One action of a card: exactly one of the command and the canvas is set.</summary>
/// <param name="Label">The label of the button.</param>
/// <param name="Icon">The icon as the plugin named it, or null.</param>
/// <param name="IconData">For a file, the clean SVG as a data URL; null otherwise.</param>
/// <param name="CommandId">The identity to give back to <c>invokeCommand</c> for an action that runs a command, or null.</param>
/// <param name="Canvas">The canvas an action opens, or null.</param>
/// <param name="CanvasScope"><c>Application</c>, <c>Project</c> or <c>Session</c> for an action that opens a canvas, or null.</param>
/// <param name="Key">The key of the instance of the canvas to open, or null.</param>
/// <param name="Primary">The main action of the card.</param>
/// <param name="Disabled">The action cannot be used here: its command or its canvas needs a project or a session that the landing page does not have.</param>
internal sealed record PluginUiLandingCardAction(string Label, string? Icon, string? IconData, string? CommandId, string? Canvas, string? CanvasScope, string? Key, bool Primary, bool Disabled);
