using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Control;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Api;

/// <summary>
/// The REST API v1 of the server (<c>doc/fleet/protocol.md</c>, the server's REST API): JSON in camelCase, errors as
/// problem details with a stable code and a hint, every mutation audited, every call signed by a device key.
/// </summary>
internal static class ApiEndpoints
{
    private const int MaxAuditPage = 500;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").WithTags("v1");

        api.MapGet("/server/health", async (ICoordinatorStore store, CancellationToken token) =>
            {
                var organization = await store.GetOrganizationAsync(token).ConfigureAwait(false);
                return new ServerHealthDto("ok", ServerVersion.Current, organization is not null, await store.GetSchemaVersionAsync(token).ConfigureAwait(false));
            })
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .WithSummary("The health and version of the server; needs no device.");

        api.MapGet("/server/update", (ServerUpdateStatusSource update) => update.Read())
            .Produces<FleetUpdateStatus>()
            .WithSummary("The versions of the server and where its self-update stands: the running and installed versions, the one on trial, the switch that waits, the versions rolled back.");

        api.MapPost("/setup", async (SetupRequest? request, HttpContext context, SetupService setup, Telemetry.ServerTelemetry telemetry) =>
            {
                var response = await setup.SetUpAsync(request, DeviceAuthenticationMiddleware.GetSigned(context), Origin(context), context.RequestAborted).ConfigureAwait(false);
                // The organization joins the resource of the server's telemetry.
                telemetry.Apply(null, response.Organization.Id);
                return Results.Json(response, ServerApiJsonContext.Default.SetupResponse, statusCode: StatusCodes.Status201Created);
            })
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<SetupResponse>(StatusCodes.Status201Created)
            .WithSummary("Sets the server up with its one-time code, from its own machine: the organization, its owner and the owner's first controller device.");

        api.MapPost("/enroll", async (EnrollRequest? request, HttpContext context, DeviceService devices) =>
                Results.Json(await devices.EnrollAsync(request, DeviceAuthenticationMiddleware.GetSigned(context), Origin(context), context.RequestAborted).ConfigureAwait(false),
                    ServerApiJsonContext.Default.EnrollResponse, statusCode: StatusCodes.Status201Created))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<EnrollResponse>(StatusCodes.Status201Created)
            .WithSummary("Registers a machine's key as a pending device with an enrollment token; the request is signed by that key.");

        api.MapGet("/organization", async (HttpContext context, ICoordinatorStore store) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var organization = await store.GetOrganizationAsync(context.RequestAborted).ConfigureAwait(false)
                    ?? throw CoordinatorException.NotFound(CoordinatorErrorCodes.NotSetUp, "The server is not set up.");
                var people = await store.ListPeopleAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                return new OrganizationDto(organization.Id, organization.Name, organization.CreatedAt, people.Select(static person => person.ToDto()).ToArray());
            })
            .WithSummary("The organization of the server and its people.");

        api.MapGet("/devices", async (HttpContext context, ICoordinatorStore store, PresenceRegistry presence) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var devices = await store.ListDevicesAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                return new ListResponse<DeviceDto>(devices.Select(device => device.ToDto(presence.IsOnline(device.Id))).ToArray());
            })
            .WithSummary("The devices of the organization: controller devices and nodes, pending, approved and revoked.");

        api.MapGet("/devices/{id}", async (string id, HttpContext context, ICoordinatorStore store, PresenceRegistry presence) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var device = await store.GetDeviceAsync(id, context.RequestAborted).ConfigureAwait(false);
                if (device is null || device.OrganizationId != actor.OrganizationId) throw CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No device has this id.");
                return device.ToDto(presence.IsOnline(device.Id));
            })
            .WithSummary("A device.");

        api.MapPost("/devices/{id}/approve", (string id, DeviceListChangeRequest? request, HttpContext context, DeviceService devices) =>
                AuditedAsync(context, "device.approve", id, actor => devices.ApproveAsync(actor, id, request, Origin(context), context.RequestAborted)))
            .Produces<DeviceDto>()
            .WithSummary("Approves a pending device with the device-list entry that adds it, signed by the calling controller device.");

        api.MapPost("/devices/{id}/revoke", (string id, DeviceListChangeRequest? request, HttpContext context, DeviceService devices) =>
                AuditedAsync(context, "device.revoke", id, actor => devices.RevokeAsync(actor, id, request, Origin(context), context.RequestAborted)))
            .Produces<DeviceDto>()
            .WithSummary("Revokes a device: an approved one with the signed entry that removes it, a pending one without. Its connection is closed.");

        api.MapPost("/devices/self/leave", (LeaveRequest? request, HttpContext context, DeviceService devices) =>
                AuditedAsync(context, "device.leave", null, actor => devices.LeaveAsync(actor, request, Origin(context), context.RequestAborted)))
            .Produces<LeaveResponse>()
            .WithSummary("The calling device leaves the server: what it published, its copies of keys and the automations given to it are withdrawn.");

        api.MapPost("/recovery-keys", (CreateRecoveryKeyRequest? request, HttpContext context, DeviceService devices) =>
                AuditedAsync(context, "recovery_key.create", null, actor => devices.AddRecoveryKeyAsync(actor, request, Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<DeviceDto>(StatusCodes.Status201Created)
            .WithSummary("Records the recovery key of the calling controller device's person: the entry that adds it, signed by that device, and its sealed anchor.");

        api.MapGet("/recovery-keys/self", (HttpContext context, DeviceService devices) =>
                devices.GetRecoveryAnchorAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted))
            .WithSummary("The anchor of the device list a recovery key sealed, for that recovery key only.");

        api.MapPost("/recover", async (RecoverRequest? request, HttpContext context, DeviceService devices) =>
                Results.Json(await devices.RecoverAsync(request, DeviceAuthenticationMiddleware.GetSigned(context), Origin(context), context.RequestAborted).ConfigureAwait(false),
                    ServerApiJsonContext.Default.DeviceDto, statusCode: StatusCodes.Status201Created))
            .WithMetadata(new AllowUnknownDeviceAttribute())
            .Produces<DeviceDto>(StatusCodes.Status201Created)
            .WithSummary("Signs a new device in as a controller device of a person with the entry the person's recovery key signed; the request is signed by the new device's key.");

        api.MapGet("/device-list", async (HttpContext context, ICoordinatorStore store) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var entries = await store.ListDeviceListAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                return new ListResponse<DeviceListEntryDto>(entries.Select(static entry => entry.ToDto()).ToArray());
            })
            .WithSummary("The signed device list, from its first entry: devices verify it themselves.");

        api.MapPost("/enrollment-tokens", (EnrollmentTokenRequest? request, HttpContext context, DeviceService devices) =>
                AuditedAsync(context, "enrollment_token.create", null, actor => devices.CreateEnrollmentTokenAsync(actor, request, Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<EnrollmentTokenResponse>(StatusCodes.Status201Created)
            .WithSummary("Makes an enrollment token for a machine: single use, 10 minutes to 24 hours, shown once.");

        api.MapGet("/machines", async (HttpContext context, ICoordinatorStore store, PresenceRegistry presence) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var devices = (await store.ListDevicesAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false))
                    .ToDictionary(static device => device.Id, StringComparer.Ordinal);
                var machines = await store.ListMachinesAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                return new ListResponse<MachineDto>(machines.Select(machine =>
                {
                    devices.TryGetValue(machine.DeviceId, out var device);
                    return new MachineDto(machine.DeviceId, device?.Name ?? machine.DeviceId, device?.Kind ?? FleetDeviceKinds.Node, device?.Status ?? DeviceStatuses.Revoked,
                        presence.IsOnline(machine.DeviceId), machine.ReadInventory(), machine.UpdatedAt);
                }).ToArray());
            })
            .WithSummary("The machines of the organization and their last inventory.");

        api.MapGet("/sessions", async (HttpContext context, ICoordinatorStore store) =>
            {
                var actor = DeviceAuthenticationMiddleware.GetDevice(context);
                var sessions = await store.ListSessionsAsync(actor.OrganizationId, context.RequestAborted).ConfigureAwait(false);
                return new ListResponse<SessionDto>(sessions.Select(static session => session.ToDto()).ToArray());
            })
            .WithSummary("The published sessions: their facts and their sealed metadata, which the server cannot read.");

        api.MapPost("/statistics/query", async (StatisticsQueryRequest? request, HttpContext context, StatisticsService statistics) =>
                Results.Json(await statistics.QueryAsync(DeviceAuthenticationMiddleware.GetDevice(context), request, context.RequestAborted).ConfigureAwait(false),
                    FleetStatisticsJsonContext.Default.StatisticsQueryResponse))
            .Produces<StatisticsQueryResponse>()
            .WithSummary("Sums the statistics facts the machines of the calling device's person published: time, runs, tokens, costs and tool calls, by machine, project key, provider, model, day or month.");

        api.MapGet("/metadata-keys", (HttpContext context, MetadataKeyService keys)
                => keys.ListAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted))
            .WithSummary("The metadata keys of the calling device's person, each with the copy wrapped to that device; never a key in clear.");

        api.MapPost("/metadata-keys", (CreateMetadataKeyRequest? request, HttpContext context, MetadataKeyService keys) =>
                AuditedAsync(context, "metadata_key.create", request?.Id, actor => keys.CreateAsync(actor, request, Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<MetadataKeyDto>(StatusCodes.Status201Created)
            .WithSummary("Records the metadata key a controller device made for its person, wrapped to that person's devices.");

        api.MapPost("/metadata-keys/{id}/wraps", (string id, AddMetadataKeyWrapsRequest? request, HttpContext context, MetadataKeyService keys) =>
                AuditedAsync(context, "metadata_key.wrap", id, actor => keys.AddWrapsAsync(actor, id, request, Origin(context), context.RequestAborted)))
            .Produces<MetadataKeyDto>()
            .WithSummary("Adds copies of a metadata key, wrapped by a controller device that holds it, for devices of its person that have none.");

        api.MapGet("/audit", async (long? after, int? limit, HttpContext context, ICoordinatorStore store) =>
            {
                var size = Math.Clamp(limit ?? 100, 1, MaxAuditPage);
                var records = await store.ListAuditAsync(Math.Max(0, after ?? 0), size, context.RequestAborted).ConfigureAwait(false);
                var next = records.Count == size ? records[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                return new ListResponse<AuditRecordDto>(records.Select(static record => record.ToDto()).ToArray(), next);
            })
            .WithSummary("The audit log, by sequence; 'after' is the cursor of the next page.");

        api.MapGet("/audit/verify", async (HttpContext context, ICoordinatorStore store) =>
            {
                var verification = await store.VerifyAuditAsync(context.RequestAborted).ConfigureAwait(false);
                return new AuditVerificationDto(verification.IsValid, verification.Count, verification.BrokenAt);
            })
            .WithSummary("Checks the hash chain of the audit log.");

        api.MapGet("/telemetry", (HttpContext context, TelemetrySettingsService telemetry)
                => telemetry.GetAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted))
            .WithSummary("The telemetry setting of the fleet, signed by a controller device, and the headers only the server holds; for an owner's controller device.");

        api.MapPut("/telemetry", (SetTelemetrySettingsRequest? request, HttpContext context, TelemetrySettingsService telemetry) =>
                AuditedAsync(context, "telemetry.set", null, actor => telemetry.SetAsync(actor, request, Origin(context), context.RequestAborted)))
            .Produces<TelemetrySettingsDto>()
            .WithSummary("Sets the telemetry of the fleet with a setting the calling owner's controller device signed: applied at once and pushed to the machines, which check its signature.");

        api.MapGet("/updates", (HttpContext context, UpdateCampaignService updates)
                => updates.GetAsync(DeviceAuthenticationMiddleware.GetDevice(context), context.RequestAborted))
            .Produces<UpdatesDto>()
            .WithSummary("The updates of the fleet: the versions of the server, the update policy and the recent campaigns with each machine's state; for a controller device.");

        api.MapGet("/updates/campaigns/{id}", (string id, HttpContext context, UpdateCampaignService updates)
                => updates.GetCampaignAsync(DeviceAuthenticationMiddleware.GetDevice(context), id, context.RequestAborted))
            .Produces<UpdateCampaignDto>()
            .WithSummary("An update campaign and the state of each of its machines; for a controller device.");

        api.MapPost("/updates/campaigns", (CreateUpdateCampaignRequest? request, HttpContext context, UpdateCampaignService updates) =>
                AuditedAsync(context, "update.campaign.create", null, actor => updates.CreateAsync(actor, request, Origin(context), context.RequestAborted),
                    StatusCodes.Status201Created))
            .Produces<UpdateCampaignDto>(StatusCodes.Status201Created)
            .WithSummary("Starts an update campaign with a manifest the calling owner's controller device signed: nodes first, a few at a time, then the server.");

        api.MapPost("/updates/campaigns/{id}/cancel", (string id, HttpContext context, UpdateCampaignService updates) =>
                AuditedAsync(context, "update.campaign.cancel", id, actor => updates.CancelAsync(actor, id, Origin(context), context.RequestAborted)))
            .Produces<UpdateCampaignDto>()
            .WithSummary("Cancels an update campaign before the server's step; nodes that switched stay on the new version.");

        api.MapPut("/updates/policy", (SetUpdatePolicyRequest? request, HttpContext context, UpdateCampaignService updates) =>
                AuditedAsync(context, "update.policy.set", null, actor => updates.SetPolicyAsync(actor, request, Origin(context), context.RequestAborted)))
            .Produces<UpdatePolicyDto>()
            .WithSummary("Sets the update policy of the fleet (manual, pinned, automatic) with a policy the calling owner's controller device signed.");

        AutomationEndpoints.Map(api);
        SecretEndpoints.Map(api);

        app.MapPost(FleetTelemetryPaths.Forward + "/{signal}", (string signal, HttpContext context, OtlpForwarder forwarder) => forwarder.HandleAsync(context, signal))
            .ExcludeFromDescription();
        app.Map(FleetServerMethods.ControlPath, (HttpContext context, ControlService control) => control.HandleAsync(context)).ExcludeFromDescription();
        app.Map(FleetServerMethods.RelayPath + "/{ticket}", (string ticket, HttpContext context, RelayHub relays) => relays.HandleAsync(context, ticket)).ExcludeFromDescription();
    }

    internal static RequestOrigin Origin(HttpContext context) => new(context.Connection.RemoteIpAddress, context.TraceIdentifier);

    // A refused mutation is audited too, with its code: a device that tries what it may not do leaves a trace.
    private static async Task<IResult> AuditedAsync<T>(HttpContext context, string action, string? target, Func<DeviceRecord, Task<T>> mutate,
        int status = StatusCodes.Status200OK)
    {
        var actor = DeviceAuthenticationMiddleware.GetDevice(context);
        try
        {
            var result = await mutate(actor).ConfigureAwait(false);
            return Results.Json(result, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ServerApiJsonContext.Default.GetTypeInfo(typeof(T))!, statusCode: status);
        }
        catch (CoordinatorException exception)
        {
            var store = context.RequestServices.GetRequiredService<ICoordinatorStore>();
            await store.AppendAuditAsync(Origin(context).Audit(action, actor, target, exception.Code), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
