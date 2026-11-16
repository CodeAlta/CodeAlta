using System.Text.Json;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// sessions.read and sessions.act: a device reads a session the way the person reads it on this machine, by the alta command
// of the list of FleetSessionReads, and does the few small things that start no work (take a queued prompt back, change the
// provider, archive, stop a job). The machine builds each command from checked values; who may see the session is decided
// here as for sessions.history, and a list of sessions is cut to those the caller may see.
public sealed partial class FleetNodeService
{
    private void RegisterReads(FleetNodeCaller caller)
    {
        On(caller, FleetNodeMethods.SessionsRead, Access.View, FleetNodeJsonContext.Default.FleetSessionReadParams, FleetNodeJsonContext.Default.FleetSessionReadResult,
            (call, parameters, token) => ReadSessionAsync(call, parameters, acts: false, token));
        On(caller, FleetNodeMethods.SessionsAct, Access.Drive, FleetNodeJsonContext.Default.FleetSessionReadParams, FleetNodeJsonContext.Default.FleetSessionReadResult,
            (call, parameters, token) => ReadSessionAsync(call, parameters, acts: true, token));
    }

    private async Task<FleetSessionReadResult> ReadSessionAsync(FleetNodeCall call, FleetSessionReadParams parameters, bool acts, CancellationToken cancellationToken)
    {
        var alta = _options.Alta ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "This machine answers no read of its sessions.");
        var args = FleetSessionReads.Build(parameters, out var spec);
        if (spec.Acts != acts)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, spec.Acts ? $"'{spec.Name}' changes the session: it goes by sessions.act." : $"'{spec.Name}' is a read: it goes by sessions.read.");
        var ownOnly = call.Device is { OwnSessionsOnly: true };
        if (acts)
        {
            RequireWork();
            RequireNotHalted();
        }

        if (spec.Target == FleetSessionReads.Target.Machine)
        {
            // What the machine has (its projects, its providers, its models) is for the devices of its owner: a person a team
            // machine lets in reads what node.info gives them.
            if (ownOnly && !spec.ListsSessions) throw new FleetException(FleetErrorCodes.Forbidden, null, "This is read by the devices of the owner of the machine.");
        }
        else
        {
            // A Private session is not shown to exist; one of another person was refused before the handler (RequireOwnSession).
            _ = await RequireViewedSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
            if (_host.RuntimeService.Relinquishments.ListRelinquished().Contains(parameters.SessionId!)) throw SessionNotFound(parameters.SessionId!);
        }

        // Archiving the sessions below one reaches sessions the caller may not see on a team machine.
        if (ownOnly && parameters.Options?.ContainsKey("with-children") == true)
            throw new FleetException(FleetErrorCodes.Forbidden, null, "On a team machine a session is archived alone.");
        if (parameters.Options?.ContainsKey("with-children") == true)
        {
            var children = await alta(["session", "children", parameters.SessionId!, "--recursive"], FleetSessionReads.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (children.ExitCode != 0 || children.Truncated) throw new FleetException(FleetErrorCodes.Forbidden, null, "The descendants could not all be checked.");
            foreach (var child in Records(children.Jsonl))
                if (Text(child, "sessionId") is { } id && !await MaySeeAsync(call, id, cancellationToken).ConfigureAwait(false))
                    throw new FleetException(FleetErrorCodes.Forbidden, null, "A descendant is not available to this device.");
        }

        if (spec.Name == "session.set_provider")
        {
            var session = await RequireSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
            var project = session.ProjectRef is { } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
            var provider = ResolveProvider(parameters.Options?.GetValueOrDefault("provider"), project?.ProjectPath, call.Device);
            _ = RequirePolicyStart(session.SpaceId, provider, null, agent: false);
        }
        if (spec.Target == FleetSessionReads.Target.Job) await RequireJobOfSessionAsync(alta, parameters.Arguments![0], parameters.SessionId!, cancellationToken).ConfigureAwait(false);

        var image = spec.Name == "session.image";
        var maxBytes = image ? 16 * 1024 * 1024 : Math.Clamp(parameters.MaxBytes ?? FleetSessionReads.DefaultMaxBytes, 16 * 1024, FleetSessionReads.MaxBytes);
        FleetNodeAltaResult result;
        try
        {
            result = await alta(args, maxBytes, acts ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new FleetException(FleetNodeErrorCodes.NodeStopping, FleetErrorHints.Retry, "The node is stopping.");
        }

        var records = new List<JsonElement>();
        string? error = null;
        foreach (var record in Records(result.Jsonl))
        {
            var type = record.TryGetProperty("type", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            if (type == "alta.result") continue;
            if (type == "alta.error")
            {
                error ??= Text(record, "message") ?? Text(record, "code");
                continue;
            }

            if (spec.ListsSessions && Text(record, "sessionId") is { } listed && !await MaySeeAsync(call, listed, cancellationToken).ConfigureAwait(false)) continue;
            records.Add(image ? ImagePiece(record, parameters.Options) : record);
        }

        if (acts) LogInfo($"{Capitalized(Who(call))} ran {spec.Name} on the session {parameters.SessionId}.");
        return new FleetSessionReadResult(result.ExitCode, records, result.Truncated, result.ExitCode == 0 ? null : error ?? result.Error ?? "The command failed.");
    }

    // A job is read and stopped only through its session: another session's job is not shown to exist.
    private static async Task RequireJobOfSessionAsync(Func<IReadOnlyList<string>, int, CancellationToken, Task<FleetNodeAltaResult>> alta, string jobId, string sessionId,
        CancellationToken cancellationToken)
    {
        var status = await alta(["job", "status", jobId], 64 * 1024, cancellationToken).ConfigureAwait(false);
        foreach (var record in Records(status.Jsonl))
        {
            if (Text(record, "sessionId") is { } owner && string.Equals(owner, sessionId, StringComparison.OrdinalIgnoreCase)) return;
        }

        throw new FleetException(FleetNodeErrorCodes.SessionNotFound, null, "No job of this session has this id.");
    }

    // Whether the caller may see a session a list names: not a Private one, not one the machine gave away, and on a team
    // machine only one its person started.
    private async Task<bool> MaySeeAsync(FleetNodeCall call, string sessionId, CancellationToken cancellationToken)
    {
        if (!MayStillRead(call.Caller, sessionId)) return false;
        if (_host.RuntimeService.Relinquishments.ListRelinquished().Contains(sessionId)) return false;
        return !await IsPrivateAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    // One piece of the base64 of an image, from the offset the request names, with where it is in the whole.
    private static JsonElement ImagePiece(JsonElement record, IReadOnlyDictionary<string, string>? options)
    {
        if (Text(record, "base64") is not { } whole) return record;
        var offset = options is not null && options.TryGetValue("offset", out var text) && int.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var asked) ? Math.Min(asked, whole.Length) : 0;
        var length = Math.Min(FleetSessionReads.ImageChunk, whole.Length - offset);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in record.EnumerateObject())
            {
                if (property.NameEquals("base64")) continue;
                property.WriteTo(writer);
            }

            writer.WriteString("base64", whole.AsSpan(offset, length));
            writer.WriteNumber("base64Offset", offset);
            writer.WriteNumber("base64Length", whole.Length);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Records(string jsonl)
    {
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement record;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                record = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            yield return record;
        }
    }

    private static string? Text(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
