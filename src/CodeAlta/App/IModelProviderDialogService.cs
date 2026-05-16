using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IModelProviderDialogService
{
    IReadOnlyList<CodeAltaProviderDocument> LoadDefinitions();

    Task SaveDefinitionsAsync(IReadOnlyList<CodeAltaProviderDocument> definitions);

    Task<ProviderTestResult> TestProviderAsync(CodeAltaProviderDocument definition);

    Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus);

    Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus);

    Task<ProviderTestResult> LogoutAsync(CodeAltaProviderDocument definition);

    Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition);

    Task<ProviderTestResult> ListModelsAsync(CodeAltaProviderDocument definition);

    Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition);
}

internal sealed class ModelProviderDialogService : IModelProviderDialogService
{
    private readonly ProviderFrontendCoordinator _providerUi;

    public ModelProviderDialogService(ProviderFrontendCoordinator providerUi)
    {
        ArgumentNullException.ThrowIfNull(providerUi);
        _providerUi = providerUi;
    }

    public IReadOnlyList<CodeAltaProviderDocument> LoadDefinitions()
        => _providerUi.LoadProviderDefinitions();

    public Task SaveDefinitionsAsync(IReadOnlyList<CodeAltaProviderDocument> definitions)
        => _providerUi.SaveProviderDefinitionsAsync(definitions, CancellationToken.None);

    public Task<ProviderTestResult> TestProviderAsync(CodeAltaProviderDocument definition)
        => _providerUi.TestProviderAsync(definition, CancellationToken.None);

    public Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus)
        => string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal)
            ? _providerUi.LoginCopilotDirectWithBrowserAsync(definition, reportStatus, CancellationToken.None)
            : _providerUi.LoginCodexSubscriptionWithBrowserAsync(definition, reportStatus, CancellationToken.None);

    public Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus)
        => string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal)
            ? _providerUi.LoginCopilotDirectWithDeviceCodeAsync(definition, reportStatus, CancellationToken.None)
            : _providerUi.LoginCodexSubscriptionWithDeviceCodeAsync(definition, reportStatus, CancellationToken.None);

    public Task<ProviderTestResult> LogoutAsync(CodeAltaProviderDocument definition)
        => string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal)
            ? _providerUi.LogoutCopilotDirectAsync(definition, CancellationToken.None)
            : _providerUi.LogoutCodexSubscriptionAsync(definition, CancellationToken.None);

    public Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition)
        => string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal)
            ? _providerUi.TestCopilotDirectAuthenticationAsync(definition, CancellationToken.None)
            : _providerUi.TestCodexSubscriptionAuthenticationAsync(definition, CancellationToken.None);

    public Task<ProviderTestResult> ListModelsAsync(CodeAltaProviderDocument definition)
        => string.Equals(definition.ProviderType, "copilot", StringComparison.Ordinal)
            ? _providerUi.ListCopilotDirectModelsAsync(definition, CancellationToken.None)
            : _providerUi.ListCodexSubscriptionModelsAsync(definition, CancellationToken.None);

    public Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition)
        => _providerUi.ListCodexSubscriptionAccountsAsync(definition, CancellationToken.None);
}
