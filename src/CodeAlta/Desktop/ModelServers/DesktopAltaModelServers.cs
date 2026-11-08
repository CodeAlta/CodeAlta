using System.Collections.Concurrent;
using System.Globalization;
using CodeAlta.Fleet.Client;
using CodeAlta.LiveTool;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.ModelServers;

/// <summary>
/// The model servers of the window for the <c>alta llama</c> commands: those of this computer and of the paired machines.
/// A download, an install or an update a session starts tells that session its end with a prompt, as a background job does.
/// </summary>
internal sealed class DesktopAltaModelServers : IAltaModelServers, IDisposable
{
    private readonly DesktopModelServers _servers;
    private readonly Func<string, string, Task<string>> _deliver;
    // What a session started and waits for: the machine and the id of a download or a task, and the session.
    private readonly ConcurrentDictionary<(string Machine, string Id), string> _waiting = new();

    /// <summary>Creates the commands' view of the model servers.</summary>
    /// <param name="servers">The model servers of the window.</param>
    /// <param name="deliver">Gives a session a prompt (its id, the prompt), and says what became of it.</param>
    public DesktopAltaModelServers(DesktopModelServers servers, Func<string, string, Task<string>> deliver)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(deliver);
        _servers = servers;
        _deliver = deliver;
        _servers.DownloadEnded += OnDownloadEnded;
        _servers.TaskEnded += OnTaskEnded;
    }

    /// <summary>Creates the view of a runtime: the prompts are queued as those of background jobs.</summary>
    /// <param name="servers">The model servers.</param>
    /// <param name="runtime">The runtime of the sessions.</param>
    /// <returns>The view.</returns>
    public static DesktopAltaModelServers Of(DesktopModelServers servers, SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new DesktopAltaModelServers(servers, (session, prompt) => runtime.DeliverHostPromptAsync(session, prompt, SessionRuntimeService.JobPromptKind));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AltaModelServersMachine>> ListMachinesAsync(CancellationToken cancellationToken)
    {
        await _servers.WatchMachinesAsync(cancellationToken).ConfigureAwait(false);
        return [.. _servers.Machines().Select(static machine => new AltaModelServersMachine(machine.Id, machine.Name, machine.Local, machine.State, machine.Manages, machine.Uses, machine.Problem))];
    }

    /// <inheritdoc />
    public Task<ModelHostStatus> StatusAsync(string machine, CancellationToken cancellationToken) => _servers.StatusAsync(machine, cancellationToken);

    /// <inheritdoc />
    public async Task<ModelHostActionResult> RunAsync(string machine, ModelHostAction action, string? sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var result = await _servers.DoAsync(machine, action, sessionId is null ? "an agent" : "session " + sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionId is not null)
        {
            if (result.Download is { } download && download.State is "queued" or "downloading" or "checking") _waiting[(machine, download.Id)] = sessionId;
            if (result.Task is { } task && task.State is "running" or "waiting") _waiting[(machine, task.Id)] = sessionId;
            // A server of models not on the disk downloads them: each tells the session its end.
            if (result.Server is { } server && action.Action == "setServer")
            {
                var status = await _servers.StatusAsync(machine, cancellationToken).ConfigureAwait(false);
                foreach (var started in status.Downloads.Where(item => item.State is "queued" or "downloading" or "checking"
                    && server.Definition.Models.Any(model => string.Equals(model.Model, item.Model, StringComparison.OrdinalIgnoreCase))))
                    _waiting.TryAdd((machine, started.Id), sessionId);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HubModel>> SearchHubAsync(string? search, string? author, string? pipeline, string sort, int limit, CancellationToken cancellationToken)
        => (_servers.Local ?? throw new ModelHostException("unavailable", "This instance runs no model host: Hugging Face is read through it."))
            .Hub.SearchAsync(search, author, pipeline, sort, limit, cancellationToken);

    /// <inheritdoc />
    public Task<AltaHubModel> ReadHubModelAsync(string repo, IReadOnlyList<string> machines, CancellationToken cancellationToken)
        => _servers.ReadHubModelAsync(repo, machines, cancellationToken);

    /// <inheritdoc />
    public async Task<(string Provider, IReadOnlyDictionary<string, string?> Results)> UseInSessionsAsync(string machine, string server, string name, IReadOnlyList<string> machines,
        CancellationToken cancellationToken)
    {
        var results = await _servers.UseInSessionsAsync(machine, server, name, machines, cancellationToken).ConfigureAwait(false);
        return (_servers.ProviderKey(machine, server), results);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _servers.DownloadEnded -= OnDownloadEnded;
        _servers.TaskEnded -= OnTaskEnded;
    }

    private void OnDownloadEnded(string machine, DownloadInfo download)
    {
        if (!_waiting.TryRemove((machine, download.Id), out var session)) return;
        var where = NameOf(machine);
        var prompt = download.State switch
        {
            "done" => $"The download of the model `{download.Model}` to {where} ended: it is on the disk ({Size(download.Total)}). " +
                "A server that waited for it starts now: `alta llama server list" + MachineOption(machine) + "` says where each stands.",
            "cancelled" => $"The download of the model `{download.Model}` to {where} was cancelled.",
            _ => $"The download of the model `{download.Model}` to {where} failed: {download.Error ?? "no reason given"}." +
                " `alta llama download resume " + download.Id + MachineOption(machine) + "` tries again where it stopped.",
        };
        _ = DeliverAsync(session, prompt);
    }

    private void OnTaskEnded(string machine, HostTask task)
    {
        if (!_waiting.TryRemove((machine, task.Id), out var session)) return;
        var where = NameOf(machine);
        var prompt = task.State == "done"
            ? $"{task.Title} on {where} ended: {task.Message ?? "done"}. `alta llama status{MachineOption(machine)}` says what runs now."
            : $"{task.Title} on {where} failed: {task.Message ?? "no reason given"}. `alta llama build list{MachineOption(machine)}` says why a build fits or not.";
        _ = DeliverAsync(session, prompt);
    }

    private async Task DeliverAsync(string session, string prompt)
    {
        try
        {
            await _deliver(session, prompt).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException or ArgumentException)
        {
            // The session is gone or the window closes: nobody waits for it.
        }
    }

    private string NameOf(string machine)
        => machine == FleetModelServerTransports.LocalMachine ? "this computer" : _servers.Machines().FirstOrDefault(item => item.Id == machine)?.Name ?? machine;

    private string MachineOption(string machine)
        => machine == FleetModelServerTransports.LocalMachine ? "" : $" --machine \"{NameOf(machine)}\"";

    private static string Size(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
