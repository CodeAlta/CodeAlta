using System.Collections.Concurrent;
using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Control;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>The options of the <see cref="HealthService"/>.</summary>
public sealed class HealthOptions
{
    /// <summary>Gets how long a problem a machine reports must last before its owner is told.</summary>
    public TimeSpan LastingAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long a machine must be offline before its owner is told.</summary>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets how often the server looks for machines offline or behind its version.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets whether the server looks by itself, on a timer (the default); tests that step it by hand turn it off.</summary>
    public bool RunLoop { get; init; } = true;
}

/// <summary>
/// The health of the machines (<c>doc/fleet/observability.md</c>, health checks): the problems each machine reports in its
/// heartbeat, and those the server sees for itself (a machine offline longer than <see cref="HealthOptions.OfflineAfter"/>, a
/// version behind the server's). A problem that lasts is told once to the owner of the machine, and marked resolved when it ends;
/// one that comes back later is a new problem. The store is written only when the problems of a machine change or one starts to
/// last, not at every heartbeat.
/// </summary>
internal sealed class HealthService : IAsyncDisposable
{
    private const int MaxProblems = 16;
    private const int MaxTextLength = 128;

    private readonly ICoordinatorStore _store;
    private readonly IBudgetStore _health;
    private readonly NotificationService _notifications;
    private readonly PresenceRegistry _presence;
    private readonly ServerUpdateStatusSource _server;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HealthOptions _options;
    private readonly ConcurrentDictionary<string, Reported> _reported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="HealthService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="health">The store of the problems and notifications.</param>
    /// <param name="notifications">The notifications of people.</param>
    /// <param name="presence">The devices online.</param>
    /// <param name="server">The versions of the server.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    /// <param name="options">The thresholds.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public HealthService(ICoordinatorStore store, IBudgetStore health, NotificationService notifications, PresenceRegistry presence, ServerUpdateStatusSource server,
        TimeProvider time, ILogger logger, HealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        (_store, _health, _notifications, _presence, _server, _time, _logger, _options) = (store, health, notifications, presence, server, time, logger, options);
    }

    /// <summary>Starts looking for machines offline or behind on a timer; nothing when the options turn the loop off.</summary>
    public void Start()
    {
        if (_options.RunLoop) _loop = Task.Run(LoopAsync);
    }

    /// <summary>Takes the problems a machine reports in its heartbeat.</summary>
    /// <param name="device">The machine.</param>
    /// <param name="problems">Its problems now; an empty list for none.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are kept.</returns>
    /// <exception cref="CoordinatorException">The problems do not read (<see cref="CoordinatorErrorCodes.PublicationInvalid"/>).</exception>
    public async Task ReportAsync(DeviceRecord device, IReadOnlyList<FleetHealthProblem> problems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count > MaxProblems || problems.Any(static problem => problem is null || !FleetHealthKinds.IsReported(problem.Kind)
                || problem.Subject is { Length: > MaxTextLength } || problem.Detail is { Length: > MaxTextLength }))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid,
                $"A heartbeat reports at most {MaxProblems} problems of the kinds {string.Join(", ", FleetHealthKinds.Reported)}, with a subject and a detail of at most {MaxTextLength} characters.");
        var now = _time.GetUtcNow();
        _lastSeen[device.Id] = now;
        var kept = problems.Select(static problem => (problem.Kind, Subject: Clean(problem.Subject) ?? string.Empty, Detail: Clean(problem.Detail)))
            .DistinctBy(static problem => (problem.Kind, problem.Subject)).ToArray();
        var text = string.Join('\n', kept.Select(static problem => problem.Kind + "\u001f" + problem.Subject).Order(StringComparer.Ordinal));
        // The same problems, none of them due to be told yet: nothing is written.
        if (_reported.TryGetValue(device.Id, out var held) && held.Text == text && (held.Due is not { } waiting || waiting > now)) return;
        var due = await ApplyAsync(device, kept, FleetHealthKinds.Reported, _options.LastingAfter, now, cancellationToken).ConfigureAwait(false);
        _reported[device.Id] = new Reported(text, due);
    }

    /// <summary>Looks at every machine once: those offline longer than the threshold, those behind the version of the server.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes once they were looked at.</returns>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var organization = await _store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        if (organization is null) return;
        var now = _time.GetUtcNow();
        var machines = (await _store.ListMachinesAsync(organization.Id, cancellationToken).ConfigureAwait(false)).ToDictionary(static machine => machine.DeviceId, StringComparer.Ordinal);
        UpdateVersion.TryParse(_server.Read().Running, out var serverVersion);
        foreach (var device in await _store.ListDevicesAsync(organization.Id, cancellationToken).ConfigureAwait(false))
        {
            // Nodes only: a laptop is offline when its lid is closed, which is no problem.
            if (device.Kind != FleetDeviceKinds.Node || device.Status != DeviceStatuses.Approved || device.OwnerPersonId is null) continue;
            machines.TryGetValue(device.Id, out var machine);
            List<(string Kind, string Subject, string? Detail)> offline = [];
            if (_presence.IsOnline(device.Id))
            {
                _lastSeen[device.Id] = now;
            }
            else
            {
                var seen = _lastSeen.TryGetValue(device.Id, out var known) ? known : machine?.UpdatedAt ?? device.ApprovedAt ?? device.CreatedAt;
                if (now - seen >= _options.OfflineAfter)
                    offline.Add((FleetHealthKinds.Offline, string.Empty, ((long)(now - seen).TotalHours).ToString(CultureInfo.InvariantCulture)));
            }

            await ApplyAsync(device, offline, [FleetHealthKinds.Offline], TimeSpan.Zero, now, cancellationToken).ConfigureAwait(false);
            List<(string Kind, string Subject, string? Detail)> behind = [];
            if (serverVersion is not null && Version(machine) is { } text && UpdateVersion.TryParse(text, out var version) && version < serverVersion)
                behind.Add((FleetHealthKinds.VersionBehind, string.Empty, text));
            await ApplyAsync(device, behind, [FleetHealthKinds.VersionBehind], TimeSpan.Zero, now, cancellationToken).ConfigureAwait(false);
        }
    }

    // Keeps the problems of kinds a report speaks of; tells the owner of each that lasts, once; resolves those that ended. Gives
    // when the next problem not told yet is due to be told.
    private async Task<DateTimeOffset?> ApplyAsync(DeviceRecord device, IReadOnlyList<(string Kind, string Subject, string? Detail)> problems, IReadOnlyCollection<string> kinds,
        TimeSpan lastingAfter, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (current, ended) = await _health.ReportHealthAsync(device.Id, problems, kinds, now, cancellationToken).ConfigureAwait(false);
        var resolved = ended.Where(static problem => problem.NotificationId is not null).Select(static problem => problem.NotificationId!).ToArray();
        if (resolved.Length > 0)
        {
            await _health.ResolveNotificationsAsync(resolved, now, cancellationToken).ConfigureAwait(false);
            if (device.OwnerPersonId is { } owner)
                await _notifications.TellAsync(device.OrganizationId, owner, FleetBudgetMethods.NotificationsChanged, controllers: true, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset? due = null;
        foreach (var problem in current.Where(problem => kinds.Contains(problem.Kind) && problem.NotificationId is null))
        {
            var at = problem.FirstSeen + lastingAfter;
            if (at > now)
            {
                due = due is { } earlier && earlier < at ? earlier : at;
                continue;
            }

            if (device.OwnerPersonId is not { } person) continue;
            // Once per problem: a problem that ends and comes back later is another one.
            var key = $"health:{device.Id}:{problem.Kind}:{problem.Subject}:{problem.FirstSeen.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            var id = await _notifications.NotifyAsync(device.OrganizationId, person, key, FleetNotificationKinds.Health, Severity(problem.Kind), null, device.Id, problem.Kind,
                problem.Subject.Length == 0 ? null : problem.Subject, problem.Detail, cancellationToken).ConfigureAwait(false);
            if (id is not null)
            {
                await _health.SetHealthNotificationAsync(device.Id, problem.Kind, problem.Subject, id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("The machine {Device} has a lasting problem: {Kind} {Subject}", device.Id, problem.Kind, problem.Subject);
            }
        }

        return due;
    }

    private static string? Version(MachineRecord? machine)
    {
        if (machine is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(machine.Inventory, FleetServerJsonContext.Default.FleetMachineInventory)?.AppVersion;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Severity(string kind) => kind switch
    {
        FleetHealthKinds.SignInFailed or FleetHealthKinds.DiskLow => "error",
        FleetHealthKinds.VersionBehind => "info",
        _ => "warning",
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : new string([.. text.Select(static c => char.IsControl(c) ? ' ' : c)]).Trim();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogWarning("The health of the machines was not read: {Reason}", exception.Message);
            }

            try
            {
                await Task.Delay(_options.Interval, _time, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private sealed record Reported(string Text, DateTimeOffset? Due);
}
