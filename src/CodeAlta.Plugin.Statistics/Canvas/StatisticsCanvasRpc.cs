using System.Text;
using System.Text.Json;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Statistics.Canvas;

/// <summary>What a script of the canvas sends to a call of the statistics: one record for every call, with the fields the call reads.</summary>
internal sealed record StatisticsCall
{
    /// <summary>Gets the request of a question: <c>{ "period": "30d", "frequency": "week", "filter": {...} }</c>, written by the page as <see cref="StatisticsJson.ParseRequest"/> reads it.</summary>
    public JsonElement? Request { get; init; }

    /// <summary>Gets the metric of a series.</summary>
    public string? Metric { get; init; }

    /// <summary>Gets the group of a series.</summary>
    public string? Group { get; init; }

    /// <summary>Gets the kind of a ranking, or of a choice of history.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets the measure of a ranking.</summary>
    public string? By { get; init; }

    /// <summary>Gets the sort of a table.</summary>
    public string? Sort { get; init; }

    /// <summary>Gets the measure of a distribution.</summary>
    public string? Measure { get; init; }

    /// <summary>Gets the subject of a distribution.</summary>
    public string? Subject { get; init; }

    /// <summary>Gets the list of names of a details question.</summary>
    public string? List { get; init; }

    /// <summary>Gets the session of a session question.</summary>
    public string? Id { get; init; }

    /// <summary>Gets a value indicating whether a session question includes the sessions the session created.</summary>
    public bool WithChildren { get; init; }

    /// <summary>Gets the number of days of a choice of history.</summary>
    public int? Days { get; init; }
}

/// <summary>
/// The calls the script of the Statistics canvas makes to the plugin: thin wrappers over the questions and the controls of
/// <see cref="IStatisticsService"/>, whose results are written by <see cref="StatisticsJson"/> so that the page reads exactly the JSON of
/// <c>alta statistics</c> and of the golden tests.
/// </summary>
/// <remarks>
/// A question is bounded in what it asks of the database (a few at a time), in what it returns (under the cap of the transport), and in
/// what it says on failure: a stable code and a sentence made to be shown, never the text of an unexpected exception.
/// </remarks>
internal sealed class StatisticsCanvasRpc
{
    /// <summary>The name of the event that carries the changes of the status and of the numbers to the script.</summary>
    public const string EventsName = "statistics.events";

    /// <summary>The most bytes of JSON a result may have: under the 4 MiB the transport carries, with room for its envelope.</summary>
    public const int MaximumResultBytes = 3 * 1024 * 1024;

    /// <summary>The most questions that read the database at once, whatever the number of canvases.</summary>
    public const int MaximumConcurrentQuestions = 4;

    private readonly Func<StatisticsEngine?> _engine;
    private readonly Func<CancellationToken, ValueTask<byte[]>> _context;
    private readonly SemaphoreSlim _questions = new(MaximumConcurrentQuestions, MaximumConcurrentQuestions);
    private readonly Logger? _logger;
    private readonly int _maximumResultBytes;

    /// <summary>Initializes the calls.</summary>
    /// <param name="engine">Gives the engine of the plugin, or null while there is none.</param>
    /// <param name="context">Writes the JSON of the spaces and the projects, for the filters of the page.</param>
    /// <param name="logger">The logger of the plugin, or null.</param>
    /// <param name="maximumResultBytes">The most bytes of JSON a result may have; the default is <see cref="MaximumResultBytes"/>.</param>
    public StatisticsCanvasRpc(Func<StatisticsEngine?> engine, Func<CancellationToken, ValueTask<byte[]>> context, Logger? logger = null, int maximumResultBytes = MaximumResultBytes)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(context);
        _engine = engine;
        _context = context;
        _logger = logger;
        _maximumResultBytes = maximumResultBytes;
    }

    /// <summary>Registers the calls of the canvas, before its view is returned.</summary>
    /// <param name="rpc">The registry of the instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rpc"/> is null.</exception>
    public void Register(IPluginCanvasRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        Question(rpc, "statistics.summary", (queries, call, token) => queries.SummaryAsync(Request(call), token));
        Question(rpc, "statistics.series", (queries, call, token) => queries.SeriesAsync(Request(call), Required(call.Metric, "metric"), Optional(call.Group), token));
        Question(rpc, "statistics.top", (queries, call, token) => queries.TopAsync(Request(call), Required(call.Kind, "kind"), Required(call.By, "by"), token));
        Question(rpc, "statistics.tools", (queries, call, token) => queries.ToolsAsync(Request(call), token));
        Question(rpc, "statistics.models", (queries, call, token) => queries.ModelsAsync(Request(call), token));
        Question(rpc, "statistics.projects", (queries, call, token) => queries.ProjectsAsync(Request(call), token));
        Question(rpc, "statistics.sessions", (queries, call, token) => queries.SessionsAsync(Request(call), Required(call.Sort, "sort"), token));
        Question(rpc, "statistics.session", async (queries, call, token) => await queries.SessionAsync(Required(call.Id, "id"), call.WithChildren, token).ConfigureAwait(false)
            ?? throw new PluginRpcException("not_found", "No session has this identifier."));
        Question(rpc, "statistics.distribution", (queries, call, token) => queries.DistributionAsync(Request(call), Required(call.Measure, "measure"), Optional(call.Subject), token));
        Question(rpc, "statistics.calendar", (queries, call, token) => queries.CalendarAsync(Request(call), token));
        Question(rpc, "statistics.week-hour", (queries, call, token) => queries.WeekHourAsync(Request(call), token));
        Question(rpc, "statistics.records", (queries, call, token) => queries.RecordsAsync(Request(call), token));
        Question(rpc, "statistics.health", (queries, call, token) => queries.HealthAsync(Request(call), token));
        Question(rpc, "statistics.details", (queries, call, token) => queries.DetailsAsync(Request(call), Required(call.List, "list"), token));
        Question(rpc, "statistics.runs", (queries, call, token) => queries.RunsAsync(Request(call), Required(call.Sort, "sort"), token));

        // The state of the history and its controls. Each answers with the status after the call.
        Control(rpc, "statistics.status", async (engine, _, token) =>
        {
            await engine.InitializeAsync(token).ConfigureAwait(false);
            // The first-time card is told what exists; the listing is cached for half a minute and read again past it.
            return await engine.RefreshOverviewAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        });
        Control(rpc, "statistics.choose-history", (engine, call, token) => engine.ChooseHistoryAsync(Choice(call), token));
        Control(rpc, "statistics.pause", (engine, _, token) => engine.PauseAsync(token));
        Control(rpc, "statistics.resume", (engine, _, token) => engine.ResumeAsync(token));
        Control(rpc, "statistics.stop-here", (engine, _, token) => engine.StopHereAsync(token));
        Control(rpc, "statistics.reset", (engine, _, token) => engine.ResetAsync(token));
        rpc.Handle<StatisticsCall, JsonElement>("statistics.forget-deleted", async (_, token) =>
        {
            var engine = Engine();
            await engine.InitializeAsync(token).ConfigureAwait(false);
            var count = await engine.ForgetDeletedAsync(token).ConfigureAwait(false);
            return Parse(Encoding.UTF8.GetBytes($"{{\"count\":{count}}}"));
        });
        rpc.Handle<StatisticsCall, JsonElement>("statistics.context", async (_, token) =>
        {
            Engine();
            return Parse(await _context(token).ConfigureAwait(false));
        });
    }

    /// <summary>Gets the stable error of a question that failed for a reason the person can act on, or null for any other failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The error to give the script; null when the failure is not the person's to fix, which the host then reports as <c>internal_error</c>.</returns>
    public static PluginRpcException? Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            PluginRpcException known => known,
            // The queries name a period, a metric or a group that does not exist in a sentence made to be shown.
            ArgumentException argument => new PluginRpcException("invalid_request", WithoutParameter(argument.Message)),
            _ => null,
        };
    }

    private void Question<T>(IPluginCanvasRpc rpc, string name, Func<StatisticsQueries, StatisticsCall, CancellationToken, ValueTask<T>> ask)
    {
        rpc.Handle<StatisticsCall, JsonElement>(name, async (call, token) =>
        {
            var engine = Engine();
            // The tables exist before the first question, whatever the delay the engine waits before it reads.
            await engine.InitializeAsync(token).ConfigureAwait(false);
            await _questions.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await ask(engine.Queries, call, token).ConfigureAwait(false);
                return Result(result);
            }
            catch (Exception exception) when (Describe(exception) is { } known)
            {
                throw known;
            }
            finally
            {
                _questions.Release();
            }
        });
    }

    private void Control(IPluginCanvasRpc rpc, string name, Func<StatisticsEngine, StatisticsCall, CancellationToken, ValueTask<StatisticsStatus>> run)
    {
        rpc.Handle<StatisticsCall, JsonElement>(name, async (call, token) =>
        {
            try
            {
                var engine = Engine();
                return Result(await run(engine, call, token).ConfigureAwait(false));
            }
            catch (Exception exception) when (Describe(exception) is { } known)
            {
                throw known;
            }
        });
    }

    private StatisticsEngine Engine()
        => _engine() ?? throw new PluginRpcException("unavailable", "The statistics are not running.", retryable: true);

    private static StatisticsRequest Request(StatisticsCall call)
        => call.Request switch
        {
            { ValueKind: JsonValueKind.Object } request => StatisticsJson.ParseRequest(request.GetRawText()),
            null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => new StatisticsRequest(),
            _ => throw new PluginRpcException("invalid_request", "The request must be an object."),
        };

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new PluginRpcException("invalid_request", $"The call needs '{name}'.") : value;

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static HistoryChoice Choice(StatisticsCall call)
        => call.Kind switch
        {
            "all" => HistoryChoice.All,
            "fromToday" => HistoryChoice.FromToday,
            "days" when call.Days is >= 1 and <= 3650 => HistoryChoice.LastDays(call.Days.Value),
            _ => throw new PluginRpcException("invalid_request", "A choice is 'all', 'fromToday', or 'days' with a number of days from 1 to 3650."),
        };

    private JsonElement Result<T>(T result)
    {
        var utf8 = Encoding.UTF8.GetBytes(StatisticsJson.Serialize(result));
        if (utf8.Length > _maximumResultBytes)
        {
            _logger?.Warn($"A statistics result of {utf8.Length} bytes was refused: it does not fit the transport.");
            throw new PluginRpcException("result_too_large", "This question has too many rows to show: choose a shorter period or add a filter.");
        }

        return Parse(utf8);
    }

    private static JsonElement Parse(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }

    private static string WithoutParameter(string message)
    {
        var index = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return index > 0 ? message[..index] : message;
    }
}
