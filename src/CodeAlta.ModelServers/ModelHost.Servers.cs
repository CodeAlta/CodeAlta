using System.Globalization;
using System.Text.Json;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Servers;

namespace CodeAlta.ModelServers;

public sealed partial class ModelHost
{
    private string ServersRoot => Path.Combine(_options.InstanceRoot, "servers");

    /// <summary>Creates or changes a server; a running one restarts to take the change, a new one starts when it is enabled.</summary>
    /// <param name="definition">What the person set; an empty id or port is chosen.</param>
    /// <param name="cancellationToken">Ends the start.</param>
    /// <returns>The server as saved.</returns>
    /// <exception cref="ModelHostException">A setting is refused, or the port is taken by another server.</exception>
    public async Task<ServerView> SetServerAsync(ServerDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var existing = definition.Id.Length > 0 ? Find(definition.Id) : null;
        if (definition.Id.Length == 0) definition = definition with { Id = NewId(definition.Name) };
        if (definition.Port == 0) definition = definition with { Port = existing?.Definition.Port ?? FreePort() };
        if (definition.Models.Count == 0) throw new ModelHostException("invalid", "A server serves at least one model.");
        if (Supervisors().FirstOrDefault(other => other.Definition.Id != definition.Id && other.Definition.Port == definition.Port) is { } clash)
            throw new ModelHostException("port_taken", $"{clash.Definition.Name} uses port {definition.Port}.");
        definition = definition with { Models = definition.Models.Select(static model => model with { Model = NormalizeModelId(model.Model) }).ToList() };
        ServerArguments.Validate(definition);
        var folder = Path.Combine(ServersRoot, definition.Id);
        Directory.CreateDirectory(folder);
        SafeFile.WriteAllText(Path.Combine(folder, "server.json"), JsonSerializer.Serialize(definition, ModelServersJsonContext.Default.ServerDefinition));
        if (existing is null)
        {
            var supervisor = AddSupervisor(definition);
            if (definition.Enabled) await TryStartAsync(supervisor, cancellationToken).ConfigureAwait(false);
            // A build of its backend that is not installed yet is installed; the server starts once it is.
            if (definition.Enabled && Builds.Find(definition.Backend, definition.Build) is null) InstallBuilds([definition.Backend], definition.Build, "server " + definition.Id);
            // A model of the server that is not on disk yet is downloaded.
            foreach (var model in definition.Models.Where(model => Store.Find(model.Model) is null)) Download(model.Model, "server " + definition.Id);
        }
        else
        {
            var wasRunning = existing.Status.State != "stopped";
            // UpdateAsync stops a server turned off and restarts one that stays on.
            await existing.UpdateAsync(definition, cancellationToken).ConfigureAwait(false);
            if (!wasRunning && definition.Enabled) await TryStartAsync(existing, cancellationToken).ConfigureAwait(false);
            if (definition.Enabled && Builds.Find(definition.Backend, definition.Build) is null) InstallBuilds([definition.Backend], definition.Build, "server " + definition.Id);
            foreach (var model in definition.Models.Where(model => Store.Find(model.Model) is null)) Download(model.Model, "server " + definition.Id);
        }

        NotifyChanged();
        var saved = Find(definition.Id)!;
        return new ServerView(saved.Definition, saved.Status);
    }

    /// <summary>Removes a server: it stops, and its folder goes. Its models stay on disk.</summary>
    /// <param name="id">The server.</param>
    /// <param name="cancellationToken">Ends the stop.</param>
    /// <returns>A task that ends once it is removed.</returns>
    /// <exception cref="ModelHostException">It is not found.</exception>
    public async Task RemoveServerAsync(string id, CancellationToken cancellationToken)
    {
        var supervisor = Find(id) ?? throw new ModelHostException("not_found", $"There is no server {id}.");
        await supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        await supervisor.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _servers.Remove(id);
        }

        try { Directory.Delete(Path.Combine(ServersRoot, id), recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        NotifyChanged();
    }

    /// <summary>Starts, stops or restarts a server, or loads or unloads one of its models.</summary>
    /// <param name="id">The server.</param>
    /// <param name="action"><c>start</c>, <c>stop</c>, <c>restart</c>, <c>load</c> or <c>unload</c>.</param>
    /// <param name="model">The model, for <c>load</c> and <c>unload</c>.</param>
    /// <param name="cancellationToken">Ends the action.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ModelHostException">It is not found, or the action fails.</exception>
    public async Task<ServerView> ControlServerAsync(string id, string action, string? model, CancellationToken cancellationToken)
    {
        var supervisor = Find(id) ?? throw new ModelHostException("not_found", $"There is no server {id}.");
        switch (action)
        {
            case "start":
                await SetEnabledAsync(supervisor, true).ConfigureAwait(false);
                await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "stop":
                await SetEnabledAsync(supervisor, false).ConfigureAwait(false);
                await supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "restart":
                await supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "load" or "unload":
                if (model is null || supervisor.Client is not { } client) throw new ModelHostException("not_running", $"{supervisor.Definition.Name} does not run.");
                if (supervisor.Definition.Kind != ServerKinds.Several) throw new ModelHostException("invalid", "A one-model server keeps its model loaded.");
                var named = supervisor.RouterName(model);
                if (!(action == "load" ? await client.LoadAsync(named, cancellationToken).ConfigureAwait(false) : await client.UnloadAsync(named, cancellationToken).ConfigureAwait(false)))
                    throw new ModelHostException("refused", $"The server did not {action} {model}.");
                break;
            default:
                throw new ModelHostException("invalid", $"'{action}' is not an action of a server.");
        }

        NotifyChanged();
        return new ServerView(supervisor.Definition, supervisor.Status);
    }

    /// <summary>Tests a server: its first model (or the one named) loads and gives 8 tokens.</summary>
    /// <param name="id">The server.</param>
    /// <param name="model">The model, or null for its first.</param>
    /// <param name="cancellationToken">Ends the test.</param>
    /// <returns>What the test gave.</returns>
    /// <exception cref="ModelHostException">It is not found or does not run.</exception>
    public async Task<ServerTestResult> TestServerAsync(string id, string? model, CancellationToken cancellationToken)
    {
        var supervisor = Find(id) ?? throw new ModelHostException("not_found", $"There is no server {id}.");
        if (!await supervisor.WaitReadyAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false) || supervisor.Client is not { } client)
            return new ServerTestResult(false, null, null, supervisor.Status.Reason ?? $"{supervisor.Definition.Name} does not run.");
        var target = model ?? supervisor.Definition.Models.FirstOrDefault(m => Store.Find(m.Model) is not null)?.Model;
        if (target is null) return new ServerTestResult(false, null, null, "None of its models is on this machine yet.");
        return await client.TestAsync(supervisor.RouterName(target), supervisor.Definition.Purpose, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the end of the log of a server.</summary>
    /// <param name="id">The server.</param>
    /// <param name="lines">How many lines.</param>
    /// <returns>The lines.</returns>
    /// <exception cref="ModelHostException">It is not found.</exception>
    public IReadOnlyList<string> ReadServerLog(string id, int lines)
        => (Find(id) ?? throw new ModelHostException("not_found", $"There is no server {id}.")).ReadLog(Math.Clamp(lines, 1, 2000));

    /// <summary>The address and key of a server, for a provider of this machine.</summary>
    /// <param name="id">The server.</param>
    /// <returns>Its address and key, or null when it is not found.</returns>
    public (string Address, string Key)? Endpoint(string id) => Find(id) is { } supervisor ? ($"http://127.0.0.1:{supervisor.Definition.Port}", supervisor.Key) : null;

    /// <summary>Stops every server (a node that stops for good, not one that switches its version).</summary>
    /// <param name="cancellationToken">Ends the stops.</param>
    /// <returns>A task that ends once they stopped.</returns>
    public async Task StopAllServersAsync(CancellationToken cancellationToken)
    {
        foreach (var supervisor in Supervisors().Where(static s => s.Status.State != "stopped"))
        {
            try { await supervisor.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Gives the list of models of a server (`/v1/models`) with the context each model had when it last loaded: a router
    /// lists the context a model was trained for, which `--fit` may have lowered, and a provider sizes its compaction by it.
    /// </summary>
    /// <param name="id">The server.</param>
    /// <param name="json">The list the server gave.</param>
    /// <returns>The list with those contexts, or the list as it was.</returns>
    public byte[] AdjustModelList(string id, byte[] json)
    {
        if (Find(id) is not { } supervisor) return json;
        // By the name the router lists each model with.
        var contexts = supervisor.Status.Models.Where(static m => m.Context is > 0).ToDictionary(m => supervisor.RouterName(m.Model), static m => m.Context!.Value, StringComparer.Ordinal);
        var names = supervisor.Definition.Models.ToDictionary(m => supervisor.RouterName(m.Model), static m => CodeAlta.ModelServers.Store.GgufNames.DisplayName(m.Model), StringComparer.Ordinal);
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (root?["data"] is not System.Text.Json.Nodes.JsonArray data) return json;
            foreach (var entry in data.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (entry["id"]?.GetValue<string>() is not { } model) continue;
                if (contexts.TryGetValue(model, out var context) && entry["meta"] is null) entry["context_length"] = context;
                // The name people read in the composer: a server of one model lists the path of its file as its id.
                var known = names.TryGetValue(model, out var name) ? name : supervisor.Definition.Models.Count == 1 ? names.Values.First() : null;
                if (known is not null && entry["name"] is null) entry["name"] = known;
            }

            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return json;
        }
    }

    /// <summary>The servers.</summary>
    /// <returns>Each server and where it stands.</returns>
    public IReadOnlyList<ServerView> ListServers() => Supervisors().Select(View).ToList();

    /// <summary>
    /// Gets the slots of the models of the servers, which the requests of inference of this machine and of the others take
    /// before they reach a server (<see cref="ModelSlotGates"/>).
    /// </summary>
    public ModelSlotGates Slots => LazyInitializer.EnsureInitialized(ref _slots, () => new ModelSlotGates(NotifyChanged));

    private ModelSlotGates? _slots;

    /// <summary>
    /// Gets the gate of the slots of the model a request of inference names on a server: sized by the slots the server reports
    /// for it, else by the slots its settings give it; one that names no model is for the one model of a server.
    /// </summary>
    /// <param name="id">The server.</param>
    /// <param name="model">The model the request names (its <c>model</c>), or null.</param>
    /// <returns>The gate and the model it is for, or null for a server that is not on this machine.</returns>
    public (ModelSlotGate Gate, string Model)? SlotsOf(string id, string? model)
    {
        if (Find(id) is not { } supervisor) return null;
        var definition = supervisor.Definition;
        // The model as the server knows it: by the name the router lists it with, or by its own; the one model of a server else.
        var named = model is { Length: > 0 } ? definition.Models.FirstOrDefault(m => m.Model == model || supervisor.RouterName(m.Model) == model) : null;
        var served = named ?? (definition.Models.Count == 1 ? definition.Models[0] : null);
        var key = served?.Model ?? model ?? string.Empty;
        var status = served is null ? null : supervisor.Status.Models.FirstOrDefault(m => m.Model == served.Model);
        var slots = status is { TotalSlots: > 0 } ? status.TotalSlots : served?.Parallel;
        return (Slots.For(id, key, slots), key);
    }

    // A server as it is listed: its models with the requests that wait for their slots here.
    private ServerView View(ServerSupervisor supervisor)
    {
        var status = supervisor.Status;
        var id = supervisor.Definition.Id;
        var waiting = status.Models.Select(model => model with { Waiting = Slots.Waiting(id, model.Model) }).ToList();
        return new ServerView(supervisor.Definition, status with { Models = waiting });
    }

    /// <summary>A server for a command line of llama-server (a <c>run_*.sh</c> script), not saved yet.</summary>
    /// <param name="text">The command or the script.</param>
    /// <param name="name">The name of the server, or null.</param>
    /// <returns>The server it gives, and what was not taken.</returns>
    public (ServerDefinition? Definition, IReadOnlyList<string> Ignored) Import(string text, string? name)
    {
        var imported = ServerArguments.Import(text);
        if (imported.Settings is null) return (null, imported.Ignored);
        HardwareInventory hardware;
        lock (_gate) hardware = _hardware;
        var backend = ChooseBackend(hardware, imported.Devices);
        var devices = imported.Devices.ToList();
        var ignored = imported.Ignored.ToList();
        if (devices.Count == 0 && imported.VisibleDevices.Count > 0)
        {
            // HIP_VISIBLE_DEVICES=0 names the first GPU of its vendor; the build names it with its own index.
            var build = Builds.Find(backend, null);
            var gpus = build?.Devices.Where(device => LlamaBuilds.DeviceBackend(device.Name) == backend).ToList() ?? [];
            foreach (var index in imported.VisibleDevices)
            {
                if (index < gpus.Count) devices.Add(gpus[index].Name);
                else ignored.Add($"GPU {index.ToString(CultureInfo.InvariantCulture)} of HIP_VISIBLE_DEVICES: choose its device after the import.");
            }
        }

        var model = imported.Settings with { Devices = devices };
        var definition = new ServerDefinition
        {
            Id = "",
            Name = name ?? (imported.Model is { } id ? id.Split('/').Last().Split(':')[0].Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase) : "Imported server"),
            Kind = ServerKinds.Several,
            Backend = backend,
            Port = imported.Port is { } port && Supervisors().All(supervisor => supervisor.Definition.Port != port) ? port : 0,
            Models = [model],
        };
        return (definition, ignored);
    }

    /// <summary>The backend a server takes on this machine: the one of its devices, else the best installed one.</summary>
    /// <param name="hardware">The machine.</param>
    /// <param name="devices">Its devices.</param>
    /// <returns>The backend.</returns>
    public string ChooseBackend(HardwareInventory hardware, IReadOnlyList<string> devices)
    {
        if (devices.Count > 0) return LlamaBuilds.DeviceBackend(devices[0]);
        var installed = Builds.List().Where(static build => build.Current).Select(static build => build.Backend).ToHashSet(StringComparer.Ordinal);
        foreach (var backend in BuildSelector.Recommended(hardware, BuildSelector.Candidates(hardware, null)).Concat(["vulkan", "cpu"]))
        {
            if (installed.Contains(backend)) return backend;
        }

        return BuildSelector.Recommended(hardware, BuildSelector.Candidates(hardware, null))[0];
    }

    private async Task SetEnabledAsync(ServerSupervisor supervisor, bool enabled)
    {
        if (supervisor.Definition.Enabled == enabled) return;
        var definition = supervisor.Definition with { Enabled = enabled };
        SafeFile.WriteAllText(Path.Combine(ServersRoot, definition.Id, "server.json"), JsonSerializer.Serialize(definition, ModelServersJsonContext.Default.ServerDefinition));
        await supervisor.UpdateAsync(definition, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TryStartAsync(ServerSupervisor supervisor, CancellationToken cancellationToken)
    {
        try
        {
            await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelHostException exception) when (exception.Code is "no_build" or "missing_model" or "too_old" or "cannot_start")
        {
            // It starts once what it waits for is there; its page says what.
            supervisor.MarkWaiting(exception.Message);
        }
    }

    private ServerSupervisor AddSupervisor(ServerDefinition definition)
    {
        var supervisor = new ServerSupervisor(Path.Combine(ServersRoot, definition.Id), definition, _local, (backend, tag) => Builds.Find(backend, tag), ModelPaths)
        {
            DefaultDevices = build => { lock (_gate) return LlamaBuilds.DiscreteDevices(build, _hardware); },
        };
        supervisor.Changed += NotifyChanged;
        lock (_gate)
        {
            _servers[definition.Id] = supervisor;
        }

        return supervisor;
    }

    private (string Model, string? Mmproj)? ModelPaths(string model)
    {
        IReadOnlyList<StoredModel> models;
        lock (_gate) models = _models;
        var found = models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase)) ?? Store.Find(model);
        return found?.ModelPath is { } path ? (path, found.MmprojPath) : null;
    }

    private ServerSupervisor? Find(string id)
    {
        lock (_gate) return _servers.GetValueOrDefault(id);
    }

    private List<ServerSupervisor> Supervisors()
    {
        lock (_gate) return [.. _servers.Values];
    }

    private int FreePort()
    {
        var used = Supervisors().Select(static supervisor => supervisor.Definition.Port).ToHashSet();
        var port = _options.FirstPort;
        while (used.Contains(port) || !IsPortFree(port)) port++;
        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private string NewId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "server";
        if (slug.Length > 32) slug = slug[..32].Trim('-');
        var id = slug;
        for (var n = 2; Find(id) is not null; n++) id = $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
        return id;
    }

    private static string NormalizeModelId(string model)
    {
        var (repo, tag) = CodeAlta.ModelServers.Store.ModelStore.ParseId(model);
        return $"{repo}:{tag}";
    }
}
