using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Client;
using CodeAlta.ModelServers;
using CodeAlta.Orchestration.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Fleet;

/// <summary>
/// Refreshes the providers that name a model server when it becomes ready or its model catalog changes.
/// Activity (requests, slots and speed) does not refresh models. Changes during a probe queue another probe after it.
/// </summary>
public sealed class ModelServerProviderRefresh : IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly ModelHost? _local;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Machine, string Server), ServerView> _servers = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task _refresh = Task.CompletedTask;
    private bool _running;
    private bool _closed;

    /// <summary>Follows a local model host, if given; remote snapshots are supplied with <see cref="UpdateAsync"/>.</summary>
    /// <param name="host">The runtime whose configured providers are refreshed.</param>
    /// <param name="local">The model host of this machine, or null to follow remote snapshots only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public ModelServerProviderRefresh(CodeAltaHost host, ModelHost? local)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _local = local;
        if (local is not null)
        {
            local.Changed += OnLocalChanged;
            OnLocalChanged();
        }
    }

    private void OnLocalChanged()
    {
        if (_local is not null) _ = UpdateAsync(FleetModelServerTransports.LocalMachine, _local.ListServers());
    }

    /// <summary>Observes the current servers of a machine and queues model reads for the catalogs that changed.</summary>
    /// <param name="machine">The machine as providers name it (<c>local</c> or a paired machine's id).</param>
    /// <param name="servers">Its current servers; an empty list forgets previous observations, such as on reconnection.</param>
    /// <returns>A task that completes when the queued model reads finish.</returns>
    /// <exception cref="ArgumentException"><paramref name="machine"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="servers"/> is null.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation still owns unconfirmed dependencies.</exception>
    public Task UpdateAsync(string machine, IReadOnlyList<ServerView> servers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        ArgumentNullException.ThrowIfNull(servers);
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            foreach (var key in _servers.Keys.Where(key => key.Machine == machine && !servers.Any(server => server.Definition.Id == key.Server)).ToArray())
                _servers.Remove(key);
            foreach (var server in servers)
            {
                var key = (machine, server.Definition.Id);
                if (server.Status.State == "ready" && (!_servers.TryGetValue(key, out var before) || !SameCatalog(before, server)))
                    _pending.Add(machine + "/" + server.Definition.Id);
                _servers[key] = server;
            }

            if (!_running && _pending.Count > 0)
            {
                _running = true;
                _refresh = Task.Run(RefreshPendingAsync);
            }

            return _refresh;
        }
    }

    private static bool SameCatalog(ServerView before, ServerView after)
        => before.Status.State == after.Status.State && before.Status.ProcessId == after.Status.ProcessId && before.Status.StartedAt == after.Status.StartedAt
            && before.Definition.Models.Select(static model => (model.Model, model.Context, model.Vision))
                .SequenceEqual(after.Definition.Models.Select(static model => (model.Model, model.Context, model.Vision)))
            && before.Status.Models.Select(static model => (model.Model, model.State, model.Context, model.TrainedContext, model.Vision))
                .SequenceEqual(after.Status.Models.Select(static model => (model.Model, model.State, model.Context, model.TrainedContext, model.Vision)));

    private async Task RefreshPendingAsync()
    {
        while (true)
        {
            string[] references;
            lock (_gate)
            {
                if (_closed || _pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                references = [.. _pending];
                _pending.Clear();
            }

            foreach (var reference in references)
            {
                try
                {
                    var keys = new CodeAltaConfigStore(_host.CatalogOptions).LoadGlobalProviderDefinitions(includeDisabled: false)
                        .Where(definition => string.Equals(definition.ProviderType, "llama-cpp", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(definition.Server, reference, StringComparison.OrdinalIgnoreCase))
                        .Select(static definition => new ModelProviderId(definition.ProviderKey)).ToArray();
                    foreach (var key in keys)
                    {
                        // Do not join a probe that may already have read the server before this change.
                        try { await _host.ModelProviderInitializationService.RefreshProviderAfterChangeAsync(key, _stop.Token).ConfigureAwait(false); }
                        catch (KeyNotFoundException)
                        {
                            // Its configuration may be saved before registration, or removed during this read.
                            // The other providers of the server must still be refreshed.
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // A provider removed or being replaced is left to its configuration change; shutdown admits no probe.
                    if (LogManager.IsInitialized && !_stop.IsCancellationRequested)
                        LogManager.GetLogger("CodeAlta.Fleet.ModelServerProviders").Warn($"Could not refresh the providers of model server {reference}: {exception.Message}");
                }
            }
        }
    }

    /// <summary>Stops observing changes and joins the queued refreshes before the runtime is disposed.</summary>
    /// <returns>A task that completes when this observer has stopped.</returns>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation still owns unconfirmed dependencies.</exception>
    public async ValueTask DisposeAsync()
    {
        if (_local is not null) _local.Changed -= OnLocalChanged;
        Task refresh;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _pending.Clear();
            refresh = _refresh;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await refresh.ConfigureAwait(false);
        _stop.Dispose();
    }
}
