using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Store;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Statistics;

/// <summary>The record a command of the statistics writes on its error stream when it fails.</summary>
/// <param name="Type">Always <c>alta.error</c>.</param>
/// <param name="Version">The version of the record.</param>
/// <param name="CorrelationId">The correlation of the command.</param>
/// <param name="Code">A stable code, such as <c>usage.invalidQuery</c>.</param>
/// <param name="ExitCode">The exit code of the command.</param>
/// <param name="Message">A sentence made to be shown.</param>
internal sealed record StatisticsCommandError(string Type, int Version, string? CorrelationId, string Code, int ExitCode, string Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StatisticsCommandError))]
internal sealed partial class StatisticsCommandJsonContext : JsonSerializerContext;

/// <summary>
/// The <c>alta statistics</c> commands: the numbers of the Statistics page for agents and for the user, read from the same store, so that
/// a command and the page never disagree. Every command writes one JSONL record, bounded by the limits of the queries.
/// </summary>
internal static class StatisticsCommands
{
    private const int Usage = 2;
    private const int Failure = 1;

    public static Command Create(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Group("statistics", "The statistics of the sessions: how much CodeAlta is used, on what, with which models, tools and cost.");
        command.Add(Summary(plugin, context));
        command.Add(Series(plugin, context));
        command.Add(Top(plugin, context));
        command.Add(Details(plugin, context));
        command.Add(Session(plugin, context));
        command.Add(Status(plugin, context));
        command.Add(History(plugin, context));
        command.Add(StatisticsPlugin.CreateEstimateCommand(context));
        Help(
            command,
            "The numbers are counted from the sessions the user chose to read, and kept up to date while sessions run. Every result says in `query.coverage` whether the history is read for the period, and which filters it could not apply in `query.ignoredFilters`.",
            "A period is `today`, `yesterday`, `7d` (any `Nd`: the last N days, today included), `week`, `month`, `last-month`, `year`, `all`, or `2026-01-01..2026-03-31` (an end left out is today). Days are local days.",
            "A cost is given for each unit: dollars and AI credits never add up. A space filter names the projects the space has today.",
            "Examples: `alta statistics summary --period 7d`; `alta statistics series tokens --period 90d --by week --group model`; `alta statistics top tools --by time --limit 5`; `alta statistics session <id> --with-children`; `alta statistics status`.");
        return command;
    }

    private static Command Summary(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var options = new QueryOptions();
        var command = Leaf("summary", "The numbers of a period at a glance: sessions, runs, active time, prompts you sent, requests, tokens by kind, tool calls, cost by unit.");
        options.AddTo(command, frequency: true);
        command.Add(async (_, _) => await Run(plugin, context, async queries => Write(context, "alta.statistics.summary", await queries.SummaryAsync(options.ToRequest(), context.CancellationToken).ConfigureAwait(false))).ConfigureAwait(false));
        Help(command, "Each tile has its value, the value of the compared period (`--compare previous|year`), the change as a ratio, and a line over the buckets of `--by`.");
        return command;
    }

    private static Command Series(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var options = new QueryOptions();
        string? metric = null, group = null;
        var command = Leaf("series", "One metric over time: one record with a value for each bucket, optionally cut by a group.");
        command.Add("<metric>", "The metric: " + string.Join(", ", StatisticsQueries.MetricNames) + ".", value => metric = value);
        command.Add("group=", "Cut the series by: " + string.Join(", ", StatisticsQueries.GroupNames) + ". The groups beyond --limit are added up as `other`.", value => group = value);
        options.AddTo(command, frequency: true);
        command.Add(async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(metric))
            {
                return Fail(context, "usage.missingMetric", "A metric is required: " + string.Join(", ", StatisticsQueries.MetricNames) + ".", Usage);
            }

            return await Run(plugin, context, async queries => Write(context, "alta.statistics.series", await queries.SeriesAsync(options.ToRequest(), metric, group, context.CancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        });
        Help(command, "At most 5,000 buckets and, with the limit, 20 groups by default: `--by` chooses a coarser frequency for a long period.");
        return command;
    }

    private static Command Top(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var options = new QueryOptions();
        string? kind = null, by = null;
        var command = Leaf("top", "A ranking of the tools, the models, the projects or the sessions of a period.");
        command.Add("<kind>", "What to rank: " + string.Join(", ", StatisticsQueries.TopKinds) + ".", value => kind = value);
        command.Add("by=", "The measure: " + string.Join(", ", StatisticsQueries.TopMeasures) + " (default tokens). For tools `tokens` is the bytes of their arguments and results.", value => by = value);
        options.AddTo(command, frequency: false);
        command.Add(async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return Fail(context, "usage.missingKind", "What to rank is required: " + string.Join(", ", StatisticsQueries.TopKinds) + ".", Usage);
            }

            return await Run(plugin, context, async queries => Write(context, "alta.statistics.top", await queries.TopAsync(options.ToRequest(), kind, by ?? "tokens", context.CancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        });
        Help(command, "At most 500 rows, 10 by default. Each row has its share of the ranking and a line over time.");
        return command;
    }

    private static Command Details(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var options = new QueryOptions();
        string? list = null;
        var command = Leaf("details", "The names the statistics count, ranked: the programs of shell commands, the commands of alta, the extensions of the files that were changed.");
        command.Add("<list>", "The list: " + string.Join(", ", StatisticsQueries.DetailNames) + ".", value => list = value);
        options.AddTo(command, frequency: false);
        command.Add(async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                return Fail(context, "usage.missingList", "A list is required: " + string.Join(", ", StatisticsQueries.DetailNames) + ".", Usage);
            }

            return await Run(plugin, context, async queries => Write(context, "alta.statistics.details", await queries.DetailsAsync(options.ToRequest(), list, context.CancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        });
        Help(command, "Only a program (`git`, `dotnet`) or the first two words of an alta command (`session create`) are kept, never the rest of a command. Example: `alta statistics details shell-program --period 30d --limit 10`.");
        return command;
    }

    private static Command Session(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        string? session = null;
        var withChildren = false;
        var command = Leaf("session", "The numbers of one session over its whole life: totals, its latest runs, its models and tools.");
        command.Add("<session>", "The identifier of the session, or the start of it.", value => session = value);
        command.Add("with-children", "Add the sessions the session created, at any depth, to its totals.", value => withChildren = value is not null);
        command.Add(async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(session))
            {
                return Fail(context, "usage.missingSession", "A session is required.", Usage);
            }

            return await Run(plugin, context, async queries =>
            {
                var result = await queries.SessionAsync(session, withChildren, context.CancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    return Fail(context, "session.notFound", $"No statistics for a session '{session}': it is not read yet, or the identifier is wrong.");
                }

                Write(context, "alta.statistics.session", result);
                return 0;
            }).ConfigureAwait(false);
        });
        return command;
    }

    private static Command Status(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Leaf("status", "Whether the history is read, how far, and what is left.");
        command.Add(async (_, _) =>
        {
            try
            {
                var status = await ReadStatusAsync(plugin, context).ConfigureAwait(false);
                if (status is null)
                {
                    return Fail(context, "statistics.unavailable", "This application has no database for the statistics.");
                }

                Write(context, "alta.statistics.status", status.Value.Status, ("running", status.Value.Running));
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Fail(context, "statistics.failed", exception.Message);
            }
        });
        Help(command, "`state` is starting, needsChoice (nothing is read until the user chooses), reading, paused, stoppedHere, done or failed. `completeFromDay` is the first day (yyyymmdd) the numbers are complete from; `running` says whether this application reads the sessions (CodeAlta Desktop does).");
        return command;
    }

    private static Command History(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Group("history", "Choose how much history to read, and pause, resume or stop the reading. Only when the user asks for it.");
        command.Add(HistoryRead(plugin, context));
        command.Add(HistoryControl(plugin, context, "pause", "Pause the reading of the history; the numbers of today keep up to date. The pause is kept across restarts.", static (service, token) => service.PauseAsync(token)));
        command.Add(HistoryControl(plugin, context, "resume", "Resume a paused reading.", static (service, token) => service.ResumeAsync(token)));
        command.Add(HistoryControl(plugin, context, "stop", "Stop the reading where it is: the statistics start at the date reached. `history read` goes further back later.", static (service, token) => service.StopHereAsync(token)));
        command.Add(ForgetDeleted(plugin, context));
        Help(command, "The history is read by CodeAlta Desktop, one session at a time, the most recent first. Examples: `alta statistics history read --days 90`; `alta statistics history pause`; `alta statistics history resume`.");
        return command;
    }

    private static Command HistoryRead(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        int? days = null;
        var all = false;
        var fromToday = false;
        var command = Leaf("read", "Choose how much of the history to read, or read more of it: all of it, the last N days, or nothing of the past.");
        command.Add("days=", "Read the sessions with activity in the last N days (30 or 90 are usual).", value => days = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1);
        command.Add("all", "Read every session.", value => all = value is not null);
        command.Add("from-today", "Read nothing of the past: the statistics start with today.", value => fromToday = value is not null);
        command.Add(async (_, _) =>
        {
            var chosen = (days is not null ? 1 : 0) + (all ? 1 : 0) + (fromToday ? 1 : 0);
            if (chosen != 1 || days is < 1)
            {
                return Fail(context, "usage.historyChoice", "Choose one of --days <N> (at least 1), --all or --from-today.", Usage);
            }

            var choice = all ? HistoryChoice.All : fromToday ? HistoryChoice.FromToday : HistoryChoice.LastDays(days!.Value);
            return await WithService(plugin, context, async (service, token) =>
            {
                var status = await service.ChooseHistoryAsync(choice, token).ConfigureAwait(false);
                Write(context, "alta.statistics.history", status, ("running", true), ("action", "read"));
                return 0;
            }).ConfigureAwait(false);
        });
        Help(command, "The first choice starts the reading. A later one that goes further back reads more; one that does not go further back changes nothing.");
        return command;
    }

    private static Command HistoryControl(StatisticsPlugin plugin, PluginAltaCommandContext context, string name, string description, Func<IStatisticsService, CancellationToken, ValueTask<StatisticsStatus>> action)
    {
        var command = Leaf(name, description);
        command.Add(async (_, _) => await WithService(plugin, context, async (service, token) =>
        {
            var status = await action(service, token).ConfigureAwait(false);
            Write(context, "alta.statistics.history", status, ("running", true), ("action", name));
            return 0;
        }).ConfigureAwait(false));
        return command;
    }

    private static Command ForgetDeleted(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Leaf("forget-deleted", "Remove the statistics of the sessions that were deleted. Their numbers are kept otherwise. Only when the user asks for it.");
        command.Add(async (_, _) => await WithService(plugin, context, async (service, token) =>
        {
            var forgotten = await service.ForgetDeletedAsync(token).ConfigureAwait(false);
            WriteJson(context, "alta.statistics.forgotten", "{\"sessions\":" + forgotten.ToString(CultureInfo.InvariantCulture) + "}");
            return 0;
        }).ConfigureAwait(false));
        return command;
    }

    private static async Task<int> WithService(StatisticsPlugin plugin, PluginAltaCommandContext context, Func<IStatisticsService, CancellationToken, Task<int>> run)
    {
        if (plugin.Statistics is not { } service)
        {
            return Fail(context, "statistics.notRunning", "The history is read by CodeAlta Desktop: this application does not read the sessions.");
        }

        try
        {
            return await run(service, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(context, "statistics.failed", exception.Message);
        }
    }

    private static async ValueTask<(StatisticsStatus Status, bool Running)?> ReadStatusAsync(StatisticsPlugin plugin, PluginAltaCommandContext context)
    {
        // Where the engine runs, the store is the one of the engine, prepared: the state is known, never the one of an engine that waits to start.
        var store = await plugin.OpenStoreAsync(context, context.CancellationToken).ConfigureAwait(false);
        if (plugin.Statistics is { } service)
        {
            return (service.Status, true);
        }

        if (store is null)
        {
            return null;
        }

        var meta = await store.GetAllMetaAsync(context.CancellationToken).ConfigureAwait(false);
        var sessions = await store.ReadAsync(sql => (int)sql.ScalarLong($"SELECT COUNT(*) FROM {store.Prefix}journal"), context.CancellationToken).ConfigureAwait(false);
        var choice = meta.GetValueOrDefault("history.choice");
        var state = string.IsNullOrEmpty(choice) ? HistoryState.NeedsChoice
            : meta.GetValueOrDefault("history.stopped") == "1" ? HistoryState.StoppedHere
            : meta.GetValueOrDefault("history.paused") == "1" ? HistoryState.Paused
            : meta.GetValueOrDefault("history.done") == "1" ? HistoryState.Done
            : HistoryState.Reading;
        int? Day(string key) => meta.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day > 0 ? day : null;
        return (new StatisticsStatus
        {
            State = state,
            Choice = string.IsNullOrEmpty(choice) ? null : choice,
            FloorDay = Day("history.floor_day"),
            CompleteFromDay = Day("history.complete_from_day"),
            SessionsDone = sessions,
            SessionsTotal = sessions,
        }, false);
    }

    private static async Task<int> Run(StatisticsPlugin plugin, PluginAltaCommandContext context, Func<StatisticsQueries, Task<int>> run)
    {
        try
        {
            if (await plugin.GetQueriesAsync(context, context.CancellationToken).ConfigureAwait(false) is not { } queries)
            {
                return Fail(context, "statistics.unavailable", "This application has no database for the statistics.");
            }

            return await run(queries).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Fail(context, "usage.invalidQuery", exception.Message, Usage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(context, "statistics.failed", exception.Message);
        }
    }

    private static async Task<int> Run(StatisticsPlugin plugin, PluginAltaCommandContext context, Func<StatisticsQueries, Task> run)
        => await Run(plugin, context, async queries =>
        {
            await run(queries).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);

    /// <summary>Writes a result as one JSONL record: its type, its version and the correlation of the command, then the properties of the result.</summary>
    private static void Write<T>(PluginAltaCommandContext context, string type, T result, params (string Name, object Value)[] extra)
        => WriteJson(context, type, StatisticsJson.Serialize(result), extra);

    private static void WriteJson(PluginAltaCommandContext context, string type, string json, params (string Name, object Value)[] extra)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("version", 1);
            writer.WriteString("correlationId", context.CorrelationId);
            foreach (var (name, value) in extra)
            {
                switch (value)
                {
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;
                    default:
                        writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        context.Stdout.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static int Fail(PluginAltaCommandContext context, string code, string message, int exitCode = Failure)
    {
        var error = new StatisticsCommandError("alta.error", 1, context.CorrelationId, code, exitCode, message);
        context.Stderr.WriteLine(JsonSerializer.Serialize(error, StatisticsCommandJsonContext.Default.StatisticsCommandError));
        return exitCode;
    }

    /// <summary>Reads the name of a value of an enumeration, in any case. A number or a list of names is not a name.</summary>
    private static bool TryParseName<TEnum>(string text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        var name = text.Trim();
        return name.Length > 0 && name.All(char.IsAsciiLetter) && Enum.TryParse(name, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static Command Group(string name, string description) => new(name, description) { new CommandUsage(), new HelpOption() };

    private static Command Leaf(string name, string description) => new(name, description) { new CommandUsage(), new HelpOption() };

    private static void Help(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines)
        {
            command.Add(line);
        }
    }

    /// <summary>The options every question about the numbers has: a period, filters and a comparison.</summary>
    private sealed class QueryOptions
    {
        private string _period = "30d";
        private string? _by;
        private string? _compare;
        private string? _weekStart;
        private int? _limit;
        private string? _space, _project, _provider, _model, _effort, _origin, _toolKind;

        public void AddTo(Command command, bool frequency)
        {
            command.Add("period=", "The period: today, yesterday, 7d, 30d, 90d (any Nd), week, month, last-month, year, all, or <from>..<to> (default 30d).", value => _period = value ?? "30d");
            if (frequency)
            {
                command.Add("by=", "The frequency of the buckets: auto (default), hour, day, week, month or year.", value => _by = value);
                command.Add("compare=", "Compare with the period before (`previous`) or the same dates a year before (`year`).", value => _compare = value);
                command.Add("week-start=", "The first day of the week, such as monday. By default the one of the regional settings of this computer, as on the Statistics page.", value => _weekStart = value);
            }

            command.Add("project=", "Only the sessions of this project: its id, slug or name.", value => _project = value);
            command.Add("space=", "Only the sessions of the projects this space has today: its id or name.", value => _space = value);
            command.Add("provider=", "Only this provider.", value => _provider = value);
            command.Add("model=", "Only this model.", value => _model = value);
            command.Add("effort=", "Only this reasoning effort.", value => _effort = value);
            command.Add("origin=", "Only the prompts of this origin: you, agent, automation or reminder.", value => _origin = value);
            command.Add("tool-kind=", "Only the tools of this kind: files, search, shell, web, alta, mcp, skill or other.", value => _toolKind = value);
            command.Add("limit=", "The most rows or lines to return.", value => _limit = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ? limit : -1);
        }

        public StatisticsRequest ToRequest()
        {
            var frequency = StatisticsFrequency.Auto;
            if (_by is not null && !TryParseName(_by, out frequency))
            {
                throw new ArgumentException($"'{_by}' is not a frequency: use auto, hour, day, week, month or year.");
            }

            var comparison = _compare?.ToLowerInvariant() switch
            {
                null or "" or "none" => StatisticsComparison.None,
                "previous" => StatisticsComparison.PreviousPeriod,
                "year" => StatisticsComparison.SamePeriodLastYear,
                _ => throw new ArgumentException($"'{_compare}' is not a comparison: use previous or year."),
            };
            DayOfWeek? weekStart = null;
            if (_weekStart is not null)
            {
                weekStart = TryParseName<DayOfWeek>(_weekStart, out var day) ? day : throw new ArgumentException($"'{_weekStart}' is not a day of the week: use its name, such as monday.");
            }

            if (_limit is < 1)
            {
                throw new ArgumentException("--limit is at least 1.");
            }

            return new StatisticsRequest
            {
                Period = _period,
                Frequency = frequency,
                Comparison = comparison,
                WeekStart = weekStart,
                Limit = _limit,
                Filter = new StatisticsFilter
                {
                    Space = Clean(_space),
                    Project = Clean(_project),
                    Provider = Clean(_provider),
                    Model = Clean(_model),
                    Effort = Clean(_effort),
                    Origin = Clean(_origin),
                    ToolKind = Clean(_toolKind),
                },
            };
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
