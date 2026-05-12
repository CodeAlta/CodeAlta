using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.CopilotDirect;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI.CodexSubscription;
using CodeAlta.App.Events;
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
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private bool? _hasAnyEnabledProviders;

    public ProviderFrontendCoordinator(
        CodeAltaOwnedServices? ownedServices,
        CatalogOptions catalogOptions,
        ChatBackendInitializationCoordinator chatBackendInitializationCoordinator,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Action<Action> dispatchToUi,
        FrontendEventPublisher frontendEvents,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(chatBackendInitializationCoordinator);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(frontendEvents);
        ArgumentNullException.ThrowIfNull(setStatus);

        _ownedServices = ownedServices;
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _chatBackendInitializationCoordinator = chatBackendInitializationCoordinator;
        _chatBackendStates = chatBackendStates;
        _dispatchToUi = dispatchToUi;
        _frontendEvents = frontendEvents;
        _setStatus = setStatus;
    }

    public IReadOnlyList<CodeAltaProviderDocument> LoadProviderDefinitions()
    {
        var definitions = _configStore.LoadGlobalProviderDefinitions(includeDisabled: true);
        _hasAnyEnabledProviders = definitions.Any(static definition => definition.Enabled != false);
        return definitions;
    }

    public bool HasAnyEnabledProviders()
    {
        if (_hasAnyEnabledProviders is { } cached)
        {
            return cached;
        }

        var hasEnabledProviders = LoadProviderDefinitions().Any(static definition => definition.Enabled != false);
        _hasAnyEnabledProviders = hasEnabledProviders;
        return hasEnabledProviders;
    }

    public async Task SaveProviderDefinitionsAsync(
        IReadOnlyList<CodeAltaProviderDocument> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _configStore.SaveGlobalProviderDefinitions(definitions);
        _hasAnyEnabledProviders = null;
        if (_ownedServices is null)
        {
            _setStatus("Provider configuration saved.", false, StatusTone.Info);
            return;
        }

        await _ownedServices.RefreshProviderBackendsAsync(cancellationToken);
        _dispatchToUi(
            () =>
            {
                SyncModelProviderCatalog();
                PublishModelProviderCatalogChanged();
            });
        await _chatBackendInitializationCoordinator.InitializeAsync(cancellationToken);
        _dispatchToUi(
            () =>
            {
                SyncModelProviderCatalog();
                PublishModelProviderCatalogChanged();
                _setStatus("Model providers refreshed.", false, StatusTone.Info);
            });
    }

    public async Task<ProviderTestResult> TestProviderAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (IsTemporarilyDisabledCopilotProvider(definition))
        {
            return new ProviderTestResult(
                false,
                "GitHub Copilot is temporarily disabled until the upstream Copilot SDK process cleanup issue is fixed.",
                0);
        }

        if (TryBuildActiveBackendTestResult(definition, _chatBackendStates, out var activeResult))
        {
            return activeResult;
        }

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

    public async Task<ProviderTestResult> LoginCodexSubscriptionWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(reportStatus);

        var manager = CreateCodexSubscriptionLoginManager(definition);
        var login = manager.BeginBrowserLogin(definition.AccountId);
        var waitForCallbackTask = manager.WaitForBrowserCallbackAsync(login, cancellationToken).AsTask();
        reportStatus($"Open ChatGPT login in your browser: {login.AuthorizeUri}");
        TryOpenBrowser(login.AuthorizeUri);
        var credential = await waitForCallbackTask;
        return new ProviderTestResult(
            true,
            FormatCodexCredentialMessage("ChatGPT browser login completed", credential),
            0);
    }

    public async Task<ProviderTestResult> LoginCodexSubscriptionWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(reportStatus);

        var manager = CreateCodexSubscriptionLoginManager(definition);
        var credential = await manager.CompleteDeviceLoginAsync(
                (deviceCode, _) =>
                {
                    reportStatus(
                        $"Open {deviceCode.VerificationUri} and enter code {deviceCode.UserCode}. Waiting for ChatGPT authorization...");
                    return ValueTask.CompletedTask;
                },
                cancellationToken: cancellationToken);
        return new ProviderTestResult(
            true,
            FormatCodexCredentialMessage("ChatGPT device-code login completed", credential),
            0);
    }

    public async Task<ProviderTestResult> LogoutCodexSubscriptionAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var manager = CreateCodexSubscriptionLoginManager(definition);
        await manager.DeleteCredentialAsync(cancellationToken);
        return new ProviderTestResult(true, "Deleted CodeAlta-owned ChatGPT/Codex credentials for this provider.", 0);
    }

    public async Task<ProviderTestResult> TestCodexSubscriptionAuthenticationAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var authManager = CreateCodexSubscriptionAuthManager(definition);
        var context = await authManager.GetAccountContextAsync(cancellationToken);
        var account = string.IsNullOrWhiteSpace(context.AccountId) ? "no account/workspace id in token" : context.AccountId;
        return new ProviderTestResult(true, $"Authenticated without sending a model turn · account/workspace: {account}.", 0);
    }

    public async Task<ProviderTestResult> ListCodexSubscriptionModelsAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = await TestProviderAsync(definition, cancellationToken);
        return result.Success
            ? result with { Message = $"Model listing completed without sending a model turn · {result.ModelCount} model(s) available." }
            : result;
    }

    public async Task<ProviderTestResult> ListCodexSubscriptionAccountsAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var store = new FileOpenAICodexSubscriptionCredentialStore(GetProviderStateRootPath());
        var credential = await store.LoadAsync(definition.ProviderKey, cancellationToken);
        if (credential is null)
        {
            return new ProviderTestResult(false, "Login required before account/workspace metadata can be listed.", 0);
        }

        var accountId = OpenAICodexSubscriptionAuthManager.ResolveAccountId(definition.AccountId, credential);
        var accountLabel = string.IsNullOrWhiteSpace(credential.AccountLabel) ? "ChatGPT account/workspace" : credential.AccountLabel;
        var accountMessage = string.IsNullOrWhiteSpace(accountId)
            ? $"{accountLabel}: token did not expose an account/workspace id; enter one in Account/Workspace Id if required."
            : $"{accountLabel}: {accountId}";
        return new ProviderTestResult(true, accountMessage, string.IsNullOrWhiteSpace(accountId) ? 0 : 1);
    }

    public async Task<ProviderTestResult> LoginCopilotDirectWithBrowserAsync(
        CodeAltaProviderDocument definition,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(reportStatus);

        var manager = CreateCopilotDirectLoginManager(definition);
        var result = await manager.LoginWithDeviceCodeAsync(
            CreateCopilotDirectLoginOptions(definition),
            (deviceCode, _) =>
            {
                reportStatus($"Opening GitHub Copilot login in your browser. Enter code {deviceCode.UserCode} at {deviceCode.VerificationUri}. Waiting for authorization...");
                TryOpenBrowser(deviceCode.VerificationUri);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
        return new ProviderTestResult(true, FormatCopilotDirectLoginMessage("GitHub Copilot login completed", result), 0);
    }

    public async Task<ProviderTestResult> LoginCopilotDirectWithDeviceCodeAsync(
        CodeAltaProviderDocument definition,
        Action<string> reportStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(reportStatus);

        var manager = CreateCopilotDirectLoginManager(definition);
        var result = await manager.LoginWithDeviceCodeAsync(
            CreateCopilotDirectLoginOptions(definition),
            (deviceCode, _) =>
            {
                reportStatus($"Open {deviceCode.VerificationUri} and enter code {deviceCode.UserCode}. Waiting for GitHub Copilot authorization...");
                return ValueTask.CompletedTask;
            },
            cancellationToken);
        return new ProviderTestResult(true, FormatCopilotDirectLoginMessage("GitHub Copilot device login completed", result), 0);
    }

    public async Task<ProviderTestResult> LogoutCopilotDirectAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await CreateCopilotDirectLoginManager(definition)
            .DeleteCredentialAsync(CreateCopilotDirectLoginOptions(definition), cancellationToken);
        return new ProviderTestResult(true, "Deleted CodeAlta-owned GitHub Copilot direct credentials for this provider.", 0);
    }

    public async Task<ProviderTestResult> TestCopilotDirectAuthenticationAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var status = await CreateCopilotDirectLoginManager(definition)
            .GetCredentialStatusAsync(CreateCopilotDirectLoginOptions(definition), cancellationToken);
        return status is null
            ? new ProviderTestResult(false, "Login required before cached GitHub Copilot credentials can be used.", 0)
            : new ProviderTestResult(true, FormatCopilotDirectLoginMessage("Authenticated with cached GitHub Copilot credentials", status), 0);
    }

    public async Task<ProviderTestResult> ListCopilotDirectModelsAsync(
        CodeAltaProviderDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = await TestProviderAsync(definition, cancellationToken);
        return result.Success
            ? result with { Message = $"Copilot model listing completed without sending a model turn · {result.ModelCount} model(s) available." }
            : result;
    }

    private void PublishModelProviderCatalogChanged()
        => _frontendEvents.Publish(new ModelProviderCatalogChangedEvent());

    internal static bool TryBuildActiveBackendTestResult(
        CodeAltaProviderDocument definition,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        out ProviderTestResult result)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(chatBackendStates);

        result = default;
        if (!string.Equals(definition.ProviderType, "codex", StringComparison.Ordinal) &&
            !string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal))
        {
            return false;
        }

        if (!chatBackendStates.TryGetValue(definition.ProviderKey, out var state))
        {
            return false;
        }

        switch (state.Availability)
        {
            case ChatBackendAvailability.Ready:
                result = new ProviderTestResult(
                    true,
                    $"Using active provider backend · {state.Models.Count} model(s) discovered.",
                    state.Models.Count);
                return true;
            case ChatBackendAvailability.Connecting:
            case ChatBackendAvailability.Failed:
            case ChatBackendAvailability.Unsupported:
                result = new ProviderTestResult(false, state.StatusMessage, 0);
                return true;
            default:
                return false;
        }
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
            backend = new CopilotAgentBackend(
                CodeAltaOwnedServices.CreateCopilotBackendOptions(definition, Path.Combine(stateRootPath, "cache")));
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

    private static bool IsTemporarilyDisabledCopilotProvider(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return string.Equals(definition.ProviderKey, "copilot", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(definition.ProviderType, "copilot", StringComparison.OrdinalIgnoreCase);
    }

    private OpenAICodexSubscriptionLoginManager CreateCodexSubscriptionLoginManager(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, "openai-codex-subscription", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Select a Codex (ChatGPT subscription) provider first.");
        }

        return new OpenAICodexSubscriptionLoginManager(
            new FileOpenAICodexSubscriptionCredentialStore(GetProviderStateRootPath()),
            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
            definition.ProviderKey);
    }

    private OpenAICodexSubscriptionAuthManager CreateCodexSubscriptionAuthManager(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, "openai-codex-subscription", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Select a Codex (ChatGPT subscription) provider first.");
        }

        return new OpenAICodexSubscriptionAuthManager(
            new FileOpenAICodexSubscriptionCredentialStore(GetProviderStateRootPath()),
            new OpenAICodexSubscriptionOAuthClient(new HttpClient()),
            definition.ProviderKey,
            definition.AuthSource ?? "codealta_oauth",
            definition.AccountId,
            CodexAuthFileReader.ResolveCodexHome());
    }

    private static CopilotDirectLoginManager CreateCopilotDirectLoginManager(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, "github-copilot-direct", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Select a GitHub Copilot Direct provider first.");
        }

        return new CopilotDirectLoginManager(new HttpClient());
    }

    private CopilotDirectLoginOptions CreateCopilotDirectLoginOptions(CodeAltaProviderDocument definition)
        => new(
            definition.ProviderKey,
            GetProviderStateRootPath(),
            definition.GitHubEnterpriseUrl,
            TryCreateUri(definition.ApiUrl));

    private string GetProviderStateRootPath()
        => _ownedServices?.CatalogOptions.GlobalRoot
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");

    private static string FormatCodexCredentialMessage(string prefix, OpenAICodexSubscriptionCredential credential)
    {
        var account = string.IsNullOrWhiteSpace(credential.AccountId) ? "account/workspace unknown" : credential.AccountId;
        return $"{prefix} · account/workspace: {account}.";
    }

    private static string FormatCopilotDirectLoginMessage(string prefix, CopilotDirectLoginResult result)
    {
        var expiry = result.ExpiresAt is null ? "expiry unknown" : $"expires {result.ExpiresAt.Value.LocalDateTime:g}";
        var enterprise = string.IsNullOrWhiteSpace(result.EnterpriseDomain) ? "GitHub.com" : result.EnterpriseDomain.Trim();
        return $"{prefix} · {enterprise} · API {result.BaseUri} · {expiry}.";
    }

    private static Uri? TryCreateUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static void TryOpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Headless terminals can use the reported authorization URL instead.
        }
    }

    private void SyncModelProviderCatalog()
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
