using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

internal sealed partial class ModelProviderEditorItemViewModel
{
    private readonly CodeAltaProviderDocument _source;
    private Action? _editedCallback;
    private bool _isInitialized;

    private ModelProviderEditorItemViewModel(CodeAltaProviderDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        ProviderKey = source.ProviderKey;
        Enabled = source.Enabled != false;
        ProviderType = source.ProviderType ?? "openai-chat";
        DisplayName = source.DisplayName;
        UseDefaultDisplayName = source.DisplayName is null;
        Model = source.Model;
        UseDefaultModel = source.Model is null;
        ReasoningEffort = source.ReasoningEffort ?? "high";
        UseDefaultReasoningEffort = source.ReasoningEffort is null;
        ApiKey = source.ApiKey;
        UseDefaultApiKey = source.ApiKey is null;
        ApiKeyEnv = source.ApiKeyEnv;
        UseDefaultApiKeyEnv = source.ApiKeyEnv is null;
        ApiUrl = source.ApiUrl;
        UseDefaultApiUrl = source.ApiUrl is null;
        GitHubEnterpriseUrl = source.GitHubEnterpriseUrl;
        UseDefaultGitHubEnterpriseUrl = source.GitHubEnterpriseUrl is null;
        OrganizationId = source.OrganizationId;
        UseDefaultOrganizationId = source.OrganizationId is null;
        ProjectId = source.ProjectId;
        UseDefaultProjectId = source.ProjectId is null;
        Project = source.Project;
        UseDefaultProject = source.Project is null;
        Location = source.Location;
        UseDefaultLocation = source.Location is null;
        ModelsDevProviderId = source.ModelsDevProviderId;
        UseDefaultModelsDevProviderId = source.ModelsDevProviderId is null;
        SingleModelId = source.SingleModelId;
        UseDefaultSingleModelId = source.SingleModelId is null;
        AuthSource = source.AuthSource ?? "codealta_oauth";
        UseDefaultAuthSource = source.AuthSource is null;
        AccountId = source.AccountId;
        UseDefaultAccountId = source.AccountId is null;
        ModelDiscovery = source.ModelDiscovery ?? "static";
        UseDefaultModelDiscovery = source.ModelDiscovery is null;
        ResponseTransport = source.ResponseTransport ?? "websocket_with_http_fallback";
        UseDefaultResponseTransport = source.ResponseTransport is null;
        _isInitialized = true;
    }

    [Bindable]
    public partial ModelProviderLastTestState LastTestState { get; private set; }

    [Bindable]
    public partial string? LastTestMessage { get; private set; }

    [Bindable]
    public partial string? ProviderKey { get; set; }

    public bool IsReserved => false;

    [Bindable]
    public partial bool Enabled { get; set; }

    [Bindable]
    public partial string ProviderType { get; set; }

    [Bindable]
    public partial string? DisplayName { get; set; }

    [Bindable]
    public partial bool UseDefaultDisplayName { get; set; }

    [Bindable]
    public partial string? Model { get; set; }

    [Bindable]
    public partial bool UseDefaultModel { get; set; }

    [Bindable]
    public partial string? ReasoningEffort { get; set; }

    [Bindable]
    public partial bool UseDefaultReasoningEffort { get; set; }

    [Bindable]
    public partial string? ApiKey { get; set; }

    [Bindable]
    public partial bool UseDefaultApiKey { get; set; }

    [Bindable]
    public partial string? ApiKeyEnv { get; set; }

    [Bindable]
    public partial bool UseDefaultApiKeyEnv { get; set; }

    [Bindable]
    public partial string? ApiUrl { get; set; }

    [Bindable]
    public partial bool UseDefaultApiUrl { get; set; }

    [Bindable]
    public partial string? GitHubEnterpriseUrl { get; set; }

    [Bindable]
    public partial bool UseDefaultGitHubEnterpriseUrl { get; set; }

    [Bindable]
    public partial string? OrganizationId { get; set; }

    [Bindable]
    public partial bool UseDefaultOrganizationId { get; set; }

    [Bindable]
    public partial string? ProjectId { get; set; }

    [Bindable]
    public partial bool UseDefaultProjectId { get; set; }

    [Bindable]
    public partial string? Project { get; set; }

    [Bindable]
    public partial bool UseDefaultProject { get; set; }

    [Bindable]
    public partial string? Location { get; set; }

    [Bindable]
    public partial bool UseDefaultLocation { get; set; }

    [Bindable]
    public partial string? ModelsDevProviderId { get; set; }

    [Bindable]
    public partial bool UseDefaultModelsDevProviderId { get; set; }

    [Bindable]
    public partial string? SingleModelId { get; set; }

    [Bindable]
    public partial bool UseDefaultSingleModelId { get; set; }

    [Bindable]
    public partial string? AuthSource { get; set; }

    [Bindable]
    public partial bool UseDefaultAuthSource { get; set; }

    [Bindable]
    public partial string? AccountId { get; set; }

    [Bindable]
    public partial bool UseDefaultAccountId { get; set; }

    [Bindable]
    public partial string? ModelDiscovery { get; set; }

    [Bindable]
    public partial bool UseDefaultModelDiscovery { get; set; }

    [Bindable]
    public partial string? ResponseTransport { get; set; }

    [Bindable]
    public partial bool UseDefaultResponseTransport { get; set; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ProviderKey ?? string.Empty : DisplayName.Trim();

    public static ModelProviderEditorItemViewModel FromDocument(CodeAltaProviderDocument definition)
        => new(Clone(definition));

    public static ModelProviderEditorItemViewModel Create(string providerKey)
        => new(new CodeAltaProviderDocument
        {
            ProviderKey = providerKey,
            Enabled = false,
            ProviderType = "openai-chat",
        });

    public void SetEditedCallback(Action? editedCallback)
        => _editedCallback = editedCallback;

    public CodeAltaProviderDocument ToDocument()
    {
        var definition = Clone(_source);
        definition.ProviderKey = (ProviderKey ?? string.Empty).Trim().ToLowerInvariant();
        definition.Enabled = Enabled;
        definition.ProviderType = ProviderType;
        definition.DisplayName = UseDefaultDisplayName ? null : NormalizeText(DisplayName);
        definition.Model = UseDefaultModel ? null : NormalizeText(Model);
        definition.ReasoningEffort = UseDefaultReasoningEffort ? null : NormalizeText(ReasoningEffort);
        var supportsApiKeyField = ProviderType is "openai-chat" or "openai-responses" or "azure-openai" or "anthropic" or "google-genai";
        definition.ApiKey = supportsApiKeyField && !UseDefaultApiKey ? NormalizeText(ApiKey) : null;
        definition.ApiKeyEnv = supportsApiKeyField && !UseDefaultApiKeyEnv ? NormalizeText(ApiKeyEnv) : null;
        definition.ApiUrl = UseDefaultApiUrl ? null : NormalizeText(ApiUrl);
        definition.GitHubEnterpriseUrl = ProviderType == "copilot" && !UseDefaultGitHubEnterpriseUrl ? NormalizeText(GitHubEnterpriseUrl) : null;
        definition.OrganizationId = UseDefaultOrganizationId ? null : NormalizeText(OrganizationId);
        definition.ProjectId = UseDefaultProjectId ? null : NormalizeText(ProjectId);
        definition.Project = UseDefaultProject ? null : NormalizeText(Project);
        definition.Location = UseDefaultLocation ? null : NormalizeText(Location);
        definition.ModelsDevProviderId = UseDefaultModelsDevProviderId ? null : NormalizeText(ModelsDevProviderId);
        definition.SingleModelId = UseDefaultSingleModelId ? null : NormalizeText(SingleModelId);
        var usesAuthSourceField = ProviderType is "codex" or "copilot" or "xai";
        var usesModelDiscoveryField = ProviderType is "codex" or "copilot" or "xai";
        definition.AuthSource = usesAuthSourceField && !UseDefaultAuthSource ? NormalizeText(AuthSource) : null;
        definition.AccountId = ProviderType == "codex" && !UseDefaultAccountId ? NormalizeText(AccountId) : null;
        definition.ModelDiscovery = usesModelDiscoveryField && !UseDefaultModelDiscovery ? NormalizeText(ModelDiscovery) : null;
        definition.ResponseTransport = ProviderType == "codex" && !UseDefaultResponseTransport ? NormalizeText(ResponseTransport) : null;
        definition.Experimental = null;
        return definition;
    }

    public bool ClearTestResult()
    {
        if (LastTestState == ModelProviderLastTestState.None && LastTestMessage is null)
        {
            return false;
        }

        LastTestState = ModelProviderLastTestState.None;
        LastTestMessage = null;
        return true;
    }

    public void SetTestResult(bool success, string? message)
    {
        LastTestState = success ? ModelProviderLastTestState.Success : ModelProviderLastTestState.Failed;
        LastTestMessage = NormalizeText(message);
    }

    public bool SetSuccessfulResultAndEnable(string? message)
    {
        var enabledChanged = !Enabled;
        if (enabledChanged)
        {
            Enabled = true;
        }

        SetTestResult(success: true, message);
        return enabledChanged;
    }

    partial void OnProviderKeyChanged(string? value) => ClearTestResultOnEdit();
    partial void OnEnabledChanged(bool value) => ClearTestResultOnEdit();
    partial void OnProviderTypeChanged(string value) => ClearTestResultOnEdit();
    partial void OnDisplayNameChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultDisplayNameChanged(bool value) => ClearTestResultOnEdit();
    partial void OnModelChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultModelChanged(bool value) => ClearTestResultOnEdit();
    partial void OnReasoningEffortChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultReasoningEffortChanged(bool value) => ClearTestResultOnEdit();
    partial void OnApiKeyChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultApiKeyChanged(bool value) => ClearTestResultOnEdit();
    partial void OnApiKeyEnvChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultApiKeyEnvChanged(bool value) => ClearTestResultOnEdit();
    partial void OnApiUrlChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultApiUrlChanged(bool value) => ClearTestResultOnEdit();
    partial void OnGitHubEnterpriseUrlChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultGitHubEnterpriseUrlChanged(bool value) => ClearTestResultOnEdit();
    partial void OnOrganizationIdChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultOrganizationIdChanged(bool value) => ClearTestResultOnEdit();
    partial void OnProjectIdChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultProjectIdChanged(bool value) => ClearTestResultOnEdit();
    partial void OnProjectChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultProjectChanged(bool value) => ClearTestResultOnEdit();
    partial void OnLocationChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultLocationChanged(bool value) => ClearTestResultOnEdit();
    partial void OnModelsDevProviderIdChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultModelsDevProviderIdChanged(bool value) => ClearTestResultOnEdit();
    partial void OnSingleModelIdChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultSingleModelIdChanged(bool value) => ClearTestResultOnEdit();
    partial void OnAuthSourceChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultAuthSourceChanged(bool value) => ClearTestResultOnEdit();
    partial void OnAccountIdChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultAccountIdChanged(bool value) => ClearTestResultOnEdit();
    partial void OnModelDiscoveryChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultModelDiscoveryChanged(bool value) => ClearTestResultOnEdit();
    partial void OnResponseTransportChanged(string? value) => ClearTestResultOnEdit();
    partial void OnUseDefaultResponseTransportChanged(bool value) => ClearTestResultOnEdit();
    private static CodeAltaProviderDocument Clone(CodeAltaProviderDocument definition)
    {
        return new CodeAltaProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ProviderType = definition.ProviderType,
            Model = definition.Model,
            ReasoningEffort = definition.ReasoningEffort,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            ApiUrl = definition.ApiUrl,
            GitHubEnterpriseUrl = definition.GitHubEnterpriseUrl,
            GitHubTokenEnv = definition.GitHubTokenEnv,
            CopilotTokenEnv = definition.CopilotTokenEnv,
            EnableModelPolicies = definition.EnableModelPolicies,
            IncludePreviewModels = definition.IncludePreviewModels,
            ProtocolTrace = definition.ProtocolTrace,
            OrganizationId = definition.OrganizationId,
            ProjectId = definition.ProjectId,
            Project = definition.Project,
            Location = definition.Location,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            ExtraBody = definition.ExtraBody,
            Profile = definition.Profile,
            Compaction = definition.Compaction,
            ModelOverrides = definition.ModelOverrides,
            AuthSource = definition.AuthSource,
            AccountId = definition.AccountId,
            MaxConcurrentRequests = definition.MaxConcurrentRequests,
            TextVerbosity = definition.TextVerbosity,
            IncludeEncryptedReasoning = definition.IncludeEncryptedReasoning,
            ModelDiscovery = definition.ModelDiscovery,
            ResponseTransport = definition.ResponseTransport,
            SendResponsesBetaHeader = definition.SendResponsesBetaHeader,
            SendInstallationId = definition.SendInstallationId,
            InstallationIdSource = definition.InstallationIdSource,
            Experimental = definition.Experimental,
        };
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ClearTestResultOnEdit()
    {
        if (!_isInitialized)
        {
            return;
        }

        ClearTestResult();
        _editedCallback?.Invoke();
    }
}
