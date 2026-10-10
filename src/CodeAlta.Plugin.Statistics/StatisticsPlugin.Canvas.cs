using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Plugin.Statistics.Canvas;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StatisticsCall))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class StatisticsCanvasJsonContext : JsonSerializerContext;

public sealed partial class StatisticsPlugin
{
    /// <summary>The identifier of the canvas of the statistics.</summary>
    internal const string CanvasId = "statistics";

    /// <summary>The identifier of the button of the title bar, and the name of the module the application builds for the canvas.</summary>
    internal const string ButtonId = "statistics";

    /// <summary>The name of the command that opens the canvas; <c>/statistics</c> in the palette.</summary>
    internal const string OpenCommandName = "statistics";

    /// <summary>The name of the command of the menu of a project: the canvas, limited to the project.</summary>
    internal const string ProjectCommandName = "statistics-project";

    /// <summary>The start of the key of a canvas limited to a project: <c>project:&lt;id&gt;</c>.</summary>
    internal const string ProjectKeyPrefix = "project:";

    /// <summary>How long what the engine says in a burst is gathered before a canvas is told.</summary>
    internal static readonly TimeSpan EventDelay = TimeSpan.FromMilliseconds(250);

    private AltaProjectDirectory? _directory;
    private StatisticsCanvasRpc? _rpc;
    private int _buttonKind;

    /// <summary>Gets a value indicating whether this plugin reads the sessions: CodeAlta Desktop with an application database. Only then it has a canvas, a button and a command.</summary>
    private bool ReadsSessions => _journals is not null && Context.Host.Frontend == PluginFrontends.Desktop && Services.Database.HasDatabase;

    /// <inheritdoc />
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        if (!ReadsSessions)
        {
            yield break;
        }

        yield return new PluginCanvasContribution
        {
            Id = CanvasId,
            Title = "Statistics",
            Description = "Your sessions in numbers and charts: activity, models and tokens, cost, tools, prompts, agents, code, projects, sessions and health.",
            Icon = "chart-column",
            Scope = PluginCanvasScope.Application,
            Open = OpenCanvasAsync,
            Describe = DescribeCanvasAsync,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        if (!ReadsSessions)
        {
            yield break;
        }

        // The button at the top right, before the space switch. A ring while the history is read, a dot while the choice waits.
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, ButtonId, "chart-column", "Statistics") with
        {
            Canvas = CanvasId,
            GetState = _ => GetButtonState(),
        };
        yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "statistics-project", "chart-column", "Statistics of this project") with
        {
            Command = ProjectCommandName,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        if (!ReadsSessions)
        {
            yield break;
        }

        yield return Command.Shell(OpenCommandName, "Opens the Statistics canvas: your sessions in numbers and charts.", OpenCommandAsync) with
        {
            Label = "Statistics",
            SearchText = "statistics usage tokens cost charts activity",
            KeyBinding = new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('C')),
        };

        // Run by the line of a project menu, for the project of the row.
        yield return Command.Shell(ProjectCommandName, "Opens the Statistics canvas for one project.", ProjectCommandAsync) with
        {
            Label = "Statistics of the project",
            Availability = PluginCommandAvailability.ProjectSelected,
            ShowInCommandPalette = false,
            ShowInCommandBar = false,
            ShowInHelp = false,
        };
    }

    /// <summary>Gets the state of the button of the title bar from the state of the history: a ring while it is read, a dot while the choice waits.</summary>
    internal PluginButtonState? GetButtonState()
    {
        return Volatile.Read(ref _buttonKind) switch
        {
            1 => new PluginButtonState { Badge = PluginButtonBadge.Busy, Tooltip = "Statistics: reading the history" },
            2 => new PluginButtonState { Badge = PluginButtonBadge.Dot, Tooltip = "Statistics: choose how much history to read" },
            _ => null,
        };
    }

    private static int ButtonKindOf(StatisticsStatus? status)
        => status?.State switch
        {
            HistoryState.Reading => 1,
            HistoryState.NeedsChoice => 2,
            _ => 0,
        };

    // The host reads the buttons again only when told: the ring and the dot follow the state, and the numbers of a reading in progress change nothing.
    internal void OnButtonStatusChanged(StatisticsStatus status)
    {
        var kind = ButtonKindOf(status);
        if (Interlocked.Exchange(ref _buttonKind, kind) != kind)
        {
            Services.Ui.InvalidateButtons();
        }
    }

    private StatisticsCanvasRpc Rpc => _rpc ??= new StatisticsCanvasRpc(() => _engine, WriteContextAsync, Logger);

    private async ValueTask<PluginCanvasView> OpenCanvasAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        // The calls of the script are registered before the view that names the script is returned.
        canvas.Rpc.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = StatisticsCanvasJsonContext.Default };
        Rpc.Register(canvas.Rpc);
        FollowEngine(canvas);
        return new PluginCanvasView
        {
            Fragment = "<div class=\"alta-muted\">Loading the statistics…</div>",
            Script = PluginScript.App("statistics"),
            Title = await TitleOfAsync(canvas.Key, cancellationToken).ConfigureAwait(false),
        };
    }

    // What the engine says goes to the script of this tab, a moment after, until the tab is closed.
    private void FollowEngine(PluginCanvasContext canvas)
    {
        if (_engine is not { } engine)
        {
            return;
        }

        var pump = new StatisticsEventPump(canvas.Rpc, () => canvas.IsVisible, EventDelay, _time, Logger);
        Action<StatisticsStatus> onStatus = pump.Status;
        Action<StatisticsDataChange> onData = pump.Data;
        Action<bool> onVisible = pump.VisibilityChanged;
        engine.StatusChanged += onStatus;
        engine.DataChanged += onData;
        canvas.VisibilityChanged += onVisible;
        canvas.Closed.Register(() =>
        {
            engine.StatusChanged -= onStatus;
            engine.DataChanged -= onData;
            canvas.VisibilityChanged -= onVisible;
            pump.Dispose();
        });
    }

    private async ValueTask<string> TitleOfAsync(string? key, CancellationToken cancellationToken)
    {
        if (ProjectOfKey(key) is not { } project || _directory is not { } directory)
        {
            return "Statistics";
        }

        var projects = await directory.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var name = projects.FirstOrDefault(item => string.Equals(item.Id, project, StringComparison.OrdinalIgnoreCase))?.Name;
        return name is null ? "Statistics" : "Statistics: " + name;
    }

    /// <summary>Gets the project a key of a canvas names (<c>project:&lt;id&gt;</c>), or null.</summary>
    internal static string? ProjectOfKey(string? key)
        => key is not null && key.StartsWith(ProjectKeyPrefix, StringComparison.Ordinal) && key.Length > ProjectKeyPrefix.Length ? key[ProjectKeyPrefix.Length..] : null;

    private ValueTask<string?> DescribeCanvasAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        var status = _engine?.Status;
        var builder = new StringBuilder("# Statistics\n\n");
        builder.Append(status?.State switch
        {
            HistoryState.NeedsChoice => "Waiting for the choice of how much history to read.",
            HistoryState.Reading => $"Reading the history: {status.SessionsDone} of {status.SessionsTotal} sessions.",
            HistoryState.Paused => "The reading of the history is paused.",
            HistoryState.StoppedHere => "The reading of the history stopped; the statistics start where it did.",
            HistoryState.Done => "The history is read and the statistics follow the sessions as they run.",
            HistoryState.Failed => "The statistics could not start.",
            _ => "The statistics are starting.",
        });
        builder.Append("\n\nThe numbers are those of `alta statistics summary`, `series`, `top` and `session`. Open it with the key `project:<project id>` for the statistics of one project.");
        return new ValueTask<string?>(builder.ToString());
    }

    private async ValueTask<PluginCommandResult> OpenCommandAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var result = await Services.Canvases.OpenAsync(CanvasId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Statistics canvas cannot be shown here.");
    }

    private async ValueTask<PluginCommandResult> ProjectCommandAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId))
        {
            return PluginCommandResult.Message("Choose a project first.");
        }

        var result = await Services.Canvases.OpenAsync(CanvasId, new PluginCanvasOpenOptions { Key = ProjectKeyPrefix + context.ProjectId }, cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Statistics canvas cannot be shown here.");
    }

    // The spaces and the projects the filters of the page choose among (the default space has every project), and the first day of the
    // week the questions use when they name none: the page lays its weeks out from it, as `alta statistics` cuts them.
    private async ValueTask<byte[]> WriteContextAsync(CancellationToken cancellationToken)
    {
        var directory = _directory;
        var projects = directory is null ? [] : await directory.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var spaces = directory is null ? [] : await directory.ListSpacesAsync(cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("weekStart", StatisticsQueries.DefaultWeekStart.ToString());
            writer.WriteStartArray("spaces");
            foreach (var space in spaces)
            {
                writer.WriteStartObject();
                writer.WriteString("id", space.Id);
                writer.WriteString("name", space.Name);
                writer.WriteBoolean("isDefault", space.IsDefault);
                writer.WriteStartArray("projectIds");
                foreach (var project in projects.Where(project => space.IsDefault || project.SpaceIds.Contains(space.Id, StringComparer.Ordinal)))
                {
                    writer.WriteStringValue(project.Id);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("projects");
            foreach (var project in projects)
            {
                writer.WriteStartObject();
                writer.WriteString("id", project.Id);
                writer.WriteString("name", project.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
