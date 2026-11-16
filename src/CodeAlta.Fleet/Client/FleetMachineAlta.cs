using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// The <c>alta</c> commands of a person on another machine, for what shows that person the sessions of several machines the
/// same way (the page of the phones). A read is answered by the machine with its own records (<c>sessions.read</c>); a
/// prompt, a stop and a new session are the calls of the node API, so the machine admits them as the work of this device; a
/// few small actions that start no work go by <c>sessions.act</c>. Any other command is refused: nothing here runs a command
/// line on another machine.
/// </summary>
/// <remarks>
/// A command that names files of this computer (<c>--attachments</c>) is refused: the node API carries none. A failure of the
/// channel is an error of the command, never an exception: the caller shows it as it shows an error of <c>alta</c>.
/// </remarks>
public sealed class FleetMachineAlta : IPluginAltaService
{
    /// <summary>The code of the error of a command the node API has no call for.</summary>
    public const string Unsupported = "machine.unsupported";

    /// <summary>The code of the error of a machine that is not reached.</summary>
    public const string Unreachable = "machine.unreachable";

    private readonly Func<CancellationToken, ValueTask<FleetNodeClient?>> _connect;
    private readonly string _name;

    /// <summary>Creates the commands of a machine.</summary>
    /// <param name="name">Its name, for the messages.</param>
    /// <param name="connect">Gives the open channel to it, or null while it is not reached.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public FleetMachineAlta(string name, Func<CancellationToken, ValueTask<FleetNodeClient?>> connect)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(connect);
        _name = name;
        _connect = connect;
    }

    /// <inheritdoc />
    public async ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (await _connect(cancellationToken).ConfigureAwait(false) is not { } client) return Failed(Unreachable, $"{_name} is not reached now.");
            if (FleetSessionReads.TryParse(args, out var request))
                return await ReadAsync(client, request! with { MaxBytes = options?.MaxOutputBytes }, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> rest = [.. args.Skip(3)];
            return args.Count switch
            {
                >= 3 when args[0] == "session" && args[1] is "send" or "queue" or "steer" => await PromptAsync(client, args[1], args[2], rest, stdin, cancellationToken).ConfigureAwait(false),
                >= 3 when args[0] == "session" && args[1] == "abort" => await AbortAsync(client, args[2], cancellationToken).ConfigureAwait(false),
                >= 2 when args[0] == "session" && args[1] == "create" => await CreateAsync(client, [.. args.Skip(2)], cancellationToken).ConfigureAwait(false),
                _ => Failed(Unsupported, $"This is not done on {_name} from here yet: do it on that machine."),
            };
        }
        catch (FleetException exception)
        {
            return Failed(exception.Error.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Failed(Unreachable, $"{_name} did not answer: {exception.Message}");
        }
    }

    private static async Task<PluginAltaCommandResult> ReadAsync(FleetNodeClient client, FleetSessionReadParams request, CancellationToken cancellationToken)
    {
        var result = await client.ReadSessionAsync(request, cancellationToken).ConfigureAwait(false);
        // An image comes by pieces: they are put together again before the caller reads its record.
        if (request.Read == "session.image" && result.ExitCode == 0 && result.Records is [var first] && Piece(first) is { } piece && piece.Total > piece.Text.Length)
        {
            if (piece.Total > 16 * 1024 * 1024 || Offset(first) != 0) return Failed("machine.read", "The image is too large or its first piece is missing.");
            var whole = new StringBuilder(piece.Total).Append(piece.Text);
            while (whole.Length < piece.Total)
            {
                var options = new Dictionary<string, string>(request.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["offset"] = whole.Length.ToString(CultureInfo.InvariantCulture) };
                var next = await client.ReadSessionAsync(request with { Options = options }, cancellationToken).ConfigureAwait(false);
                if (next.ExitCode != 0 || next.Records is not [var more] || Piece(more) is not { Text.Length: > 0 } following
                    || following.Total != piece.Total || Offset(more) != whole.Length || following.Text.Length > piece.Total - whole.Length)
                    return Failed("machine.read", next.Error ?? "The image was not read whole.");
                whole.Append(following.Text);
            }

            return Records(0, [With(first, "base64", whole.ToString())], false, null);
        }

        return Records(result.ExitCode, result.Records, result.Truncated, result.Error);
    }

    private static (string Text, int Total)? Piece(JsonElement record)
        => record.TryGetProperty("base64", out var text) && text.ValueKind == JsonValueKind.String
            && record.TryGetProperty("base64Length", out var total) && total.TryGetInt32(out var length) ? (text.GetString()!, length) : null;

    private static int Offset(JsonElement record) => record.TryGetProperty("base64Offset", out var value) && value.TryGetInt32(out var offset) ? offset : -1;

    // A prompt: the selection of the phone goes with it (model, effort, agent prompt), as with a prompt of this computer.
    private async Task<PluginAltaCommandResult> PromptAsync(FleetNodeClient client, string mode, string sessionId, IReadOnlyList<string> rest, string? stdin,
        CancellationToken cancellationToken)
    {
        if (!TryOptions(rest, PromptOptions, out var options)) return Failed(Unsupported, "The prompt has an option that does not go to another machine.");
        if (options.ContainsKey("attachments")) return Failed(Unsupported, $"Files and photos do not go to a session of {_name} yet: send the text alone.");
        if (options.ContainsKey("permission-mode")) return Failed(Unsupported, $"The permissions of a session of {_name} are changed on that machine.");
        var text = stdin ?? "";
        if (string.IsNullOrWhiteSpace(text)) return Failed("usage.missingPrompt", "A prompt is needed.");
        var retryKey = Guid.NewGuid().ToString("N");
        if (mode == "steer")
        {
            var state = await client.GetSessionStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (state.ActiveRunId is not { } runId || state.AttachmentGeneration is not { } generation) return Failed("session.idle", "No turn of the session runs: send the prompt instead.");
            return Command(await client.SteerAsync(new FleetSteerParams(sessionId, text, state.RuntimeInstanceId, generation, runId), retryKey, CancellationToken.None).ConfigureAwait(false),
                sessionId, "alta.session.steered");
        }

        if (mode == "queue") return Queued(await client.QueueAsync(sessionId, text, retryKey, CancellationToken.None).ConfigureAwait(false), sessionId);
        var selection = options.ContainsKey("model") || options.ContainsKey("reasoning") || options.ContainsKey("prompt-id")
            ? new FleetModelSelection(null, options.GetValueOrDefault("model"), options.GetValueOrDefault("prompt-id"), options.GetValueOrDefault("reasoning")) : null;
        var sent = await client.SendAsync(new FleetSendParams(sessionId, text, selection), retryKey, CancellationToken.None).ConfigureAwait(false);
        // A send to a session that works is queued when the caller said so, as alta does here: never steered into a turn it did not see.
        if (sent.Status == "busy" && options.ContainsKey("queue-if-busy"))
            return Queued(await client.QueueAsync(sessionId, text, Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false), sessionId);
        return Command(sent, sessionId, "alta.session.submitted");
    }

    private static readonly FrozenDictionary<string, bool> PromptOptions = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["stdin"] = false, ["no-wait"] = false, ["queue-if-busy"] = false, ["model"] = true, ["reasoning"] = true, ["prompt-id"] = true, ["permission-mode"] = true, ["attachments"] = true,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private async Task<PluginAltaCommandResult> AbortAsync(FleetNodeClient client, string sessionId, CancellationToken cancellationToken)
    {
        var state = await client.GetSessionStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // Nothing runs: the stop the person asked for is done.
        if (state.ActiveRunId is not { } runId || state.AttachmentGeneration is not { } generation) return Records(0, [Record("alta.session.aborted", sessionId)], false, null);
        return Command(await client.AbortAsync(new FleetAbortParams(sessionId, null, state.RuntimeInstanceId, generation, runId), Guid.NewGuid().ToString("N"), CancellationToken.None)
            .ConfigureAwait(false), sessionId, "alta.session.aborted");
    }

    private static readonly FrozenDictionary<string, bool> CreateOptions = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["global"] = false, ["worktree"] = false, ["project"] = true, ["space"] = true, ["tier"] = true, ["provider"] = true, ["model"] = true, ["reasoning"] = true,
        ["prompt-id"] = true, ["permission-mode"] = true, ["title"] = true,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // A new session: the machine gives it its project, its provider and a worktree of its own, as to any session started from
    // another computer. A tier is one of the machine: it says which model that is first.
    private async Task<PluginAltaCommandResult> CreateAsync(FleetNodeClient client, IReadOnlyList<string> rest, CancellationToken cancellationToken)
    {
        if (!TryOptions(rest, CreateOptions, out var options)) return Failed(Unsupported, "The new session has an option that does not go to another machine.");
        var (provider, model) = (options.GetValueOrDefault("provider"), options.GetValueOrDefault("model"));
        if (options.GetValueOrDefault("tier") is { } tier)
        {
            var resolved = await client.ReadSessionAsync(new FleetSessionReadParams("model.resolve") { Options = new Dictionary<string, string> { ["tier"] = tier } }, cancellationToken)
                .ConfigureAwait(false);
            var record = resolved.Records.FirstOrDefault(static item => Text(item, "providerKey") is not null || Text(item, "providerId") is not null);
            if (resolved.ExitCode != 0 || record.ValueKind != JsonValueKind.Object) return Failed("model.unresolved", resolved.Error ?? $"{_name} has no model for the tier '{tier}'.");
            (provider, model) = (Text(record, "providerKey") ?? Text(record, "providerId"), Text(record, "modelId"));
            options.TryAdd("reasoning", Text(record, "reasoningEffort") ?? "");
        }

        var effort = options.GetValueOrDefault("reasoning") is { Length: > 0 } reasoning ? reasoning : null;
        var selection = provider is null && model is null && effort is null && !options.ContainsKey("prompt-id") ? null
            : new FleetModelSelection(provider, model, options.GetValueOrDefault("prompt-id"), effort);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(options.ContainsKey("global") ? null : options.GetValueOrDefault("project"),
            options.GetValueOrDefault("space"), options.GetValueOrDefault("title"), null, selection) { PermissionMode = options.GetValueOrDefault("permission-mode") },
            Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);
        return Records(0, [Record("alta.session.created", created.SessionId)], false, null);
    }

    // The options of a command, each known to take a value or not; a word that is none of them is refused.
    private static bool TryOptions(IReadOnlyList<string> words, IReadOnlyDictionary<string, bool> known, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (!word.StartsWith("--", StringComparison.Ordinal) || !known.TryGetValue(word[2..], out var takesValue)) return false;
            if (!takesValue) options[word[2..]] = "";
            else if (++index >= words.Count) return false;
            else options[word[2..]] = words[index];
        }

        return true;
    }

    private static PluginAltaCommandResult Command(FleetCommandResult result, string sessionId, string type)
        => result.Status is "accepted" or "replay" or FleetSeatStatuses.Queued
            ? Records(0, [Record(type, sessionId)], false, null)
            : Failed("session." + result.Status, result.Status switch
            {
                "busy" => "The session works: queue the prompt, or steer the turn.",
                "unknown_target" or "expired" => "The turn ended meanwhile: send the prompt again.",
                "closed" => "The machine is stopping.",
                "capacity" => "The machine takes no more prompts for now.",
                _ => $"The machine did not take it ({result.Status}{(result.Code is { } code ? ": " + code : "")}).",
            });

    private static PluginAltaCommandResult Queued(FleetQueueResult result, string sessionId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "alta.session.queued");
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("queueItemId", result.QueueItemId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return Records(0, [document.RootElement.Clone()], false, null);
    }

    private static JsonElement Record(string type, string sessionId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("sessionId", sessionId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement With(JsonElement record, string name, string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in record.EnumerateObject())
            {
                if (property.NameEquals(name) || property.NameEquals("base64Offset") || property.NameEquals("base64Length")) continue;
                property.WriteTo(writer);
            }

            writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement record, string name)
        => record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static PluginAltaCommandResult Records(int exitCode, IReadOnlyList<JsonElement> records, bool truncated, string? error)
    {
        var text = new StringBuilder();
        foreach (var record in records) text.Append(record.GetRawText()).Append('\n');
        if (exitCode != 0 && error is not null) text.Append(ErrorRecord("machine.command", error)).Append('\n');
        return new PluginAltaCommandResult { ExitCode = exitCode, TranscriptJsonl = text.ToString(), Truncated = truncated, Error = exitCode == 0 ? null : error ?? "The command failed." };
    }

    private static PluginAltaCommandResult Failed(string code, string message)
        => new() { ExitCode = 1, TranscriptJsonl = ErrorRecord(code, message) + "\n", Error = message };

    private static string ErrorRecord(string code, string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "alta.error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
