using System.Globalization;
using System.Text.Json;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Servers;

namespace CodeAlta.ModelServers;

public sealed partial class ModelHost
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private string PlannedPath => Path.Combine(_options.SharedRoot, "update.json");

    private string HistoryPath => Path.Combine(_options.SharedRoot, "updates.json");

    private string LastScheduledPath => Path.Combine(_options.SharedRoot, "update-last.txt");

    /// <summary>Plans one update at a time; the machine keeps it and does it by itself, across its restarts.</summary>
    /// <param name="at">When.</param>
    /// <param name="build">The build, or null for the newest of the channel.</param>
    /// <param name="models">Whether the models are updated too.</param>
    /// <param name="requestedBy">Who asks.</param>
    /// <returns>The planned update.</returns>
    public PlannedUpdate PlanUpdate(DateTimeOffset at, string? build, bool models, string requestedBy)
    {
        var planned = new PlannedUpdate("upd-" + Guid.NewGuid().ToString("N")[..8], at.ToUniversalTime(), build, models, requestedBy);
        Directory.CreateDirectory(_options.SharedRoot);
        SafeFile.WriteAllText(PlannedPath, JsonSerializer.Serialize(planned, ModelServersJsonContext.Default.PlannedUpdate));
        NotifyChanged();
        return planned;
    }

    /// <summary>Cancels the update planned once.</summary>
    /// <returns>Whether there was one.</returns>
    public bool CancelPlannedUpdate()
    {
        if (!File.Exists(PlannedPath)) return false;
        File.Delete(PlannedPath);
        NotifyChanged();
        return true;
    }

    /// <summary>Updates llama.cpp now: each backend installed goes to the build asked for (the newest of the channel by default), once its servers are idle; each is tested after the switch and goes back to its previous build when the test fails.</summary>
    /// <param name="build">The build, or null for the newest of the channel. An older build than the one installed takes the machine back to it.</param>
    /// <param name="models">Whether the models whose files changed in their repository are updated too.</param>
    /// <param name="requestedBy">Who asks.</param>
    /// <returns>The task of the update, which runs on.</returns>
    public HostTask UpdateNow(string? build, bool models, string? requestedBy)
    {
        var task = AddTask("update", build is null ? "Update llama.cpp" : $"Switch llama.cpp to {build}", requestedBy);
        _ = Task.Run(() => RunUpdateAsync(task.Id, build, models, _disposed.Token));
        return task;
    }

    private async Task RunScheduledUpdatesAsync(CancellationToken cancellationToken)
    {
        var now = _options.Time.GetUtcNow();
        if (ReadPlanned() is { } planned && planned.At <= now)
        {
            CancelPlannedUpdate();
            var task = AddTask("update", planned.Build is null ? "Planned update of llama.cpp" : $"Planned switch to {planned.Build}", planned.RequestedBy);
            await RunUpdateAsync(task.Id, planned.Build, planned.Models, cancellationToken).ConfigureAwait(false);
            return;
        }

        var policy = Settings.Updates;
        if (policy.Schedule == "manual") return;
        var local = TimeZoneInfo.ConvertTime(now, _options.Time.LocalTimeZone);
        var time = TimeOnly.ParseExact(policy.Time, "HH:mm", CultureInfo.InvariantCulture);
        if (TimeOnly.FromDateTime(local.DateTime) < time) return;
        if (policy.Schedule == "weekly" && local.DayOfWeek != Enum.Parse<DayOfWeek>(policy.Day, ignoreCase: true)) return;
        var today = DateOnly.FromDateTime(local.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = File.Exists(LastScheduledPath) ? File.ReadAllText(LastScheduledPath).Trim() : "";
        if (last == today) return;
        Directory.CreateDirectory(_options.SharedRoot);
        SafeFile.WriteAllText(LastScheduledPath, today);
        var scheduled = AddTask("update", "Scheduled update of llama.cpp", "policy");
        await RunUpdateAsync(scheduled.Id, null, policy.UpdateModels, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunUpdateAsync(string taskId, string? build, bool models, CancellationToken cancellationToken)
    {
        if (!await _updateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            EndTask(taskId, "failed", "Another update runs on this machine now.");
            return;
        }

        try
        {
            var release = await ResolveChannelAsync(Settings.Updates, build, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (build is null)
                {
                    _latest = release;
                    _latestAt = _options.Time.GetUtcNow();
                }
            }

            HardwareInventory hardware;
            lock (_gate) hardware = _hardware;
            var candidates = BuildSelector.Candidates(hardware, release);
            var backends = Builds.List().Where(static b => b.Current && b.Source != "own").Select(static b => b.Backend).Distinct().ToList();
            if (backends.Count == 0) backends = [.. BuildSelector.Recommended(hardware, candidates)];
            var messages = new List<string>();
            var failed = false;
            foreach (var backend in backends)
            {
                var current = Builds.Find(backend, null);
                if (current?.Tag == release.Tag)
                {
                    messages.Add($"{backend}: already on {release.Tag}.");
                    continue;
                }

                if (build is null && Builds.IsBad(backend, release.Tag))
                {
                    messages.Add($"{backend}: {release.Tag} failed its test here before; it stays on {current?.Tag}.");
                    continue;
                }

                var assets = BuildSelector.AssetsFor(release, hardware.Os, hardware.Architecture, backend, candidates.FirstOrDefault(c => c.Backend == backend));
                UpdateTask(taskId, t => t with { State = "running", Total = t.Total + assets.Sum(static a => a.Size), Message = $"Downloading {release.Tag} ({backend})…" });
                InstalledBuild installed;
                try
                {
                    installed = await Builds.InstallAsync(release, backend, assets, makeCurrent: false, bytes => UpdateTask(taskId, t => t with { Done = Math.Max(0, t.Done + bytes) }), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ModelHostException exception)
                {
                    failed = true;
                    Record(new UpdateRecord(_options.Time.GetUtcNow(), backend, current?.Tag, release.Tag, "failed", exception.Message));
                    messages.Add($"{backend}: {exception.Message}");
                    continue;
                }

                var servers = Supervisors().Where(s => s.Definition.Backend == backend && s.Definition.Build is null && s.Status.State != "stopped").ToList();
                UpdateTask(taskId, t => t with { State = "waiting", Message = $"Waiting until the {backend} servers are idle…" });
                if (!await WaitIdleAsync(servers, cancellationToken).ConfigureAwait(false))
                {
                    messages.Add($"{backend}: its servers stayed busy for an hour; {release.Tag} is installed and will be used at the next update.");
                    continue;
                }

                UpdateTask(taskId, t => t with { State = "running", Message = $"Switching {backend} to {release.Tag}…" });
                var previousSpeed = servers.SelectMany(static s => s.Status.Models).Select(static m => m.TokensPerSecond).OfType<double>().DefaultIfEmpty().Max();
                Builds.MakeCurrent(backend, release.Tag);
                var (passed, reason) = await SwitchAndTestAsync(servers, previousSpeed, cancellationToken).ConfigureAwait(false);
                if (passed)
                {
                    Record(new UpdateRecord(_options.Time.GetUtcNow(), backend, current?.Tag, release.Tag, "updated", reason));
                    messages.Add($"{backend}: {current?.Tag ?? "none"} → {release.Tag}.");
                    var inUse = Supervisors().Select(static s => s.BuildFolder).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Builds.Prune(inUse);
                }
                else
                {
                    failed = true;
                    Builds.MarkBad(backend, release.Tag);
                    if (current is not null)
                    {
                        Builds.MakeCurrent(backend, current.Tag);
                        foreach (var server in servers) await server.RestartAsync(cancellationToken).ConfigureAwait(false);
                    }

                    Record(new UpdateRecord(_options.Time.GetUtcNow(), backend, current?.Tag, release.Tag, "rolledBack", reason));
                    messages.Add($"{backend}: {release.Tag} failed its test ({reason}); back on {current?.Tag}.");
                }
            }

            if (models) messages.Add(await UpdateModelsAsync(cancellationToken).ConfigureAwait(false));
            EndTask(taskId, failed ? "failed" : "done", string.Join(" ", messages));
        }
        catch (Exception exception) when (exception is ModelHostException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            EndTask(taskId, "failed", exception.Message);
        }
        catch (OperationCanceledException)
        {
            EndTask(taskId, "failed", "Stopped.");
        }
        finally
        {
            _updateGate.Release();
            NotifyChanged();
        }
    }

    // Each server goes to the new build; its first model loads and gives 8 tokens, at least half as fast as before.
    private async Task<(bool Passed, string? Reason)> SwitchAndTestAsync(IReadOnlyList<ServerSupervisor> servers, double previousSpeed, CancellationToken cancellationToken)
    {
        foreach (var server in servers)
        {
            await server.RestartAsync(cancellationToken).ConfigureAwait(false);
            if (!await server.WaitReadyAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false))
                return (false, server.Status.Reason ?? $"{server.Definition.Name} did not start.");
            var model = server.Definition.Models.FirstOrDefault(m => Store.Find(m.Model) is not null)?.Model;
            if (model is null || server.Client is not { } client) continue;
            var result = await client.TestAsync(model, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            if (!result.Passed) return (false, result.Message);
            if (server.ReadLog(400).Any(static line => line.Contains("offloaded 0/", StringComparison.Ordinal)))
                return (false, "The model ran on the CPU: the GPU was not used.");
            if (previousSpeed > 0 && result.TokensPerSecond is { } speed && speed < previousSpeed / 2)
                return (false, string.Create(CultureInfo.InvariantCulture, $"{speed:0} tokens/s, less than half of the {previousSpeed:0} before."));
        }

        return (true, servers.Count == 0 ? "No server ran on this backend." : null);
    }

    private async Task<bool> WaitIdleAsync(IReadOnlyList<ServerSupervisor> servers, CancellationToken cancellationToken)
    {
        var end = _options.Time.GetUtcNow() + IdleWait;
        while (_options.Time.GetUtcNow() < end)
        {
            if (servers.All(static s => s.Status.Models.All(static m => m.BusySlots == 0 && m.State != "loading"))) return true;
            await Task.Delay(TimeSpan.FromSeconds(15), _options.Time, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<string> UpdateModelsAsync(CancellationToken cancellationToken)
    {
        var updated = new List<string>();
        foreach (var model in Store.Scan())
        {
            try
            {
                var details = await _hub.GetModelAsync(model.Repo, cancellationToken).ConfigureAwait(false);
                if (details.Revision == model.Revision) continue;
                var quant = CodeAlta.ModelServers.Store.GgufNames.Find(details.Files, model.Tag);
                // A new commit that changed nothing of this model (a README) does not download it again.
                if (quant is null || quant.Files.All(file => model.Files.Any(mine => mine.Path == file.Path && mine.Size == file.Size))) continue;
                Download(model.Id, "update");
                updated.Add(model.Id);
            }
            catch (ModelHostException)
            {
            }
        }

        return updated.Count == 0 ? "The models are up to date." : $"Downloading the new revision of {string.Join(", ", updated)}.";
    }

    /// <summary>Lists the models whose files changed in their repository since they were downloaded.</summary>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The ids of the models with an update.</returns>
    public async Task<IReadOnlyList<string>> CheckModelUpdatesAsync(CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var model in Store.Scan())
        {
            try
            {
                var details = await _hub.GetModelAsync(model.Repo, cancellationToken).ConfigureAwait(false);
                if (details.Revision == model.Revision) continue;
                var quant = CodeAlta.ModelServers.Store.GgufNames.Find(details.Files, model.Tag);
                if (quant is not null && quant.Files.Any(file => !model.Files.Any(mine => mine.Path == file.Path && mine.Size == file.Size))) result.Add(model.Id);
            }
            catch (ModelHostException)
            {
            }
        }

        return result;
    }

    private PlannedUpdate? ReadPlanned()
    {
        try
        {
            return File.Exists(PlannedPath) ? JsonSerializer.Deserialize(File.ReadAllText(PlannedPath), ModelServersJsonContext.Default.PlannedUpdate) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<UpdateRecord> ReadHistory()
    {
        try
        {
            return File.Exists(HistoryPath) ? JsonSerializer.Deserialize(File.ReadAllText(HistoryPath), ModelServersJsonContext.Default.ListUpdateRecord) ?? [] : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Record(UpdateRecord record)
    {
        var history = ReadHistory();
        history.Insert(0, record);
        if (history.Count > 50) history.RemoveRange(50, history.Count - 50);
        Directory.CreateDirectory(_options.SharedRoot);
        SafeFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, ModelServersJsonContext.Default.ListUpdateRecord));
    }
}
