using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Telemetry;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>
/// The telemetry setting of the fleet (<c>doc/fleet/observability.md</c>): an owner's controller device signs it and
/// sends it with <c>PUT /api/v1/telemetry</c>; the server checks the signature, keeps it, applies it to its own telemetry
/// at once, and pushes it, as signed, to the devices online (<see cref="FleetServerMethods.TelemetrySettings"/>), and to
/// each device that connects later. The headers of the server route stay here: they are sent to the collector only.
/// </summary>
internal sealed class TelemetrySettingsService
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);

    private readonly ICoordinatorStore _store;
    private readonly PresenceRegistry _presence;
    private readonly ServerTelemetry _telemetry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // One setting at a time, from the read of the one held to its application: two set at once could otherwise both be
    // stored in turn and the older one applied last to the server's own telemetry and forwarding.
    private readonly SemaphoreSlim _setGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="TelemetrySettingsService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="presence">The devices online, to push a new setting to.</param>
    /// <param name="telemetry">The telemetry of the server, which applies the setting.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    public TelemetrySettingsService(ICoordinatorStore store, PresenceRegistry presence, ServerTelemetry telemetry, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _presence = presence;
        _telemetry = telemetry;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the setting, with the names of the headers only the server holds, never their values; for an owner's controller device only.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of an owner.</exception>
    public async Task<TelemetrySettingsDto> GetAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        await RequireOwnerControllerAsync(actor, cancellationToken).ConfigureAwait(false);
        return ToDto(await _store.GetTelemetrySettingsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Replaces the setting with one the calling controller device signed, numbered after the one held; applies it and
    /// pushes it to the devices online.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The signed setting and the headers of the server route.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The setting held now.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, the setting does not hold or is not signed by it, or it is not newer.</exception>
    public async Task<TelemetrySettingsDto> SetAsync(DeviceRecord actor, SetTelemetrySettingsRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        try
        {
            var result = await SetCoreAsync(actor, request, origin, cancellationToken).ConfigureAwait(false);
            _telemetry.Metrics.TelemetrySettings(null);
            return result;
        }
        catch (CoordinatorException exception)
        {
            _telemetry.Metrics.TelemetrySettings(exception.Code);
            throw;
        }
    }

    /// <summary>Sends the setting held, if any, to a device that just connected.</summary>
    /// <param name="session">Its control connection.</param>
    /// <returns>A task that completes once it was sent or could not be.</returns>
    public async Task SendCurrentAsync(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            if (await _store.GetTelemetrySettingsAsync(session.Device.OrganizationId).ConfigureAwait(false) is { } settings)
                await PushAsync(session, new FleetSignedTelemetrySettings(settings.Payload, settings.Signature)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogDebug("The telemetry setting was not sent to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    private async Task<TelemetrySettingsDto> SetCoreAsync(DeviceRecord actor, SetTelemetrySettingsRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        await RequireOwnerControllerAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.Settings is not { Payload.Length: > 0, Signature.Length: > 0 } signed || signed.Payload.Length > 64 * 1024)
            throw Invalid("The request needs a setting signed by the calling device.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        if (!key.Verify(FleetTelemetrySettingsDocuments.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The signature of the setting does not verify with the key of the calling device.");
        if (!FleetTelemetrySettingsDocuments.TryParse(signed.Payload, out var settings)) throw Invalid("The setting does not read.");
        if (!string.Equals(settings.SignerDeviceId, actor.Id, StringComparison.Ordinal)) throw Invalid("The setting must name the device that signs it.");
        if (!string.Equals(settings.OrganizationId, actor.OrganizationId, StringComparison.Ordinal)) throw Invalid("The setting must name the organization of the server.");
        if (!FleetTelemetrySettingsDocuments.TryValidate(settings, out var reason)) throw Invalid($"The setting does not hold: {reason}.");
        var given = request.ServerHeaders ?? new Dictionary<string, string>();
        var keep = request.KeepServerHeaders ?? [];
        if ((given.Count > 0 || keep.Count > 0) && settings.Route != FleetTelemetryRoutes.Server) throw Invalid("The headers of a direct route are part of the signed setting.");
        if (keep.Count > FleetTelemetrySettingsDocuments.MaxHeaders) throw Invalid("Too many headers to keep.");

        TelemetrySettingsRecord record;
        IReadOnlyDictionary<string, string>? serverHeaders;
        await _setGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var held = await _store.GetTelemetrySettingsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (held is not null && settings.Sequence <= held.Sequence)
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.TelemetryConflict, $"The setting must be numbered after the one held ({held.Sequence}): read it again.");

            // The owner never reads the values of the headers again: a header kept by its name keeps the value held.
            var merged = new Dictionary<string, string>(given, StringComparer.OrdinalIgnoreCase);
            var heldHeaders = new Dictionary<string, string>(ServerTelemetry.ReadHeaders(held?.ServerHeaders) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var name in keep)
            {
                if (merged.ContainsKey(name ?? string.Empty)) continue;
                if (name is null || !heldHeaders.TryGetValue(name, out var value)) throw Invalid($"No header '{name}' is held to keep.");
                merged[name] = value;
            }

            serverHeaders = merged.Count > 0 ? merged : null;
            if (serverHeaders is not null && !FleetTelemetrySettingsDocuments.TryValidateHeaders(serverHeaders, out var headerReason))
                throw Invalid($"The headers do not hold: {headerReason}.");

            record = new TelemetrySettingsRecord(actor.OrganizationId, settings.Sequence, signed.Payload, signed.Signature, actor.Id,
                serverHeaders is null ? null : JsonSerializer.Serialize(serverHeaders.ToDictionary(), Fleet.Protocol.FleetTelemetryJsonContext.Default.DictionaryStringString),
                _time.GetUtcNow());
            // Where the machines send is a security decision: the audit says it, never the headers.
            var audit = origin.Audit("telemetry.set", actor, actor.OrganizationId, details: Details(settings, serverHeaders?.Count ?? 0));
            // Once stored, it is applied whatever the caller does: the server never holds one setting and runs another.
            if (!await _store.SetTelemetrySettingsAsync(record, audit, CancellationToken.None).ConfigureAwait(false))
                throw CoordinatorException.Conflict(CoordinatorErrorCodes.TelemetryConflict, "Another setting was stored meanwhile: read it again.");

            _telemetry.Apply(record, actor.OrganizationId);
        }
        finally
        {
            _setGate.Release();
        }

        foreach (var session in _presence.List(actor.OrganizationId)) _ = PushAsync(session, signed);
        _logger.LogInformation("The telemetry setting {Sequence} of the fleet was set by {Device}: {State}", settings.Sequence, actor.Id,
            settings.Enabled ? $"{settings.Route} to {settings.Endpoint}" : "off");
        return ToDto(record);
    }

    private async Task PushAsync(ControlSession session, FleetSignedTelemetrySettings signed)
    {
        try
        {
            using var timeout = new CancellationTokenSource(PushTimeout);
            await session.Connection.SendEventAsync(new FleetEvent(FleetServerMethods.TelemetrySettings,
                JsonSerializer.SerializeToElement(signed, FleetServerJsonContext.Default.FleetSignedTelemetrySettings)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            // The device reads it again when it connects next.
            _logger.LogDebug("The telemetry setting was not pushed to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    private async Task RequireOwnerControllerAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of an owner may read or set the telemetry of the fleet.");
        var person = actor.OwnerPersonId is null ? null : await _store.GetPersonAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        if (person is null || person.Role != PersonRoles.Owner)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only an owner may read or set the telemetry of the fleet.");
    }

    // The names of the headers of the server route, never their values: they leave the server toward the collector only.
    private static TelemetrySettingsDto ToDto(TelemetrySettingsRecord? record)
        => record is null ? new TelemetrySettingsDto(null, null, null)
            : new TelemetrySettingsDto(new FleetSignedTelemetrySettings(record.Payload, record.Signature),
                ServerTelemetry.ReadHeaders(record.ServerHeaders)?.ToDictionary(static pair => pair.Key, static _ => string.Empty, StringComparer.OrdinalIgnoreCase), record.UpdatedAt);

    private static string Details(FleetTelemetrySettings settings, int serverHeaders)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", settings.Sequence);
            writer.WriteBoolean("enabled", settings.Enabled);
            writer.WriteString("route", settings.Route);
            writer.WriteString("endpoint", settings.Endpoint);
            writer.WriteStartArray("signals");
            foreach (var signal in settings.Signals) writer.WriteStringValue(signal);
            writer.WriteEndArray();
            writer.WriteNumber("headers", (settings.Headers?.Count ?? 0) + serverHeaders);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.TelemetryInvalid, message);
}
