using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.CodexSdk;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.App;

internal readonly record struct ProviderTestResult(bool Success, string Message, int ModelCount);

internal sealed class ProviderFrontendCoordinator
{
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaConfigStore _configStore;
    private readonly ChatBackendInitializationCoordinator _chatBackendInitializationCoordinator;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _refreshSelectionAndThreadWorkspace;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public ProviderFrontendCoordinator(
        CodeAltaOwnedServices? ownedServices,
        CatalogOptions catalogOptions,
        ChatBackendInitializationCoordinator chatBackendInitializationCoordinator,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Action<Action> dispatchToUi,
        Action refreshSelectionAndThreadWorkspace,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(chatBackendInitializationCoordinator);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(refreshSelectionAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setStatus);

        _ownedServices = ownedServices;
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _chatBackendInitializationCoordinator = chatBackendInitializationCoordinator;
        _chatBackendStates = chatBackendStates;
        _dispatchToUi = dispatchToUi;
        _refreshSelectionAndThreadWorkspace = refreshSelectionAndThreadWorkspace;
        _setStatus = setStatus;
    }

    public IReadOnlyList<CodeAltaProviderDocument> LoadProviderDefinitions()
        => _configStore.LoadGlobalProviderDefinitions(includeDisabled: true);

    public bool HasAnyEnabledProviders()
        => LoadProviderDefinitions().Any(static definition => definition.Enabled != false);

    public async Task SaveProviderDefinitionsAsync(
        IReadOnlyList<CodeAltaProviderDocument> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _configStore.SaveGlobalProviderDefinitions(definitions);
        if (_ownedServices is null)
        {
            _setStatus("Provider configuration saved.", false, StatusTone.Info);
            return;
        }

        await _ownedServices.RefreshProviderBackendsAsync(cancellationToken);
        _dispatchToUi(
            () =>
            {
                SyncChatBackendCatalog();
                _refreshSelectionAndThreadWorkspace();
            });
        await _chatBackendInitializationCoordinator.InitializeAsync(cancellationToken);
        _dispatchToUi(
            () =>
            {
                SyncChatBackendCatalog();
                _refreshSelectionAndThreadWorkspace();
                _setStatus("Model providers refreshed.", false, StatusTone.Info);
            });
    }

    public async Task<ProviderTestResult> TestProviderAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var homeRoot = _ownedServices?.CatalogOptions.GlobalRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");
        var modelCatalog = _ownedServices?.ModelsDevCatalogService;

        if (!TryCreateBackend(definition, homeRoot, modelCatalog, out var backend))
        {
            return new ProviderTestResult(false, "Enter valid provider settings before testing.", 0);
        }

        await using var _ = backend;
        await backend.StartAsync(cancellationToken);
        var models = await backend.ListModelsAsync(cancellationToken);
        return new ProviderTestResult(true, $"Connected successfully · {models.Count} model(s) discovered.", models.Count);
    }

    private bool TryCreateBackend(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out IAgentBackend backend)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        if (string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal))
        {
            var codexPath = CodeAltaOwnedServices.ResolveCodexExecutablePath(Environment.GetEnvironmentVariable("CODEALTA_CODEX_PATH"));
            backend = new CodexAgentBackend(
                new CodexAgentBackendOptions
                {
                    ProcessOptions = new CodexProcessOptions
                    {
                        CodexPath = codexPath,
                        LocalRootPath = Path.Combine(stateRootPath, "cache"),
                        ReleaseTag = codexPath is null ? CodeAlta.CodexSdk.CodexClient.CompiledAgainstReleaseTag : null,
                    },
                });
            return true;
        }

        if (string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal))
        {
            backend = new CopilotAgentBackend(new CopilotAgentBackendOptions());
            return true;
        }

        if (!RawApiBackendRegistrar.TryCreateBackendRegistration(definition, stateRootPath, modelCatalog, out _, out var createBackend))
        {
            backend = null!;
            return false;
        }

        backend = createBackend();
        return true;
    }

    private void SyncChatBackendCatalog()
    {
        var backendDescriptors = _ownedServices?.BackendDescriptors
            ?? CodeAltaOwnedServices.CreateBuiltInBackendDescriptors();
        var activeBackendIds = new HashSet<string>(
            backendDescriptors.Select(static descriptor => descriptor.BackendId.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in backendDescriptors)
        {
            if (_chatBackendStates.TryGetValue(descriptor.BackendId.Value, out var existing))
            {
                existing.DisplayName = descriptor.DisplayName;
                continue;
            }

            _chatBackendStates[descriptor.BackendId.Value] = new ChatBackendState(descriptor.BackendId, descriptor.DisplayName);
        }

        foreach (var backendId in _chatBackendStates.Keys.Where(key => !activeBackendIds.Contains(key)).ToArray())
        {
            _chatBackendStates.Remove(backendId);
        }
    }
}
