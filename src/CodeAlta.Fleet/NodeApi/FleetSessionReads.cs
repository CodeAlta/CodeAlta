using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>The parameters of <c>sessions.read</c> and <c>sessions.act</c>: one read, or one small action, of the list of <see cref="FleetSessionReads"/>.</summary>
/// <param name="Read">Its name, such as <c>session.events</c>.</param>
/// <param name="SessionId">The session it is about; null for a read of the machine (its projects, its providers, its models).</param>
public sealed record FleetSessionReadParams(string Read, string? SessionId = null)
{
    /// <summary>Gets what it names beside the session, in order: the id of a tool call, of an image, of a job, of a queued prompt.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>Gets its options by name, each of the kind its read declares; a flag has an empty value.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Gets the most bytes of records the caller takes; null for the default. The node keeps it within what one answer holds.</summary>
    public int? MaxBytes { get; init; }
}

/// <summary>
/// The result of <c>sessions.read</c> and <c>sessions.act</c>: the records the <c>alta</c> command of the machine wrote, as that
/// machine writes them. A reader takes them as it takes the journal events of <c>sessions.history</c>: fields it does not
/// know are ignored, fields that are missing are optional.
/// </summary>
/// <param name="ExitCode">The exit code of the command: 0 when it succeeded.</param>
/// <param name="Records">Its records, the result header left out.</param>
/// <param name="Truncated">Whether the output was cut: records are missing at its end.</param>
/// <param name="Error">What failed, for people; null when it succeeded.</param>
public sealed record FleetSessionReadResult(int ExitCode, IReadOnlyList<JsonElement> Records, bool Truncated, string? Error = null);

/// <summary>What ran an <c>alta</c> command on a machine, for a person.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Jsonl">Its records, one JSON object a line.</param>
/// <param name="Truncated">Whether its output was cut.</param>
/// <param name="Error">What failed; null when it succeeded.</param>
public sealed record FleetNodeAltaResult(int ExitCode, string Jsonl, bool Truncated, string? Error);

/// <summary>
/// The reads and the small actions a device runs on the machine that owns a session, each the <c>alta</c> command the person
/// would run there. A caller names one and gives its values; the machine checks each value against its kind and builds the
/// command itself, so nothing a caller sends is ever a command line. The same list reads the command line of a client back
/// into a request (<see cref="TryParse"/>), so the two sides cannot disagree on what a read takes.
/// </summary>
public static class FleetSessionReads
{
    /// <summary>The feature a node announces in <c>node.info</c> when it takes <c>sessions.read</c> and <c>sessions.act</c>.</summary>
    public const string Feature = "sessions.read";

    /// <summary>The bytes of records of an answer when the caller names none.</summary>
    public const int DefaultMaxBytes = 512 * 1024;

    /// <summary>The most bytes of records of one answer: under the size of a frame, with room for its envelope.</summary>
    public const int MaxBytes = 3 * 1024 * 1024;

    /// <summary>The characters of the base64 of an image one answer of <c>session.image</c> holds; <c>offset</c> asks for the next ones.</summary>
    public const int ImageChunk = 1024 * 1024;

    /// <summary>What a read or an action is about.</summary>
    internal enum Target
    {
        /// <summary>The machine: its projects, its providers, its models, its sessions.</summary>
        Machine,

        /// <summary>A session, the first value after the command.</summary>
        Session,

        /// <summary>A session, named by <c>--session</c>.</summary>
        SessionOption,

        /// <summary>A job, the first value after the command, checked to be of the session of the request.</summary>
        Job,
    }

    /// <summary>The kind of the value of an option.</summary>
    internal enum Kind
    {
        /// <summary>No value.</summary>
        Flag,

        /// <summary>A number from 0.</summary>
        Number,

        /// <summary>An id: letters, digits and <c>._:@/-</c>.</summary>
        Id,

        /// <summary>Ids separated by commas.</summary>
        Ids,
    }

    /// <summary>One read or action.</summary>
    /// <param name="Name">Its name on the wire.</param>
    /// <param name="Command">The words of the <c>alta</c> command.</param>
    /// <param name="Target">What it is about.</param>
    /// <param name="Values">How many values follow the command, the session left out.</param>
    /// <param name="Acts">Whether it changes something: it needs <c>drive</c>, and goes by <c>sessions.act</c>.</param>
    /// <param name="ListsSessions">Whether its records are sessions, each shown only to who may see it.</param>
    internal sealed record Spec(string Name, string[] Command, Target Target, int Values = 0, bool Acts = false, bool ListsSessions = false)
    {
        public FrozenDictionary<string, Kind> Options { get; init; } = FrozenDictionary<string, Kind>.Empty;
    }

    private static readonly FrozenDictionary<string, Kind> EventOptions = Options(("since", Kind.Number), ("limit", Kind.Number), ("last", Kind.Number), ("before", Kind.Number),
        ("include", Kind.Ids), ("fields", Kind.Ids), ("kind", Kind.Ids), ("no-tool-output", Kind.Flag), ("tool-images", Kind.Flag));

    private static readonly ImmutableArray<Spec> All =
    [
        new("session.list", ["session", "list"], Target.Machine, ListsSessions: true) { Options = Options(("limit", Kind.Number), ("project", Kind.Id), ("space", Kind.Id)) },
        new("session.status", ["session", "status"], Target.Session),
        new("session.events", ["session", "events"], Target.Session) { Options = EventOptions },
        new("session.tail", ["session", "tail"], Target.Session) { Options = EventOptions },
        new("session.tool", ["session", "tool"], Target.Session, Values: 1)
            { Options = Options(("max-text", Kind.Number), ("max-output", Kind.Number), ("output-offset", Kind.Number), ("since", Kind.Number)) },
        // The base64 of an image comes by pieces of ImageChunk characters: "offset" names the first one of a piece.
        new("session.image", ["session", "image"], Target.Session, Values: 1) { Options = Options(("offset", Kind.Number)) },
        new("session.queued", ["session", "queued"], Target.Session),
        new("session.choices", ["session", "choices"], Target.Session),
        new("session.children", ["session", "children"], Target.Session, ListsSessions: true) { Options = Options(("recursive", Kind.Flag)) },
        new("job.list", ["job", "list"], Target.SessionOption),
        new("job.status", ["job", "status"], Target.Job),
        new("job.output", ["job", "output"], Target.Job) { Options = Options(("lines", Kind.Number)) },
        new("automation.current", ["automation", "current"], Target.SessionOption),
        new("project.list", ["project", "list"], Target.Machine) { Options = Options(("detailed", Kind.Flag), ("all", Kind.Flag)) },
        new("space.list", ["space", "list"], Target.Machine),
        new("model.list", ["model", "list"], Target.Machine) { Options = Options(("detailed", Kind.Flag), ("provider", Kind.Id)) },
        new("model.resolve", ["model", "resolve"], Target.Machine) { Options = Options(("tier", Kind.Id)) },
        new("prompt.list", ["prompt", "list"], Target.Machine) { Options = Options(("project", Kind.Id)) },
        new("provider.list", ["provider", "list"], Target.Machine) { Options = Options(("detailed", Kind.Flag)) },
        // What changes a session without starting work: no admission of the machine is at stake.
        new("session.set_provider", ["session", "set_provider"], Target.Session, Acts: true) { Options = Options(("provider", Kind.Id)) },
        new("session.unqueue", ["session", "unqueue"], Target.Session, Values: 1, Acts: true),
        new("session.unsteer", ["session", "unsteer"], Target.Session, Values: 1, Acts: true),
        new("session.archive", ["session", "archive"], Target.Session, Acts: true) { Options = Options(("with-children", Kind.Flag)) },
        new("session.restore", ["session", "restore"], Target.Session, Acts: true),
        new("job.cancel", ["job", "cancel"], Target.Job, Acts: true),
    ];

    private static readonly FrozenDictionary<string, Spec> ByName = All.ToFrozenDictionary(static spec => spec.Name, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, Spec> ByCommand = All.ToFrozenDictionary(static spec => string.Join(' ', spec.Command), StringComparer.Ordinal);

    private static FrozenDictionary<string, Kind> Options(params (string Name, Kind Kind)[] options)
        => options.ToFrozenDictionary(static option => option.Name, static option => option.Kind, StringComparer.Ordinal);

    /// <summary>The names of the reads and of the actions.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(static spec => spec.Name).ToImmutableArray();

    /// <summary>The read or action of a name; null for none.</summary>
    internal static Spec? Find(string? name) => name is not null && ByName.TryGetValue(name, out var spec) ? spec : null;

    /// <summary>Whether a request changes something, and so goes by <c>sessions.act</c>.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True for an action; false for a read, or for a name the list does not have.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static bool IsAction(FleetSessionReadParams request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Find(request.Read)?.Acts == true;
    }

    /// <summary>
    /// Reads the command line of an <c>alta</c> command back into a request, for a client that holds a command the person ran
    /// here and sends it to the machine of its session.
    /// </summary>
    /// <param name="args">The words of the command, without <c>alta</c>.</param>
    /// <param name="request">The request, when the command is of the list and reads as it declares.</param>
    /// <returns>Whether the command is a read or an action of the list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static bool TryParse(IReadOnlyList<string> args, out FleetSessionReadParams? request)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;
        if (args.Count < 2 || !ByCommand.TryGetValue(args[0] + " " + args[1], out var spec)) return false;
        var values = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index++)
        {
            var word = args[index];
            if (!word.StartsWith("--", StringComparison.Ordinal))
            {
                values.Add(word);
                continue;
            }

            var name = word[2..];
            if (name == "session" && spec.Target == Target.SessionOption)
            {
                if (++index >= args.Count) return false;
                options["session"] = args[index];
            }
            else if (!spec.Options.TryGetValue(name, out var kind)) return false;
            else if (kind == Kind.Flag) options[name] = "";
            else if (++index >= args.Count) return false;
            else options[name] = args[index];
        }

        string? sessionId = null;
        if (spec.Target == Target.Session)
        {
            if (values.Count == 0) return false;
            sessionId = values[0];
            values.RemoveAt(0);
        }
        else if (spec.Target == Target.SessionOption)
        {
            if (!options.Remove("session", out sessionId)) return false;
        }

        var expected = spec.Values + (spec.Target == Target.Job ? 1 : 0);
        if (values.Count != expected) return false;
        request = new FleetSessionReadParams(spec.Name, sessionId) { Arguments = values.Count == 0 ? null : values, Options = options.Count == 0 ? null : options };
        return true;
    }

    /// <summary>Builds the <c>alta</c> command of a request, each value checked against its kind.</summary>
    /// <param name="request">The request.</param>
    /// <param name="spec">The read or action it names.</param>
    /// <returns>The words of the command, without <c>alta</c>.</returns>
    /// <exception cref="FleetException">The request names no read of the list, or a value is not of its kind.</exception>
    internal static List<string> Build(FleetSessionReadParams request, out Spec spec)
    {
        spec = Find(request.Read) ?? throw Invalid($"'{Short(request.Read)}' is not a read of this machine.");
        var values = request.Arguments ?? [];
        var expected = spec.Values + (spec.Target == Target.Job ? 1 : 0);
        if (values.Count != expected) throw Invalid($"'{spec.Name}' takes {expected} value{(expected == 1 ? "" : "s")}.");
        var args = new List<string>(spec.Command);
        if (spec.Target == Target.Machine)
        {
            if (request.SessionId is not null) throw Invalid($"'{spec.Name}' is about the machine, not a session.");
        }
        else if (!IsId(request.SessionId)) throw Invalid($"'{spec.Name}' names a session.");
        else if (spec.Target == Target.Session) args.Add(request.SessionId!);

        foreach (var value in values)
        {
            if (!IsId(value)) throw Invalid($"A value of '{spec.Name}' is not an id.");
            args.Add(value);
        }

        if (spec.Target == Target.SessionOption) args.AddRange(["--session", request.SessionId!]);
        foreach (var (name, value) in (request.Options ?? FrozenDictionary<string, string>.Empty).OrderBy(static option => option.Key, StringComparer.Ordinal))
        {
            if (!spec.Options.TryGetValue(name, out var kind)) throw Invalid($"'{spec.Name}' has no option '{Short(name)}'.");
            // The pieces of an image are cut by the machine, not by the command.
            if (spec.Name == "session.image" && name == "offset")
            {
                if (!IsNumber(value)) throw Invalid("The offset of an image is a number.");
                continue;
            }

            args.Add("--" + name);
            switch (kind)
            {
                case Kind.Flag:
                    if (!string.IsNullOrEmpty(value)) throw Invalid($"The option '{name}' takes no value.");
                    break;
                case Kind.Number:
                    if (!IsNumber(value)) throw Invalid($"The option '{name}' takes a number.");
                    args.Add(value);
                    break;
                case Kind.Id:
                    if (!IsId(value)) throw Invalid($"The option '{name}' takes an id.");
                    args.Add(value);
                    break;
                default:
                    if (value is null || value.Length is 0 or > 512 || value.Split(',').Any(static part => !IsId(part))) throw Invalid($"The option '{name}' takes ids separated by commas.");
                    args.Add(value);
                    break;
            }
        }

        return args;
    }

    /// <summary>Whether a text is an id: 1 to 128 letters, digits and <c>._:@/-</c>, never the start of an option.</summary>
    internal static bool IsId(string? value)
        => value is { Length: > 0 and <= 128 } && value[0] != '-' && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '@' or '/' or '-')
            && !value.Contains("..", StringComparison.Ordinal);

    private static bool IsNumber(string? value)
        => value is { Length: > 0 and <= 18 } && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string Short(string? text) => text is null ? "" : text.Length <= 40 ? text : text[..40];

    private static FleetException Invalid(string message) => new(FleetErrorCodes.InvalidParams, null, message);
}
