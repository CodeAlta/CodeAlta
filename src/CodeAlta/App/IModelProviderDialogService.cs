using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.App;

internal interface IModelProviderDialogService
{
    string ConfigurationPath { get; }

    IReadOnlyList<CodeAltaProviderDocument> LoadDefinitions();

    string LoadConfigurationContent();

    CodeAltaConfigValidationResult ValidateConfigurationContent(string? content);

    Task<ProviderConfigurationSaveResult> SaveConfigurationContentAsync(string? content, CancellationToken cancellationToken = default);

    Task<ProviderConfigurationSaveResult> SaveDefinitionsAsync(IReadOnlyList<CodeAltaProviderDocument> definitions, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> TestProviderAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> LogoutAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> ListModelsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);

    Task<ProviderModelListResult> ListSelectableModelsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);

    Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default);
}

internal readonly record struct ProviderModelListResult(bool Success, string Message, IReadOnlyList<AgentModelInfo> Models)
{
    public int ModelCount => Models.Count;
}

internal readonly record struct ProviderConfigurationSaveResult(bool RuntimeRefreshSucceeded, string? RuntimeRefreshErrorMessage)
{
    public static ProviderConfigurationSaveResult Success { get; } = new(true, null);

    public static ProviderConfigurationSaveResult RuntimeRefreshFailed(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ProviderConfigurationSaveResult(false, message);
    }
}

internal sealed class ModelProviderDialogService : IModelProviderDialogService
{
    private readonly ProviderFrontendCoordinator _providerUi;

    public ModelProviderDialogService(ProviderFrontendCoordinator providerUi)
    {
        ArgumentNullException.ThrowIfNull(providerUi);
        _providerUi = providerUi;
    }

    public string ConfigurationPath => _providerUi.ConfigurationPath;

    public IReadOnlyList<CodeAltaProviderDocument> LoadDefinitions()
        => _providerUi.LoadProviderDefinitions();

    public string LoadConfigurationContent()
        => _providerUi.LoadProviderConfigurationContent();

    public CodeAltaConfigValidationResult ValidateConfigurationContent(string? content)
        => _providerUi.ValidateProviderConfigurationContent(content);

    public Task<ProviderConfigurationSaveResult> SaveConfigurationContentAsync(string? content, CancellationToken cancellationToken = default)
        => _providerUi.SaveProviderConfigurationContentAsync(content, cancellationToken);

    public Task<ProviderConfigurationSaveResult> SaveDefinitionsAsync(IReadOnlyList<CodeAltaProviderDocument> definitions, CancellationToken cancellationToken = default)
        => _providerUi.SaveProviderDefinitionsAsync(definitions, cancellationToken);

    public Task<ProviderTestResult> TestProviderAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => _providerUi.TestProviderAsync(definition, cancellationToken);

    public Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default)
        => definition.ProviderType switch
        {
            "copilot" => _providerUi.LoginCopilotDirectWithBrowserAsync(definition, reportStatus, cancellationToken),
            "xai" => _providerUi.LoginXaiDirectWithBrowserAsync(definition, reportStatus, cancellationToken),
            _ => _providerUi.LoginCodexSubscriptionWithBrowserAsync(definition, reportStatus, cancellationToken),
        };

    public Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus, CancellationToken cancellationToken = default)
        => definition.ProviderType switch
        {
            "copilot" => _providerUi.LoginCopilotDirectWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
            "xai" => _providerUi.LoginXaiDirectWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
            _ => _providerUi.LoginCodexSubscriptionWithDeviceCodeAsync(definition, reportStatus, cancellationToken),
        };

    public Task<ProviderTestResult> LogoutAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => definition.ProviderType switch
        {
            "copilot" => _providerUi.LogoutCopilotDirectAsync(definition, cancellationToken),
            "xai" => _providerUi.LogoutXaiDirectAsync(definition, cancellationToken),
            _ => _providerUi.LogoutCodexSubscriptionAsync(definition, cancellationToken),
        };

    public Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => definition.ProviderType switch
        {
            "copilot" => _providerUi.TestCopilotDirectAuthenticationAsync(definition, cancellationToken),
            "xai" => _providerUi.TestXaiDirectAuthenticationAsync(definition, cancellationToken),
            _ => _providerUi.TestCodexSubscriptionAuthenticationAsync(definition, cancellationToken),
        };

    public Task<ProviderTestResult> ListModelsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => definition.ProviderType switch
        {
            "copilot" => _providerUi.ListCopilotDirectModelsAsync(definition, cancellationToken),
            "xai" => _providerUi.ListXaiDirectModelsAsync(definition, cancellationToken),
            _ => _providerUi.ListCodexSubscriptionModelsAsync(definition, cancellationToken),
        };

    public Task<ProviderModelListResult> ListSelectableModelsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => _providerUi.ListProviderModelsAsync(definition, cancellationToken);

    public Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken = default)
        => _providerUi.ListCodexSubscriptionAccountsAsync(definition, cancellationToken);
}
