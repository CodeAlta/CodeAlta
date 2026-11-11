using CodeAlta.Catalog;
using CodeAlta.CodexAppServer;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node;

// The programs the providers of a node run (Claude Code, the Codex app-server, the agents of the ACP registry), installed and updated
// by the node the same way for all of them (#414): the offers a device adds a provider from, an addition that installs the program
// first when it is missing, an update to the version the node offers now, and the pinned versions that follow CodeAlta once the node
// was updated. Claude Code is a type of the node, installed from the version CodeAlta pins; Codex CLI and the ACP agents come from the
// catalogs of their plugins (IPluginProviderCatalog), as the Add provider dialog of the desktop adds them.
internal sealed partial class NodeProviders
{
    private const string ClaudeType = "claude-code";
    private const string CodexCliType = "codex-cli";
    private const int MaxOffers = 300;
    private const int MaxOperations = 16;
    private static readonly TimeSpan CatalogWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UpdateCheckWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationLifetime = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private ConfiguredClaudeCodeCli? _claude;

    /// <summary>Gets the provider types of the running plugins of the node (Codex CLI, the ACP agents); null for none.</summary>
    public PluginModelProviderTypes? PluginProviderTypes { get; init; }

    // The Claude Code CodeAlta pins, in the cache of the node; one per instance, so that one download runs at a time.
    private ConfiguredClaudeCodeCli Claude
    {
        get
        {
            lock (_gate) return _claude ??= new ConfiguredClaudeCodeCli(ClaudeRoot);
        }
    }

    private string ClaudeRoot => new CatalogOptions { GlobalRoot = Root }.ClaudeCodeRoot;

    private string CodexRoot => Path.Combine(Root, "cache", "codex-cli");

    /// <inheritdoc />
    public async Task<FleetProviderOfferList> ListOffersAsync(bool refresh, CancellationToken cancellationToken)
    {
        var store = Store;
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true);
        var external = TryRead(store.LoadGlobalExternalProviders) ?? [];
        string? Configured(string type) => definitions.FirstOrDefault(definition => string.Equals(definition.ProviderType, type, StringComparison.Ordinal))?.ProviderKey
            ?? external.FirstOrDefault(provider => string.Equals(provider.ProviderType, type, StringComparison.OrdinalIgnoreCase))?.ProviderKey;

        var claude = ClaudeProgram(null);
        var offers = new List<FleetProviderOffer>
        {
            new(ClaudeType, ClaudeType, "Claude Code", "agent", "cli")
            {
                Description = "Anthropic's agent, signed in with a Claude account. CodeAlta installs the version it is tested with when the machine has none.",
                Program = claude, ConfiguredKey = Configured(ClaudeType),
                Unavailable = claude.State == "unsupported" ? claude.Detail : null,
            },
        };
        foreach (var type in OfferedTypes.Where(static type => type != ClaudeType))
            offers.Add(new FleetProviderOffer(type, type, TypeName(type), Group(type), Credential(type)) { SignIn = SignsInWithCode(type), ConfiguredKey = Configured(type) });

        string? error = null;
        foreach (var contribution in PluginProviderTypes?.Types ?? [])
        {
            if (contribution.Catalog is not { } catalog) continue;
            PluginProviderCatalogSnapshot snapshot;
            try
            {
                snapshot = await catalog.ListAsync(refresh, cancellationToken).WaitAsync(CatalogWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
            {
                error ??= $"{catalog.Title}: {exception.Message}";
                continue;
            }

            if (snapshot.Error is { } failed) error ??= $"{catalog.Title}: {failed}";
            var installed = InstalledEntries(external, contribution.ProviderType);
            foreach (var entry in snapshot.Entries)
            {
                if (offers.Count >= MaxOffers) break;
                var isCodex = string.Equals(contribution.ProviderType, CodexCliType, StringComparison.OrdinalIgnoreCase);
                offers.Add(new FleetProviderOffer($"{contribution.ProviderType}:{entry.Id}", contribution.ProviderType, Bounded(entry.Name)!, "agent",
                    contribution.Account is not null ? "sign_in" : "cli")
                {
                    Description = Bounded(entry.Description), IconSvg = entry.IconSvg is { Length: <= 64 * 1024 } svg ? svg : null, Unavailable = Bounded(entry.Unavailable),
                    Catalog = Bounded(catalog.Title),
                    ConfiguredKey = installed.GetValueOrDefault(entry.Id) ?? (isCodex ? Configured(contribution.ProviderType) : null),
                    Program = isCodex ? CodexProgram(null) : new FleetProviderProgram(entry.InstallKind switch
                    {
                        PluginProviderCatalogInstallKind.PackageRunner => "runner",
                        PluginProviderCatalogInstallKind.None => "found",
                        _ => "missing",
                    }, Bounded(entry.Version), null, Bounded(entry.Distribution)),
                });
            }
        }

        return new FleetProviderOfferList(offers, Bounded(error));
    }

    /// <inheritdoc />
    public Task<FleetProviderOperationState> StartAddAsync(FleetProviderAddParams parameters, string setBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(setBy);
        var offer = parameters.OfferId.Trim();
        var key = Optional(parameters.Key);
        if (string.Equals(offer, ClaudeType, StringComparison.Ordinal))
        {
            if (ClaudeProgram(null) is { State: "unsupported" } unsupported) throw new ArgumentException(unsupported.Detail);
            return Task.FromResult(Start((operation, token) => AddClaudeAsync(operation, key, parameters.MakeDefault, setBy, token)));
        }

        if (OfferedTypes.Contains(offer, StringComparer.Ordinal))
        {
            // A type of the node that runs no program: written at once; its key or its sign-in follows.
            var status = SetAddedAsync(offer, key, parameters.MakeDefault, setBy);
            return Task.FromResult(Completed(status.Key, $"{status.Name} was added."));
        }

        if (offer.Split(':', 2) is [var type, var entry] && PluginProviderTypes?.Find(type) is { Catalog: { } catalog } contribution)
            return Task.FromResult(Start((operation, token) => AddPluginAsync(operation, contribution, catalog, entry, key, parameters.MakeDefault, token)));
        throw new ArgumentException($"This machine offers no provider {offer}.");
    }

    /// <inheritdoc />
    public Task<FleetProviderOperationState> StartUpdateAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var store = Store;
        if (store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is { } definition)
        {
            if (definition.ProviderType != ClaudeType || ClaudeProgram(definition) is not { State: "missing" })
                throw new ArgumentException($"{definition.DisplayName ?? key} has nothing to update on this machine.");
            return Task.FromResult(Start(async (operation, token) =>
            {
                await DownloadClaudeAsync(operation, token).ConfigureAwait(false);
                lock (_gate) Apply(Store, definition.ProviderKey);
                return definition.ProviderKey;
            }));
        }

        if (TryRead(store.LoadGlobalExternalProviders)?.FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is not { } provider)
            throw new KeyNotFoundException($"No provider of this node has the key {key}.");
        if (PluginProviderTypes?.Find(provider.ProviderType) is not { Catalog: { } catalog } contribution)
            throw new ArgumentException($"The plugin of {provider.ProviderKey} does not run on this machine.");
        return Task.FromResult(Start(async (operation, token) =>
        {
            var settings = PluginModelProviderTypes.CreateSettings(provider, Store.GetEffectiveDefaultProvider());
            if (string.Equals(provider.ProviderType, CodexCliType, StringComparison.OrdinalIgnoreCase))
            {
                await catalog.InstallAsync(CodexCatalogEntry, Progress(operation), token).ConfigureAwait(false);
            }
            else
            {
                if (await catalog.GetUpdateAsync(settings, token).ConfigureAwait(false) is null) throw new InvalidOperationException($"{settings.DisplayName} is up to date.");
                var changed = await catalog.UpdateAsync(settings, Progress(operation), token).ConfigureAwait(false);
                lock (_gate)
                {
                    var values = new Dictionary<string, object?>(provider.Values, StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, value) in changed) values[name] = value;
                    Store.SaveGlobalExternalProvider(provider.ProviderKey, provider.ProviderType, values);
                    RememberApplied();
                }
            }

            RegisterPlugin(provider.ProviderKey);
            return provider.ProviderKey;
        }));
    }

    /// <inheritdoc />
    public async Task<FleetProviderOperationState> WaitOperationAsync(string id, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Operation? operation;
        lock (_gate) _operations.TryGetValue(id, out operation);
        if (operation is null) return new FleetProviderOperationState(id, "failed", "The machine knows no such operation.", null, null, null);
        try
        {
            return await operation.Completion.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return operation.Current;
        }
    }

    /// <summary>
    /// Downloads the versions CodeAlta pins now of the programs the providers of the node run, once the node was updated to a
    /// release that pins new ones: only where a version was installed before, so that the first installation stays the choice of
    /// the person. Then removes the versions no longer pinned, which no session runs before the node starts its sessions.
    /// </summary>
    /// <param name="cancellationToken">Stops the downloads; what is on disk is resumed by the next one.</param>
    /// <returns>The end of the downloads; never throws.</returns>
    internal async Task FollowPinnedVersionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = Store;
            if (HasOtherVersions(ClaudeRoot, ConfiguredClaudeCodeCli.PinnedVersion)
                && store.LoadGlobalProviderDefinitions(includeDisabled: false).Any(definition => definition.ProviderType == ClaudeType && ClaudeProgram(definition).State == "missing"))
            {
                await DownloadClaudeAsync(null, cancellationToken).ConfigureAwait(false);
            }

            if (HasOtherVersions(CodexRoot, CodexPinnedRelease.Version) && CodexProgram(null).State == "missing"
                && PluginProviderTypes?.Find(CodexCliType) is { Catalog: { } codex }
                && (TryRead(store.LoadGlobalExternalProviders) ?? []).Any(static provider => string.Equals(provider.ProviderType, CodexCliType, StringComparison.OrdinalIgnoreCase)))
            {
                await codex.InstallAsync(CodexCatalogEntry, new Progress<PluginProviderInstallProgress>(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The next start tries again; a session that needs the program says what is missing meanwhile.
        }

        RemoveOtherVersions(ClaudeRoot, ConfiguredClaudeCodeCli.PinnedVersion, ClaudeProgram(null).State == "installed");
        RemoveOtherVersions(CodexRoot, CodexPinnedRelease.Version, CodexProgram(null).State == "installed");
    }

    // The entry of Codex CLI in the catalog of its plugin.
    private const string CodexCatalogEntry = "codex-cli";

    // The program of a Claude Code provider: its command, else the version pinned in the cache of the node, else the Claude Code
    // installed on the machine; missing when none, which the node installs.
    internal FleetProviderProgram ClaudeProgram(CodeAltaProviderDocument? definition)
    {
        var claude = Claude;
        if (Optional(definition?.Command) is { } command)
            return claude.ResolveExecutable(command) is { } path ? new FleetProviderProgram("found", null, null, path)
                : new FleetProviderProgram("unsupported", null, null, $"Its command {command} is not on the machine.");
        if (claude.InstalledPath is { } pinned) return new FleetProviderProgram("installed", ConfiguredClaudeCodeCli.PinnedVersion, null, pinned);
        if (ConfiguredClaudeCodeCli.FindUserInstall() is { } user) return new FleetProviderProgram("found", null, null, user);
        if (!claude.IsSupported) return new FleetProviderProgram("unsupported", null, null, "Claude Code has no build for this machine.");
        // A version installed before, and not the one CodeAlta pins now: an update.
        return new FleetProviderProgram("missing", ConfiguredClaudeCodeCli.PinnedVersion,
            HasOtherVersions(ClaudeRoot, ConfiguredClaudeCodeCli.PinnedVersion) ? ConfiguredClaudeCodeCli.PinnedVersion : null, null);
    }

    // The program of a Codex CLI provider: its command, else the app-server CodeAlta pins, in the cache of the node.
    private FleetProviderProgram CodexProgram(CodeAltaExternalProvider? provider)
    {
        if (provider?.Values.GetValueOrDefault("command") is string { Length: > 0 } command) return new FleetProviderProgram("found", null, null, command);
        using var installer = new CodexAppServerInstaller(CodexRoot);
        if (installer.Installed is { } installed) return new FleetProviderProgram("installed", installed.Version, null, installed.Executable);
        if (CodexPinnedRelease.FindPackage(CodexPinnedRelease.CurrentTarget()) is null)
            return new FleetProviderProgram("unsupported", null, null, "Codex publishes no app-server for this machine.");
        return new FleetProviderProgram("missing", CodexPinnedRelease.Version, HasOtherVersions(CodexRoot, CodexPinnedRelease.Version) ? CodexPinnedRelease.Version : null, null);
    }

    // The program of a provider of a plugin: Codex CLI's, or the version of an agent its catalog installed, with the update its
    // catalog offers (read briefly: a catalog that does not answer at once offers none here).
    private async Task<FleetProviderProgram?> PluginProgramAsync(CodeAltaExternalProvider provider, PluginModelProviderContribution contribution, PluginProviderSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.Equals(provider.ProviderType, CodexCliType, StringComparison.OrdinalIgnoreCase)) return CodexProgram(provider);
        if (contribution.Catalog is not { } catalog || settings.GetString("agent") is null) return null;
        string? update = null;
        try
        {
            update = await catalog.GetUpdateAsync(settings, cancellationToken).WaitAsync(UpdateCheckWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        {
        }

        var distribution = settings.GetString("distribution");
        return new FleetProviderProgram(distribution is "npx" or "uvx" ? "runner" : "installed", Bounded(settings.GetString("version")), Bounded(update), Bounded(distribution));
    }

    private async Task<string> AddClaudeAsync(Operation operation, string? key, bool makeDefault, string setBy, CancellationToken cancellationToken)
    {
        if (ClaudeProgram(null).State == "missing") await DownloadClaudeAsync(operation, cancellationToken).ConfigureAwait(false);
        return SetAddedAsync(ClaudeType, key, makeDefault, setBy).Key;
    }

    // Downloads the pinned Claude Code, or waits for the download that runs; its progress goes to the operation.
    private async Task DownloadClaudeAsync(Operation? operation, CancellationToken cancellationToken)
    {
        var claude = Claude;
        var ended = new TaskCompletionSource<ConfiguredClaudeCodeCliState>(TaskCreationOptions.RunContinuationsAsynchronously);
        claude.TryStart();
        using var subscription = claude.Subscribe(state =>
        {
            operation?.Report(state.State == "installing" ? "Claude Code is being installed…" : $"Claude Code {ConfiguredClaudeCodeCli.PinnedVersion} is being downloaded…",
                state.ReceivedBytes, state.TotalBytes);
            if (state.State is "done" or "failed" or "canceled") ended.TrySetResult(state);
        });
        using var cancel = cancellationToken.Register(claude.Cancel);
        var end = await ended.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (end.State != "done" || claude.InstalledPath is null)
            throw new InvalidOperationException($"Claude Code could not be downloaded on this machine ({end.Error ?? end.State}).");
    }

    private async Task<string> AddPluginAsync(Operation operation, PluginModelProviderContribution contribution, IPluginProviderCatalog catalog, string entry, string? key, bool makeDefault,
        CancellationToken cancellationToken)
    {
        var result = await catalog.InstallAsync(entry, Progress(operation), cancellationToken).ConfigureAwait(false);
        string added;
        lock (_gate)
        {
            var store = Store;
            added = key ?? FreeKey(store, result.SuggestedKey);
            if (key is not null && Exists(store, key)) throw new InvalidOperationException($"Another provider of this machine has the key {key}.");
            var values = new Dictionary<string, object?>(result.Values, StringComparer.OrdinalIgnoreCase) { ["enabled"] = true, ["display_name"] = result.DisplayName };
            if (result.Icon is { } icon) values["icon"] = icon;
            store.SaveGlobalExternalProvider(added, contribution.ProviderType, values);
            if (makeDefault) store.SaveGlobalDefaultProvider(added);
            RememberApplied();
        }

        RegisterPlugin(added);
        return added;
    }

    // A type of the node written as a new provider, under the key given or the first free one of its type.
    private FleetProviderStatus SetAddedAsync(string type, string? key, bool makeDefault, string setBy)
    {
        string chosen;
        lock (_gate)
        {
            var store = Store;
            if (key is not null && Exists(store, key)) throw new InvalidOperationException($"Another provider of this machine has the key {key}.");
            chosen = key ?? FreeKey(store, type);
        }

        return SetAsync(new FleetProviderSetParams(chosen, type, null, null, null, null, Enabled: true, MakeDefault: makeDefault), setBy, CancellationToken.None).GetAwaiter().GetResult();
    }

    // A provider of a plugin registered again as the configuration has it, and prepared, so that the page sees it ready or why not.
    private void RegisterPlugin(string key)
    {
        if (PluginProviderTypes is not { } types) return;
        try
        {
            var store = Store;
            if (TryRead(store.LoadGlobalExternalProviders)?.FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is not { } provider) return;
            var settings = PluginModelProviderTypes.CreateSettings(provider, store.GetEffectiveDefaultProvider());
            if (!settings.IsEnabled) return;
            var registry = host.ModelProviderRegistry;
            if (types.TryCreateProviderRegistration(settings, Root, registry.SecretIssued, registry.BoundSecrets, out var descriptor, out var createRuntime))
            {
                registry.RegisterOrReplace(descriptor, createRuntime);
                _ = PrepareAsync(descriptor.ProviderId);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException)
        {
            // Applied at the next start.
        }
    }

    private static bool Exists(CodeAltaConfigStore store, string key)
        => store.LoadGlobalProviderDefinitions(includeDisabled: true).Any(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))
            || (TryRead(store.LoadGlobalExternalProviders) ?? []).Any(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    // The key suggested, as a key (lowercase letters, digits, - and _), or the first free one after it (-2, -3...).
    private static string FreeKey(CodeAltaConfigStore store, string suggested)
    {
        var key = new string([.. suggested.Trim().ToLowerInvariant().Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')]).Trim('-');
        if (key.Length == 0) key = "provider";
        if (key.Length > 56) key = key[..56];
        if (!Exists(store, key)) return key;
        for (var number = 2; ; number++)
        {
            if (!Exists(store, $"{key}-{number}")) return $"{key}-{number}";
        }
    }

    // The keys of the providers of a type of a plugin that run an entry of its catalog, by the entry (the "agent" setting).
    private static Dictionary<string, string> InstalledEntries(IReadOnlyList<CodeAltaExternalProvider> external, string type)
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in external.Where(provider => string.Equals(provider.ProviderType, type, StringComparison.OrdinalIgnoreCase)))
        {
            if (provider.Values.GetValueOrDefault("agent") is string id) installed.TryAdd(id, provider.ProviderKey);
        }

        return installed;
    }

    // How the picker groups a type of the node: a local model server, a subscription signed in, or an API key.
    private static string Group(string type)
        => type == "llama-cpp" ? "local" : Credential(type) == "sign_in" ? "subscription" : Credential(type) == "cli" ? "agent" : "key";

    // Whether the folder of a pinned program holds a version other than the one pinned now: it was installed before an update.
    private static bool HasOtherVersions(string root, string version)
        => Directory.Exists(root) && Directory.EnumerateDirectories(root).Any(folder => IsVersionFolder(folder, version));

    // Removes the versions of a pinned program other than the one pinned now, once that one is installed. A file a process still
    // holds stays, and is removed at a later start.
    private static void RemoveOtherVersions(string root, string version, bool current)
    {
        if (!current || !Directory.Exists(root)) return;
        foreach (var folder in Directory.EnumerateDirectories(root).Where(folder => IsVersionFolder(folder, version)).ToArray())
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // A folder of another version: a name that starts with a digit (2.1.322, 0.130.0), not a partial download.
    private static bool IsVersionFolder(string folder, string version)
    {
        var name = Path.GetFileName(folder);
        return name.Length > 0 && char.IsAsciiDigit(name[0]) && !name.EndsWith(".partial", StringComparison.Ordinal) && !string.Equals(name, version, StringComparison.Ordinal);
    }

    private static IProgress<PluginProviderInstallProgress> Progress(Operation operation)
        => new SynchronousProgress(value => operation.Report(Bounded(value.Message), value.Received, value.Total));

    // An operation of the node: a download, then the provider written. Kept a while once it ended, so that a wait reads its end.
    private FleetProviderOperationState Start(Func<Operation, CancellationToken, Task<string>> run)
    {
        var operation = new Operation(Guid.NewGuid().ToString("N")[..16]);
        lock (_gate)
        {
            foreach (var old in _operations.Values.Where(static value => value.Completion.IsCompleted).OrderBy(static value => value.StartedAt).ToArray())
            {
                if (_operations.Count < MaxOperations && DateTimeOffset.UtcNow - old.StartedAt < OperationLifetime) break;
                _operations.Remove(old.Id);
            }

            if (_operations.Count >= MaxOperations) throw new InvalidOperationException("Too many additions and updates run on this machine: wait for one to end.");
        }

        operation.Completion = Task.Run(async () =>
        {
            using var lifetime = new CancellationTokenSource(OperationLifetime);
            try
            {
                var key = await run(operation, lifetime.Token).ConfigureAwait(false);
                return operation.End("completed", null, key);
            }
            catch (OperationCanceledException)
            {
                return operation.End("failed", "It took too long and was stopped.", null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return operation.End("failed", Bounded(Masked(exception.Message)), null);
            }
        });
        lock (_gate) _operations[operation.Id] = operation;
        return operation.Current;
    }

    private FleetProviderOperationState Completed(string key, string message)
    {
        var operation = new Operation(Guid.NewGuid().ToString("N")[..16]);
        var end = operation.End("completed", message, key);
        operation.Completion = Task.FromResult(end);
        lock (_gate) _operations[operation.Id] = operation;
        return end;
    }

    // A message of a failure as the desktop may see it: the secrets of the machine masked.
    private string Masked(string text) => host.AgentHub.SecretMasker?.MaskText(text) ?? text;

    private sealed class Operation(string id)
    {
        private readonly Lock _gate = new();
        private FleetProviderOperationState _current = new(id, "running", null, null, null, null);

        public string Id { get; } = id;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public Task<FleetProviderOperationState> Completion { get; set; } = Task.FromResult(new FleetProviderOperationState(id, "running", null, null, null, null));

        public FleetProviderOperationState Current { get { lock (_gate) return _current; } }

        public void Report(string? message, long? received, long? total)
        {
            lock (_gate)
            {
                if (_current.Status == "running") _current = _current with { Message = message, Received = received, Total = total is > 0 ? total : null };
            }
        }

        public FleetProviderOperationState End(string status, string? message, string? key)
        {
            lock (_gate) return _current = new FleetProviderOperationState(Id, status, message, null, null, key);
        }
    }

    // A progress reported where it happens, in order: Progress<T> posts to the thread pool, which may reorder the reports.
    private sealed class SynchronousProgress(Action<PluginProviderInstallProgress> report) : IProgress<PluginProviderInstallProgress>
    {
        public void Report(PluginProviderInstallProgress value) => report(value);
    }
}