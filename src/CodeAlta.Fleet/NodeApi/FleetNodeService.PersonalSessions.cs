using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    private IFleetSessionAccess? _personalSessions;

    /// <summary>
    /// Gets the admission of the personal phone page hosted by this node. It is not registered on any wire protocol or
    /// agent tool, refuses shared machines, and uses the same policy, worktree, environment, budgets and seats as a device.
    /// </summary>
    public IFleetSessionAccess PersonalSessions
        => LazyInitializer.EnsureInitialized(ref _personalSessions, () => new PersonalSessionAccess(this));

    private void RequirePersonalAccess()
    {
        RequireWork();
        if (LetsOtherPeopleIn) throw new FleetException(FleetNodeErrorCodes.PolicyRefused, null, MobileSharedMachine);
    }

    private Task<T> PersonalOnce<T, TParameters>(string method, string? key, TParameters parameters, JsonTypeInfo<TParameters> parametersType,
        JsonTypeInfo<T> resultType, Func<Task<T>> run) where T : class
    {
        RequirePersonalAccess();
        key ??= Guid.NewGuid().ToString("N");
        if (key.Length is 0 or > 128 || key.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "An action id has 1 to 128 characters without spaces.");
        return _receipts.RunOnceAsync("personal:mobile:" + method + "|" + key, JsonSerializer.Serialize(parameters, parametersType), resultType, run);
    }

    private sealed class PersonalSessionAccess(FleetNodeService owner) : IFleetSessionAccess
    {
        public async Task<FleetSessionReadResult> ReadSessionAsync(FleetSessionReadParams parameters, CancellationToken cancellationToken = default)
        {
            owner.RequirePersonalAccess();
            var args = FleetSessionReads.Build(parameters, out var spec);
            if (spec.Acts)
                return await owner.PersonalOnce(spec.Name, parameters.ActionId, parameters, FleetNodeJsonContext.Default.FleetSessionReadParams,
                    FleetNodeJsonContext.Default.FleetSessionReadResult, ReadAsync).ConfigureAwait(false);
            return await ReadAsync().ConfigureAwait(false);

            async Task<FleetSessionReadResult> ReadAsync()
            {
                if (spec.Name == "session.set_provider")
                {
                    var session = await owner.RequireSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
                    var project = session.ProjectRef is { } projectId ? await owner._host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
                    var provider = owner.ResolveProvider(parameters.Options?.GetValueOrDefault("provider"), project?.ProjectPath, null);
                    _ = owner.RequirePolicyStart(session.SpaceId, provider, null, agent: false);
                }
                var alta = owner._options.Alta ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "The commands of the node are not ready.");
                var image = spec.Name == "session.image";
                var result = await alta(args, image ? 16 * 1024 * 1024 : Math.Clamp(parameters.MaxBytes ?? FleetSessionReads.DefaultMaxBytes, 16 * 1024, FleetSessionReads.MaxBytes),
                    cancellationToken).ConfigureAwait(false);
                owner.RequirePersonalAccess();
                var relinquished = spec.ListsSessions ? owner._host.RuntimeService.Relinquishments.ListRelinquished() : null;
                return new(result.ExitCode, [.. Records(result.Jsonl).Where(static record => Text(record, "type") != "alta.result")
                    .Where(record => relinquished is null || Text(record, "sessionId") is not { } id || !relinquished.Contains(id))
                    .Select(record => image ? ImagePiece(record, parameters.Options) : owner.SessionParent(record))], result.Truncated, result.Error);
            }
        }

        public Task<FleetSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            owner.RequirePersonalAccess();
            return owner.ReadSessionStateAsync(sessionId, cancellationToken);
        }

        public Task<FleetCreateSessionResult> CreateSessionAsync(FleetCreateSessionParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        {
            retryKey ??= Guid.NewGuid().ToString("N");
            if (parameters.Agent is not null) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A personal page submits the person's work, not an agent request.");
            owner.ValidateCreation(parameters, DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start);
            return owner.PersonalOnce("create", retryKey, parameters, FleetNodeJsonContext.Default.FleetCreateSessionParams,
                FleetNodeJsonContext.Default.FleetCreateSessionResult, () => owner.CreateSessionCoreAsync(null, "mobile:" + retryKey, parameters));
        }

        public Task<FleetCommandResult> SendAsync(FleetSendParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        {
            retryKey ??= Guid.NewGuid().ToString("N");
            if (parameters.Agent is not null) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A personal page submits the person's work.");
            return owner.PersonalOnce("send", retryKey, parameters, FleetNodeJsonContext.Default.FleetSendParams, FleetNodeJsonContext.Default.FleetCommandResult,
                () => owner.SendPersonAsync("mobile:" + retryKey, parameters, isolate: true, CancellationToken.None));
        }

        public Task<FleetQueueResult> QueueAsync(FleetQueueParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        {
            retryKey ??= Guid.NewGuid().ToString("N");
            if (parameters.Agent is not null) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A personal page submits the person's work.");
            return owner.PersonalOnce("queue", retryKey, parameters, FleetNodeJsonContext.Default.FleetQueueParams, FleetNodeJsonContext.Default.FleetQueueResult,
                () => owner.QueuePersonAsync(parameters, isolate: true));
        }

        public Task<FleetCommandResult> SteerAsync(FleetSteerParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        {
            retryKey ??= Guid.NewGuid().ToString("N");
            if (parameters.Agent is not null) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A personal page submits the person's work.");
            return owner.PersonalOnce("steer", retryKey, parameters, FleetNodeJsonContext.Default.FleetSteerParams, FleetNodeJsonContext.Default.FleetCommandResult,
                () => owner.SteerPersonAsync("mobile:" + retryKey, parameters, CancellationToken.None));
        }

        public Task<FleetCommandResult> AbortAsync(FleetAbortParams parameters, string? retryKey = null, CancellationToken cancellationToken = default)
        {
            retryKey ??= Guid.NewGuid().ToString("N");
            return owner.PersonalOnce("abort", retryKey, parameters, FleetNodeJsonContext.Default.FleetAbortParams, FleetNodeJsonContext.Default.FleetCommandResult,
                () => owner.AbortPersonAsync("mobile:" + retryKey, parameters, CancellationToken.None));
        }
    }
}
