using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>
/// The hooks of the server (<c>doc/fleet/orchestration.md</c>, "Triggers"; <c>doc/fleet/protocol.md</c>, the REST API): a person
/// makes a hook for one of their machines and gives its address to a tool that only pushes; the ingress keeps each event
/// as it came, once, and hands it to the machine (<see cref="FleetHookMethods.Event"/>), now or when it connects, for a
/// day. The server holds no secret of the sender and reads nothing of the event: the machine checks the signature, and
/// the event only selects an automation the machine holds as its person signed it. Once the machine answered, only the
/// facts of the delivery stay.
/// </summary>
internal sealed class HookService
{
    /// <summary>The largest body the ingress takes.</summary>
    internal const int MaxBodyBytes = 256 * 1024;

    /// <summary>How many events one hook takes in a minute.</summary>
    internal const int MaxEventsPerMinute = 60;

    /// <summary>The most hooks a person has.</summary>
    internal const int MaxHooksPerPerson = 50;

    /// <summary>How long an event waits for its machine.</summary>
    internal static readonly TimeSpan Waits = TimeSpan.FromHours(24);

    /// <summary>How long the facts of an event are kept.</summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    private const int MaxHeaders = 40;
    private const int MaxHeaderLength = 2048;
    private const int MaxLabelLength = 120;
    private const int PendingPerConnection = 100;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExpireEvery = TimeSpan.FromMinutes(1);

    // The headers of the connection, which say nothing of the event and may say something of who relayed it.
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "cookie", "host", "connection", "content-length", "transfer-encoding", "upgrade", "keep-alive", "te", "trailer", "forwarded", "via",
    };

    private readonly ICoordinatorStore _store;
    private readonly IHookStore _hooks;
    private readonly PresenceRegistry _presence;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Minute, int Count)> _rates = new(StringComparer.Ordinal);
    private DateTimeOffset _expiredAt;

    /// <summary>Initializes a new instance of the <see cref="HookService"/> class.</summary>
    /// <param name="store">The store of the devices and the audit.</param>
    /// <param name="hooks">The store of the hooks.</param>
    /// <param name="presence">The devices online, to hand an event to its machine at once.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    public HookService(ICoordinatorStore store, IHookStore hooks, PresenceRegistry presence, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        (_store, _hooks, _presence, _time, _logger) = (store, hooks, presence, time, logger);
    }

    /// <summary>Makes a hook for a machine of the calling controller device's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The hook asked for.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hook.</returns>
    /// <exception cref="CoordinatorException">The caller, the machine or the request does not hold.</exception>
    public async Task<HookDto> CreateAsync(DeviceRecord actor, CreateHookRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = RequirePersonController(actor);
        if (request is not { MachineId.Length: > 0 and <= 128 } || request.Source is not (HookSources.IncidentIo or HookSources.Webhook))
            throw Invalid($"A hook names a machine and its source: {HookSources.IncidentIo} or {HookSources.Webhook}.");
        var label = string.IsNullOrWhiteSpace(request.Label) ? (request.Source == HookSources.IncidentIo ? "incident.io" : "Webhook") : request.Label.Trim();
        if (label.Length > MaxLabelLength || label.Any(char.IsControl)) throw Invalid($"The label of a hook is one line of at most {MaxLabelLength} characters.");
        var machine = await _store.GetDeviceAsync(request.MachineId, cancellationToken).ConfigureAwait(false);
        if (machine is null || machine.OrganizationId != actor.OrganizationId || machine.Kind != FleetDeviceKinds.Node)
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No machine of the organization has this id.");
        if (machine.Status != DeviceStatuses.Approved) throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, "The machine is not a member of the device list.");
        // Personal scope: a person's hooks go to the machines they own; a team's come with access control.
        if (!string.Equals(machine.OwnerPersonId, person, StringComparison.Ordinal))
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A person makes hooks for the machines they own only.");
        if ((await _hooks.ListHooksAsync(actor.OrganizationId, person, cancellationToken).ConfigureAwait(false)).Count >= MaxHooksPerPerson)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.HookConflict, $"A person has at most {MaxHooksPerPerson} hooks: delete one first.");
        var record = new HookRecord(actor.OrganizationId, NewId(), person, machine.Id, request.Source, label, actor.Id, _time.GetUtcNow(), null);
        await _hooks.CreateHookAsync(record, origin.Audit("hook.create", actor, record.HookId), CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Hook {Hook} for {Machine} was made by {Device}", record.HookId, record.MachineId, actor.Id);
        return ToDto(record);
    }

    /// <summary>Lists the hooks of the calling controller device's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hooks.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<HookListDto> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var person = RequirePersonController(actor);
        return new HookListDto([.. (await _hooks.ListHooksAsync(actor.OrganizationId, person, cancellationToken).ConfigureAwait(false)).Select(ToDto)]);
    }

    /// <summary>Deletes a hook of the caller's person, and its events: the address answers nothing any more.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The hook.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hooks left.</returns>
    /// <exception cref="CoordinatorException">No hook of the caller's person has this id.</exception>
    public async Task<HookListDto> DeleteAsync(DeviceRecord actor, string id, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = RequirePersonController(actor);
        var hook = await OwnedAsync(actor, person, id, cancellationToken).ConfigureAwait(false);
        await _hooks.DeleteHookAsync(actor.OrganizationId, hook.HookId, origin.Audit("hook.delete", actor, hook.HookId), CancellationToken.None).ConfigureAwait(false);
        return await ListAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the latest events of a hook of the caller's person, as facts.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The hook.</param>
    /// <param name="limit">The most events; null for 50.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The events, the newest first.</returns>
    /// <exception cref="CoordinatorException">No hook of the caller's person has this id.</exception>
    public async Task<HookEventListDto> ListEventsAsync(DeviceRecord actor, string id, int? limit, CancellationToken cancellationToken)
    {
        var person = RequirePersonController(actor);
        var hook = await OwnedAsync(actor, person, id, cancellationToken).ConfigureAwait(false);
        var events = await _hooks.ListHookEventsAsync(actor.OrganizationId, hook.HookId, Math.Clamp(limit ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);
        return new HookEventListDto([.. events.Select(static item => new HookEventDto(item.DeliveryId, item.ReceivedAt, item.State, item.Message, item.AnsweredAt))]);
    }

    /// <summary>
    /// Takes an event a tool pushed to a hook: once (the same event pushed again is the first delivery), within the size
    /// and the pace a hook takes, and hands it to the machine of the hook when it is online.
    /// </summary>
    /// <param name="id">The hook the path names.</param>
    /// <param name="headers">The headers of the request.</param>
    /// <param name="body">Its body.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The receipt.</returns>
    /// <exception cref="CoordinatorException">No hook has this id, the body is too large, or the hook takes too many events.</exception>
    public async Task<HookReceiptDto> ReceiveAsync(string id, IHeaderDictionary headers, byte[] body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(id) || id.Length > 64 || await _hooks.GetHookAsync(id, cancellationToken).ConfigureAwait(false) is not { } hook)
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.HookNotFound, "No hook has this address.");
        if (body.Length > MaxBodyBytes) throw new CoordinatorException(StatusCodes.Status413PayloadTooLarge, CoordinatorErrorCodes.InvalidBody, $"A hook takes events of at most {MaxBodyBytes} bytes.");
        var now = _time.GetUtcNow();
        if (!Admit(hook.HookId, now)) throw new CoordinatorException(StatusCodes.Status429TooManyRequests, CoordinatorErrorCodes.HookBusy, $"A hook takes at most {MaxEventsPerMinute} events a minute.");
        await ExpireAsync(now).ConfigureAwait(false);

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            if (kept.Count == MaxHeaders) break;
            var lower = name.ToLowerInvariant();
            if (Dropped.Contains(lower) || lower.StartsWith("x-forwarded-", StringComparison.Ordinal) || lower.StartsWith("proxy-", StringComparison.Ordinal)) continue;
            var value = values.ToString();
            if (value.Length <= MaxHeaderLength) kept[lower] = value;
        }

        // A sender that retries keeps its id and its body; a forged event with a known id has another body.
        var named = kept.GetValueOrDefault("webhook-id") ?? kept.GetValueOrDefault("x-github-delivery") ?? kept.GetValueOrDefault("x-request-id") ?? string.Empty;
        var key = Convert.ToHexStringLower(SHA256.HashData([.. Encoding.UTF8.GetBytes(named + "\n"), .. body]));
        var record = new HookEventRecord(hook.OrganizationId, Guid.CreateVersion7().ToString("N"), hook.HookId, hook.MachineId, key, now, now + Waits, HookEventStates.Pending,
            null, null, JsonSerializer.Serialize(kept, HookStorageJsonContext.Default.DictionaryStringString), body);
        var (delivery, duplicate) = await _hooks.AddHookEventAsync(record, CancellationToken.None).ConfigureAwait(false);
        if (!duplicate)
        {
            _logger.LogInformation("Hook {Hook} received an event of {Bytes} bytes for {Machine}", hook.HookId, body.Length, hook.MachineId);
            if (_presence.Get(hook.MachineId) is { } session) _ = PushAsync(session, [record]);
        }

        return new HookReceiptDto(delivery, duplicate);
    }

    /// <summary>Hands a machine that connects the events that wait for it.</summary>
    /// <param name="session">Its control connection.</param>
    /// <returns>A task that completes once they were sent or could not be.</returns>
    public async Task SendPendingAsync(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Device.Kind != FleetDeviceKinds.Node) return;
        try
        {
            await ExpireAsync(_time.GetUtcNow()).ConfigureAwait(false);
            var pending = await _hooks.ListPendingHookEventsAsync(session.Device.OrganizationId, session.Device.Id, _time.GetUtcNow(), PendingPerConnection).ConfigureAwait(false);
            if (pending.Count > 0) await PushAsync(session, pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogDebug("The hook events were not sent to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    /// <summary>Records what a machine did with the events it was given, and forgets their content.</summary>
    /// <param name="device">The machine.</param>
    /// <param name="acks">Its answers.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are recorded.</returns>
    /// <exception cref="CoordinatorException">The answers do not read.</exception>
    public async Task AcknowledgeAsync(DeviceRecord device, FleetHookAcks? acks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (acks?.Acks is not { Count: > 0 and <= 1000 } list || list.Any(static ack => ack is not { DeliveryId.Length: > 0 and <= 64 }))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, "hooks.ack needs one answer per event.");
        var now = _time.GetUtcNow();
        foreach (var ack in list)
        {
            var message = ack.Message is { } text ? text.ReplaceLineEndings(" ").Trim() is var line && line.Length > 500 ? line[..500] : line : null;
            await _hooks.AnswerHookEventAsync(device.OrganizationId, device.Id, ack.DeliveryId, ack.Accepted ? HookEventStates.Accepted : HookEventStates.Refused, message, now,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PushAsync(ControlSession session, IReadOnlyList<HookEventRecord> events)
    {
        try
        {
            foreach (var item in events)
            {
                var headers = item.Headers is { } json ? JsonSerializer.Deserialize(json, HookStorageJsonContext.Default.DictionaryStringString) ?? [] : [];
                var payload = new FleetHookEvent(item.DeliveryId, item.HookId, item.ReceivedAt, headers, Convert.ToBase64String(item.Body ?? []));
                using var timeout = new CancellationTokenSource(PushTimeout);
                await session.Connection.SendEventAsync(new FleetEvent(FleetHookMethods.Event,
                    JsonSerializer.SerializeToElement(payload, FleetHookServerJsonContext.Default.FleetHookEvent)), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            // The machine gets them when it connects next: they stay pending until it answers, or expire.
            _logger.LogDebug("The hook events were not handed to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    private async Task ExpireAsync(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _expiredAt < ExpireEvery && now >= _expiredAt) return;
            _expiredAt = now;
        }

        var expired = await _hooks.ExpireHookEventsAsync(now, now - KeptFor, CancellationToken.None).ConfigureAwait(false);
        if (expired > 0) _logger.LogInformation("{Count} hook events expired before their machine took them", expired);
    }

    // At most so many events a minute per hook, counted in memory: a restart forgets the count, not the events.
    private bool Admit(string hookId, DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            if (_rates.Count > 10_000) foreach (var stale in _rates.Where(pair => pair.Value.Minute != minute).Select(static pair => pair.Key).ToArray()) _rates.Remove(stale);
            var (at, count) = _rates.GetValueOrDefault(hookId);
            if (at != minute) count = 0;
            if (count >= MaxEventsPerMinute) return false;
            _rates[hookId] = (minute, count + 1);
            return true;
        }
    }

    private async Task<HookRecord> OwnedAsync(DeviceRecord actor, string person, string id, CancellationToken cancellationToken)
    {
        var hook = string.IsNullOrEmpty(id) ? null : await _hooks.GetHookAsync(id, cancellationToken).ConfigureAwait(false);
        if (hook is null || hook.OrganizationId != actor.OrganizationId || hook.OwnerPersonId != person)
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.HookNotFound, "No hook of yours has this id.");
        return hook;
    }

    private static string RequirePersonController(DeviceRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is not { } person)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of a person manages hooks.");
        return person;
    }

    private static HookDto ToDto(HookRecord record)
        => new(record.HookId, FleetHookMethods.IngressPath + "/" + record.HookId, record.MachineId, record.Source, record.Label, record.CreatedAt, record.LastReceivedAt, record.Pending);

    // 26 characters of base 32: 130 bits, an address nobody guesses.
    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[26];
        RandomNumberGenerator.Fill(bytes);
        var id = new char[26];
        for (var index = 0; index < id.Length; index++) id[index] = Alphabet[bytes[index] & 31];
        return new string(id);
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.HookInvalid, message);
}

/// <summary>The source-generated contracts the store of hooks writes.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class HookStorageJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
