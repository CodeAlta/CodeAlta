using CodeAlta.Acp;
using CodeAlta.Agent.Acp;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.App;

internal sealed class AcpManagementService
{
    private readonly CatalogOptions _catalogOptions;
    private readonly AcpAgentRegistryService _registryService;
    private readonly CodeAltaConfigStore _configStore;
    private readonly AcpInstalledBackendStore _installedBackendStore;
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;
    private readonly AcpInstallResolver _installResolver;

    public AcpManagementService(
        CatalogOptions catalogOptions,
        AcpAgentRegistryService registryService,
        CodeAltaConfigStore configStore,
        AcpInstalledBackendStore installedBackendStore,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        AcpInstallResolver? installResolver = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(registryService);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(installedBackendStore);
        ArgumentNullException.ThrowIfNull(chatBackendStates);

        _catalogOptions = catalogOptions;
        _registryService = registryService;
        _configStore = configStore;
        _installedBackendStore = installedBackendStore;
        _chatBackendStates = chatBackendStates;
        _installResolver = installResolver ?? new AcpInstallResolver();
    }

    public async Task<AcpManagementSnapshot> LoadSnapshotAsync(
        bool refreshRegistry,
        CancellationToken cancellationToken = default)
    {
        AcpRegistryDocument? registry = null;
        string? registryError = null;

        try
        {
            registry = refreshRegistry
                ? await _registryService.RefreshRegistryAsync(cancellationToken)
                : await _registryService.LoadCachedRegistryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException)
        {
            registryError = ex.Message;
        }

        var registryFetchedAt = File.Exists(_registryService.RegistryCachePath)
            ? (DateTime?)File.GetLastWriteTimeUtc(_registryService.RegistryCachePath)
            : null;

        var installedDefinitions = _installedBackendStore.Load();
        var configuredDefinitions = _configStore.LoadGlobalAcpBackendDefinitions(includeDisabled: true);
        var configuredByAgentId = configuredDefinitions.ToDictionary(
            static definition => definition.AgentId,
            static definition => definition,
            StringComparer.OrdinalIgnoreCase);
        var installedByAgentId = installedDefinitions.ToDictionary(
            static definition => definition.AgentId,
            static definition => definition,
            StringComparer.OrdinalIgnoreCase);
        var effectiveByAgentId = _configStore.LoadEffectiveAcpBackendDefinitions(installedDefinitions).ToDictionary(
            static definition => definition.AgentId,
            static definition => definition,
            StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, AcpAgentSummaryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in registry?.Agents ?? [])
        {
            var matchingInstalled = FindMatchingDefinition(installedDefinitions, manifest);
            var matchingConfigured = FindMatchingDefinition(configuredDefinitions, manifest);
            var agentId = matchingConfigured?.AgentId ?? matchingInstalled?.AgentId ?? manifest.Id;
            items[agentId] = BuildItem(
                agentId,
                manifest,
                matchingInstalled,
                matchingConfigured,
                effectiveByAgentId.TryGetValue(agentId, out var effectiveDefinition) ? effectiveDefinition : null);
        }

        foreach (var definition in installedDefinitions)
        {
            if (items.ContainsKey(definition.AgentId))
            {
                continue;
            }

            configuredByAgentId.TryGetValue(definition.AgentId, out var configuredDefinition);
            effectiveByAgentId.TryGetValue(definition.AgentId, out var effectiveDefinition);
            items[definition.AgentId] = BuildItem(
                definition.AgentId,
                manifest: null,
                installedDefinition: definition,
                configuredDefinition,
                effectiveDefinition);
        }

        foreach (var definition in configuredDefinitions)
        {
            if (items.ContainsKey(definition.AgentId))
            {
                continue;
            }

            effectiveByAgentId.TryGetValue(definition.AgentId, out var effectiveDefinition);
            items[definition.AgentId] = BuildItem(
                definition.AgentId,
                manifest: null,
                installedDefinition: null,
                configuredDefinition: definition,
                effectiveDefinition);
        }

        return new AcpManagementSnapshot(
            registry?.Version,
            registryFetchedAt,
            registryError,
            items.Values
                .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public AcpBackendDefinition CreateEditableDefinition(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var configured = _configStore.LoadGlobalAcpBackendDefinition(agentId);
        if (configured is not null)
        {
            return CloneDefinition(configured);
        }

        var installed = _installedBackendStore.Load()
            .FirstOrDefault(definition => string.Equals(definition.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        if (installed is not null)
        {
            return CloneDefinition(installed);
        }

        return new AcpBackendDefinition
        {
            AgentId = agentId.Trim().ToLowerInvariant(),
            DisplayName = agentId.Trim(),
            Enabled = AcpBackendDefinition.DefaultEnabled,
            UseUnstable = AcpBackendDefinition.DefaultUseUnstable,
            EnableFilesystem = AcpBackendDefinition.DefaultEnableFilesystem,
            EnableTerminal = AcpBackendDefinition.DefaultEnableTerminal,
        };
    }

    public AcpBackendDefinition CreateNewManualDefinition()
    {
        return new AcpBackendDefinition
        {
            Enabled = AcpBackendDefinition.DefaultEnabled,
            UseUnstable = AcpBackendDefinition.DefaultUseUnstable,
            EnableFilesystem = AcpBackendDefinition.DefaultEnableFilesystem,
            EnableTerminal = AcpBackendDefinition.DefaultEnableTerminal,
        };
    }

    public bool AgentIdExists(string agentId, string? exceptAgentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var normalized = agentId.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(exceptAgentId) &&
            string.Equals(normalized, exceptAgentId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _configStore.LoadGlobalAcpBackendDefinitions(includeDisabled: true)
                   .Any(definition => string.Equals(definition.AgentId, normalized, StringComparison.OrdinalIgnoreCase)) ||
               _installedBackendStore.Load()
                   .Any(definition => string.Equals(definition.AgentId, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AcpBackendDefinition> InstallAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var definition = await _registryService.InstallAgentAsync(agentId, cancellationToken);
        return CloneDefinition(definition);
    }

    public void SaveConfiguration(AcpBackendDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var normalized = CloneDefinition(definition);
        normalized.AgentId = normalized.AgentId.Trim().ToLowerInvariant();
        normalized.RegistryId = string.IsNullOrWhiteSpace(normalized.RegistryId)
            ? null
            : normalized.RegistryId.Trim().ToLowerInvariant();
        normalized.DisplayName = string.IsNullOrWhiteSpace(normalized.DisplayName) ? null : normalized.DisplayName.Trim();
        normalized.Command = string.IsNullOrWhiteSpace(normalized.Command) ? null : normalized.Command.Trim();
        normalized.WorkingDirectory = string.IsNullOrWhiteSpace(normalized.WorkingDirectory) ? null : normalized.WorkingDirectory.Trim();
        normalized.Arguments = normalized.Arguments?
            .Where(static argument => !string.IsNullOrWhiteSpace(argument))
            .Select(static argument => argument.Trim())
            .ToList();
        normalized.EnvironmentVariables = normalized.EnvironmentVariables?
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                static entry => entry.Key.Trim(),
                static entry => entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        _configStore.SaveGlobalAcpBackendDefinition(normalized);
    }

    public bool ResetConfiguration(string agentId)
    {
        return _configStore.DeleteGlobalAcpBackendDefinition(agentId);
    }

    public bool RemoveAgent(string agentId, bool removeArtifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var removedConfig = _configStore.DeleteGlobalAcpBackendDefinition(agentId);
        var removedInstall = _registryService.RemoveInstalledAgent(agentId);
        if (removeArtifacts)
        {
            DeleteAgentDirectory("installs", agentId);
            DeleteAgentDirectory("downloads", agentId);
            DeleteAgentDirectory("state", agentId);
        }

        return removedConfig || removedInstall;
    }

    private AcpAgentSummaryItem BuildItem(
        string agentId,
        AcpRegistryAgentManifest? manifest,
        AcpBackendDefinition? installedDefinition,
        AcpBackendDefinition? configuredDefinition,
        AcpBackendDefinition? effectiveDefinition)
    {
        var definitionForDisplay = effectiveDefinition ?? configuredDefinition ?? installedDefinition;
        var backendId = AcpAgentBackendFactoryExtensions.CreateBackendId(agentId);
        _chatBackendStates.TryGetValue(backendId.Value, out var runtimeState);
        var distributionKinds = GetDistributionKinds(manifest);
        var installability = ResolveInstallability(manifest);

        var commandDefinition = effectiveDefinition ?? configuredDefinition ?? installedDefinition;
        var commandSummary = BuildCommandSummary(commandDefinition) ?? BuildInstallCommandPreview(manifest, _installResolver);
        var isBroken = commandDefinition is not null && !IsCommandAvailable(commandDefinition);

        return new AcpAgentSummaryItem(
            AgentId: agentId,
            DisplayName: definitionForDisplay?.DisplayName ?? manifest?.Name ?? agentId,
            Description: manifest?.Description,
            RegistryId: configuredDefinition?.RegistryId ?? installedDefinition?.RegistryId ?? manifest?.Id,
            RegistryVersion: manifest?.Version,
            Repository: manifest?.Repository,
            Website: manifest?.Website,
            Authors: manifest?.Authors ?? [],
            License: manifest?.License,
            DistributionKinds: distributionKinds,
            Installability: installability.State,
            InstallabilityMessage: installability.Message,
            IsInRegistry: manifest is not null,
            IsInstalled: installedDefinition is not null,
            HasConfiguration: configuredDefinition is not null,
            IsEnabled: effectiveDefinition?.Enabled ??
                configuredDefinition?.Enabled ??
                installedDefinition?.Enabled ??
                AcpBackendDefinition.DefaultEnabled,
            IsManual: manifest is null && string.IsNullOrWhiteSpace(definitionForDisplay?.RegistryId),
            IsBroken: isBroken,
            CommandSummary: commandSummary,
            WorkingDirectory: commandDefinition?.WorkingDirectory,
            RuntimeStatus: runtimeState?.StatusMessage,
            RuntimeAvailability: runtimeState?.Availability,
            RuntimeModelCount: runtimeState?.Models.Count,
            RuntimeModels: runtimeState?.Models.Select(static model => model.DisplayName ?? model.Id).ToArray() ?? []);
    }

    private (AcpInstallabilityState State, string Message) ResolveInstallability(AcpRegistryAgentManifest? manifest)
    {
        if (manifest is null)
        {
            return (AcpInstallabilityState.Unknown, "Registry metadata unavailable.");
        }

        try
        {
            var plan = _installResolver.Resolve(manifest);
            var summary = plan.Kind switch
            {
                AcpInstallKind.Binary => $"Installable on {plan.TargetId}.",
                AcpInstallKind.Npx => "Installable via npx.",
                AcpInstallKind.Uvx => "Installable via uvx.",
                _ => "Installable.",
            };
            return (AcpInstallabilityState.Installable, summary);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return (AcpInstallabilityState.Unavailable, ex.Message);
        }
    }

    private static IReadOnlyList<string> GetDistributionKinds(AcpRegistryAgentManifest? manifest)
    {
        if (manifest is null)
        {
            return [];
        }

        var kinds = new List<string>(3);
        if (manifest.Distribution.Binary is { Count: > 0 })
        {
            kinds.Add("binary");
        }

        if (manifest.Distribution.Npx is not null)
        {
            kinds.Add("npx");
        }

        if (manifest.Distribution.Uvx is not null)
        {
            kinds.Add("uvx");
        }

        return kinds;
    }

    private static AcpBackendDefinition? FindMatchingDefinition(
        IReadOnlyList<AcpBackendDefinition> definitions,
        AcpRegistryAgentManifest manifest)
    {
        return definitions.FirstOrDefault(definition =>
            string.Equals(definition.AgentId, manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(definition.RegistryId, manifest.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildCommandSummary(AcpBackendDefinition? definition)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Command))
        {
            return null;
        }

        return definition.Arguments is { Count: > 0 }
            ? $"{definition.Command} {string.Join(' ', definition.Arguments)}"
            : definition.Command;
    }

    private static string? BuildInstallCommandPreview(AcpRegistryAgentManifest? manifest, AcpInstallResolver installResolver)
    {
        if (manifest is null)
        {
            return null;
        }

        try
        {
            var plan = installResolver.Resolve(manifest);
            return plan.Arguments is { Count: > 0 }
                ? $"{plan.Command} {string.Join(' ', plan.Arguments)}"
                : plan.Command;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool IsCommandAvailable(AcpBackendDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Command))
        {
            return false;
        }

        if (Path.IsPathRooted(definition.Command))
        {
            return File.Exists(definition.Command);
        }

        return FindCommandPath(definition.Command) is not null;
    }

    private static string? FindCommandPath(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        var searchNames = BuildSearchNames(command.Trim());
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var searchName in searchNames)
            {
                var candidate = Path.Combine(directory, searchName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildSearchNames(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
        {
            return [command];
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExtensions)
            ? [".exe", ".cmd", ".bat"]
            : pathExtensions.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [command, .. extensions.Select(extension => command + extension)];
    }

    private void DeleteAgentDirectory(string kind, string agentId)
    {
        var root = kind switch
        {
            "installs" => _catalogOptions.AcpInstallsRoot,
            "downloads" => _catalogOptions.AcpDownloadsRoot,
            "state" => _catalogOptions.AcpStateRoot,
            _ => throw new InvalidOperationException($"Unsupported ACP directory kind '{kind}'."),
        };
        var path = Path.Combine(root, agentId.Trim().ToLowerInvariant());
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static AcpBackendDefinition CloneDefinition(AcpBackendDefinition definition)
    {
        return new AcpBackendDefinition
        {
            AgentId = definition.AgentId,
            DisplayName = definition.DisplayName,
            Enabled = definition.Enabled,
            RegistryId = definition.RegistryId,
            Command = definition.Command,
            Arguments = definition.Arguments is null ? null : [.. definition.Arguments],
            WorkingDirectory = definition.WorkingDirectory,
            EnvironmentVariables = definition.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(definition.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
            UseUnstable = definition.UseUnstable,
            EnableTerminal = definition.EnableTerminal,
            EnableFilesystem = definition.EnableFilesystem,
            EnableElicitation = definition.EnableElicitation,
        };
    }
}

internal enum AcpInstallabilityState
{
    Unknown,
    Installable,
    Unavailable,
}

internal sealed record AcpManagementSnapshot(
    string? RegistryVersion,
    DateTime? RegistryFetchedAtUtc,
    string? RegistryError,
    IReadOnlyList<AcpAgentSummaryItem> Items);

internal sealed record AcpAgentSummaryItem(
    string AgentId,
    string DisplayName,
    string? Description,
    string? RegistryId,
    string? RegistryVersion,
    string? Repository,
    string? Website,
    IReadOnlyList<string> Authors,
    string? License,
    IReadOnlyList<string> DistributionKinds,
    AcpInstallabilityState Installability,
    string InstallabilityMessage,
    bool IsInRegistry,
    bool IsInstalled,
    bool HasConfiguration,
    bool IsEnabled,
    bool IsManual,
    bool IsBroken,
    string? CommandSummary,
    string? WorkingDirectory,
    string? RuntimeStatus,
    ChatBackendAvailability? RuntimeAvailability,
    int? RuntimeModelCount,
    IReadOnlyList<string> RuntimeModels)
{
    public string CatalogLabel =>
        $"{(IsInstalled ? "[installed]" : Installability == AcpInstallabilityState.Installable ? "[ready]" : "[unavailable]")} {DisplayName}";

    public string InstalledLabel =>
        $"{(IsBroken ? "[broken]" : IsEnabled ? "[enabled]" : "[disabled]")} {DisplayName}";
}
