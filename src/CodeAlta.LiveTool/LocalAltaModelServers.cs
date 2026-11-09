using System.Collections.Concurrent;
using System.Globalization;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.LiveTool;

/// <summary>
/// The model servers of one machine for the <c>alta llama</c> commands of its sessions: the model host of a host without a
/// window (<c>altanode</c>), the only machine it knows (<c>local</c>). A download, an install or an update a session starts
/// tells that session its end with a prompt.
/// </summary>
public sealed class LocalAltaModelServers : IAltaModelServers, IDisposable
{
    /// <summary>The id of the machine.</summary>
    public const string LocalMachine = "local";

    private readonly Func<ModelHost?> _host;
    private readonly string _name;
    private readonly Func<string, string, Task<string>>? _deliver;
    private readonly ConcurrentDictionary<string, string> _waiting = new(StringComparer.Ordinal);
    private ModelHost? _watched;

    /// <summary>Creates the view of a model host.</summary>
    /// <param name="host">Gives the model host once it runs, or null while it does not.</param>
    /// <param name="name">The name of the machine.</param>
    /// <param name="deliver">Gives a session a prompt (its id, the prompt); null to tell nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="name"/> is null.</exception>
    public LocalAltaModelServers(Func<ModelHost?> host, string name, Func<string, string, Task<string>>? deliver)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(name);
        _host = host;
        _name = name;
        _deliver = deliver;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AltaModelServersMachine>> ListMachinesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AltaModelServersMachine>>(_host() is null ? [] : [new AltaModelServersMachine(LocalMachine, _name, true, "online", true, true, null)]);

    /// <inheritdoc />
    public Task<ModelHostStatus> StatusAsync(string machine, CancellationToken cancellationToken) => Task.FromResult(Host(machine).GetStatus());

    /// <inheritdoc />
    public async Task<ModelHostActionResult> RunAsync(string machine, ModelHostAction action, string? sessionId, CancellationToken cancellationToken)
    {
        var host = Host(machine);
        Watch(host);
        var result = await ModelHostActions.RunAsync(host, action, sessionId is null ? "an agent" : "session " + sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionId is not null && _deliver is not null)
        {
            if (result.Download is { State: "queued" or "downloading" or "checking" } download) _waiting[download.Id] = sessionId;
            if (result.Task is { State: "running" or "waiting" } task) _waiting[task.Id] = sessionId;
            if (result.Server is { } server && action.Action == "setServer")
            {
                foreach (var started in host.GetStatus().Downloads.Where(item => item.State is "queued" or "downloading" or "checking"
                    && server.Definition.Models.Any(model => string.Equals(model.Model, item.Model, StringComparison.OrdinalIgnoreCase))))
                    _waiting.TryAdd(started.Id, sessionId);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HubModel>> SearchHubAsync(string? search, string? author, string? pipeline, string sort, int limit, CancellationToken cancellationToken)
        => Host(LocalMachine).Hub.SearchAsync(search, author, pipeline, sort, limit, cancellationToken);

    /// <inheritdoc />
    public async Task<AltaHubModel> ReadHubModelAsync(string repo, IReadOnlyList<string> machines, CancellationToken cancellationToken)
    {
        var host = Host(LocalMachine);
        var (details, quants, shape) = await host.InspectRepoAsync(repo, cancellationToken).ConfigureAwait(false);
        var status = host.GetStatus();
        var named = machines.Count == 0 || machines.Contains(LocalMachine, StringComparer.Ordinal);
        return new AltaHubModel(details, [.. quants.Select(quant => new AltaHubQuantization(quant.Tag, quant.Bits, quant.ModelBytes, quant.MmprojBytes, quant.Files.Count,
            named ? [new AltaHubFit(LocalMachine, FitEstimate.For(quant, status.Hardware, shape),
                status.Models.Any(model => string.Equals(model.Id, $"{details.Model.Id}:{quant.Tag}", StringComparison.OrdinalIgnoreCase)))] : []))], shape);
    }

    /// <inheritdoc />
    public Task<(string Provider, IReadOnlyDictionary<string, string?> Results)> UseInSessionsAsync(string machine, string server, string name, IReadOnlyList<string> machines,
        CancellationToken cancellationToken)
        => Task.FromResult<(string, IReadOnlyDictionary<string, string?>)>(("llama-" + server,
            machines.Distinct(StringComparer.Ordinal).ToDictionary(static item => item, static string? (_) =>
                "A provider for a model server is added from CodeAlta Desktop, on the computer whose sessions use it (Settings > Fleet > Model servers, or `alta llama server use` there).")));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _watched, null) is { } host)
        {
            host.DownloadEnded -= OnDownloadEnded;
            host.TaskEnded -= OnTaskEnded;
        }
    }

    private ModelHost Host(string machine)
    {
        if (machine != LocalMachine) throw new ModelHostException("not_found", "This machine reaches its own model servers only: `--machine local`.");
        return _host() ?? throw new ModelHostException("unavailable", "This machine runs no model host (it runs offline, or it is starting).");
    }

    private void Watch(ModelHost host)
    {
        if (_deliver is null || Interlocked.CompareExchange(ref _watched, host, null) is not null) return;
        host.DownloadEnded += OnDownloadEnded;
        host.TaskEnded += OnTaskEnded;
    }

    private void OnDownloadEnded(DownloadInfo download)
    {
        if (!_waiting.TryRemove(download.Id, out var session)) return;
        Deliver(session, download.State switch
        {
            "done" => $"The download of the model `{download.Model}` to {_name} ended: it is on the disk ({(download.Total / 1e9).ToString("0.0", CultureInfo.InvariantCulture)} GB). "
                + "A server that waited for it starts now: `alta llama server list` says where each stands.",
            "cancelled" => $"The download of the model `{download.Model}` to {_name} was cancelled.",
            _ => $"The download of the model `{download.Model}` to {_name} failed: {download.Error ?? "no reason given"}. `alta llama download resume {download.Id}` tries again where it stopped.",
        });
    }

    private void OnTaskEnded(HostTask task)
    {
        if (!_waiting.TryRemove(task.Id, out var session)) return;
        Deliver(session, task.State == "done"
            ? $"{task.Title} on {_name} ended: {task.Message ?? "done"}. `alta llama status` says what runs now."
            : $"{task.Title} on {_name} failed: {task.Message ?? "no reason given"}. `alta llama build list` says why a build fits or not.");
    }

    private void Deliver(string session, string prompt)
        => _ = Task.Run(async () =>
        {
            try
            {
                await _deliver!(session, prompt).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException or ArgumentException)
            {
                // The session is gone or the host closes: nobody waits for it.
            }
        });
}
