using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode join</c>.</summary>
internal sealed class NodeJoinOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets the enrollment token an admin made.</summary>
    public string? Token { get; set; }

    /// <summary>Gets or sets the name of the node at the server; null for the name of the machine.</summary>
    public string? Name { get; set; }

    /// <summary>Gets the spaces whose sessions publish to the server, by id or name; the default space when none is given.</summary>
    public List<string> Spaces { get; } = [];

    /// <summary>Gets or sets whether join returns once enrolled, without waiting for the approval.</summary>
    public bool NoWait { get; set; }

    /// <summary>Gets the words: the address of the server.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// <c>altanode join</c>: the node of a profile joins a CodeAlta Server with an enrollment token (<c>doc/fleet/identity.md</c>,
/// enrolling nodes). It registers the node's device key, records the link in <c>fleet/servers.json</c>, links the spaces
/// whose sessions it publishes, and waits for a controller device to approve it; the running node keeps a control
/// connection to the server from then on. Like <c>pair</c>, it runs beside the node, as the owner of the profile.
/// </summary>
internal sealed class NodeServerCommands(TextWriter output, TextWriter error)
{
    private static readonly TimeSpan ApprovalPoll = TimeSpan.FromSeconds(3);

    /// <summary>Gets or sets what stops the wait for the approval, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Gets or sets the HTTP handler of the calls to the server, for tests; null for the usual one.</summary>
    public HttpMessageHandler? Handler { get; init; }

    public async ValueTask<int> JoinAsync(NodeJoinOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words.Count != 1 || !FleetServerAddress.TryParse(options.Words[0], out var address, out var certificatePin))
        {
            return await UsageAsync("join needs the address of the server, http:// or https:// (with the #pin=sha256:... of a server with its own certificate): " +
                "altanode join https://fleet.example --token <token>.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(options.Token)) return await UsageAsync("join needs the enrollment --token an admin of the server made.").ConfigureAwait(false);
        string name;
        try { name = TrustedDeviceStore.NormalizeName(options.Name ?? Environment.MachineName); }
        catch (ArgumentException exception) { return await UsageAsync("--name: " + exception.Message).ConfigureAwait(false); }

        var catalog = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot };
        var spaces = new SpaceCatalog(new ProjectCatalog(catalog));
        var linkedSpaces = await ResolveSpacesAsync(spaces, options.Spaces).ConfigureAwait(false);
        if (linkedSpaces is null) return NodeExitCodes.InvalidUsage;

        var store = new FleetServerLinkStore(catalog.FleetRoot);
        var url = FleetServerAddress.Format(address);
        using var identity = DeviceIdentity.LoadOrCreate(catalog);
        if (store.Find(url) is { State: not FleetServerLinkStates.Revoked } existing && existing.DeviceId == identity.DeviceId)
        {
            await output.WriteLineAsync($"This node is linked to {url} already ({existing.State}).").ConfigureAwait(false);
            return existing.State == FleetServerLinkStates.Approved || options.NoWait
                ? NodeExitCodes.Success
                : await WaitAsync(store, existing, address, identity).ConfigureAwait(false);
        }

        using var client = new FleetServerClient(address, identity, Handler, null, certificatePin);
        Fleet.Protocol.Api.DeviceDto device;
        try
        {
            device = await client.EnrollAsync(options.Token, name, Stop).ConfigureAwait(false);
        }
        catch (FleetServerException exception)
        {
            await error.WriteLineAsync($"altanode join: the server refused the enrollment ({exception.Code}): {exception.Message}").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
        catch (HttpRequestException exception)
        {
            await error.WriteLineAsync($"altanode join: {url} does not answer: {exception.Message}").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var now = DateTimeOffset.UtcNow;
        var link = new FleetServerLink(store.Find(url)?.Id ?? FleetServerLinkStore.NewId(), url, null, null, identity.DeviceId, FleetDeviceKinds.Node,
            FleetServerLinkStates.Pending, now, now, certificatePin);
        store.Save(link);
        foreach (var space in linkedSpaces)
        {
            var change = await spaces.UpdateSettingsAsync(space.Id, settings => settings with { Fleet = new SpaceFleet(url) }).ConfigureAwait(false);
            if (!change.Succeeded) await error.WriteLineAsync($"altanode join: the space {space.Name} could not be linked: {change.Message}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"{name} ({device.Id}) is enrolled in {url} and waits for its approval.").ConfigureAwait(false);
        await output.WriteLineAsync($"Approve it from CodeAlta Desktop (Settings > Fleet > Server); its key: {identity.Fingerprint.ToShortWords()}").ConfigureAwait(false);
        await output.WriteLineAsync($"Published: the facts of the sessions of {string.Join(", ", linkedSpaces.Select(static space => space.Name))}, never their content.")
            .ConfigureAwait(false);
        return options.NoWait ? NodeExitCodes.Success : await WaitAsync(store, link, address, identity).ConfigureAwait(false);
    }

    /// <summary>Writes the servers a profile is linked to and how each stands, for <c>altanode status</c>.</summary>
    /// <param name="catalog">The catalog of the profile.</param>
    /// <param name="held">Whether an instance holds the profile now (its connectors say how they stand).</param>
    /// <returns>A task that completes once written.</returns>
    public async Task WriteStatusAsync(CatalogOptions catalog, bool held)
    {
        IReadOnlyList<FleetServerLink> links;
        var store = new FleetServerLinkStore(catalog.FleetRoot);
        try { links = store.List(); }
        catch (InvalidDataException exception)
        {
            await output.WriteLineAsync($"Servers: unreadable ({exception.Message}).").ConfigureAwait(false);
            return;
        }

        if (links.Count == 0)
        {
            await output.WriteLineAsync("Servers: none (altanode join links the node to a CodeAlta Server).").ConfigureAwait(false);
            return;
        }

        foreach (var link in links)
        {
            var state = held ? FleetServerConnections.ReadRuntimeState(store, link.Id) : null;
            var connection = state is null ? "" : $", {Describe(state)}";
            var organization = link.OrganizationName ?? link.OrganizationId;
            await output.WriteLineAsync($"Server: {link.Url}{(organization is null ? "" : $" ({organization})")}, {link.State}{connection}.").ConfigureAwait(false);
        }
    }

    private static string Describe(FleetServerRuntimeState state) => state.State switch
    {
        nameof(FleetConnectorState.Online) => $"connected since {state.Since.ToLocalTime():g}",
        nameof(FleetConnectorState.PendingApproval) => "waiting for its approval",
        nameof(FleetConnectorState.Revoked) => "revoked",
        nameof(FleetConnectorState.Unauthenticated) => "not known to the server",
        _ => $"not connected{(state.Message is { Length: > 0 } message ? $" ({message})" : "")}",
    };

    // Waits until a controller device approves the node: a pending device is refused with pending_approval.
    private async Task<int> WaitAsync(FleetServerLinkStore store, FleetServerLink link, Uri address, DeviceIdentity identity)
    {
        await output.WriteLineAsync("Waiting for the approval... (Ctrl+C stops waiting; the node keeps asking while it runs)").ConfigureAwait(false);
        using var client = new FleetServerClient(address, identity, Handler, null, link.CertificatePin);
        while (true)
        {
            try
            {
                var device = await client.GetDeviceAsync(null, Stop).ConfigureAwait(false);
                var organization = await client.GetOrganizationAsync(Stop).ConfigureAwait(false);
                store.Update(link.Id, current => current with
                {
                    State = device.Status == "revoked" ? FleetServerLinkStates.Revoked : FleetServerLinkStates.Approved,
                    OrganizationId = organization.Id, OrganizationName = organization.Name, UpdatedAt = DateTimeOffset.UtcNow,
                });
                await output.WriteLineAsync($"Approved: the node is a machine of {organization.Name}. The running node connects to the server; altanode status shows it.")
                    .ConfigureAwait(false);
                return NodeExitCodes.Success;
            }
            catch (FleetServerException exception) when (exception.Code == FleetErrorCodes.PendingApproval)
            {
                // Not yet.
            }
            catch (FleetServerException exception) when (exception.Code == FleetErrorCodes.Revoked)
            {
                store.Update(link.Id, current => current with { State = FleetServerLinkStates.Revoked, UpdatedAt = DateTimeOffset.UtcNow });
                await error.WriteLineAsync("altanode join: the server refused the node: it was revoked.").ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }
            catch (HttpRequestException)
            {
                // The server does not answer for a moment: asked again.
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested)
            {
                return NodeExitCodes.Success;
            }

            try { await Task.Delay(ApprovalPoll, Stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return NodeExitCodes.Success; }
        }
    }

    private async Task<IReadOnlyList<SpaceDescriptor>?> ResolveSpacesAsync(SpaceCatalog catalog, IReadOnlyList<string> names)
    {
        var spaces = await catalog.LoadAsync(Stop).ConfigureAwait(false);
        if (names.Count == 0) return [spaces.First(static space => space.IsDefault)];
        var resolved = new List<SpaceDescriptor>();
        foreach (var name in names)
        {
            var space = spaces.FirstOrDefault(candidate => string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase))
                ?? spaces.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (space is null)
            {
                await UsageAsync($"--space: no space is '{name}' ({string.Join(", ", spaces.Select(static candidate => candidate.Id))}).").ConfigureAwait(false);
                return null;
            }

            if (!resolved.Contains(space)) resolved.Add(space);
        }

        return resolved;
    }

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
