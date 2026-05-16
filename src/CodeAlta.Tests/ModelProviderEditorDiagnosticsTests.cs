using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderEditorDiagnosticsTests
{
    [TestMethod]
    public void Analyze_EnabledProviderWithEmptyApiKeyEnv_ShowsMissingCredentialsAndEnvWarning()
    {
        var envName = $"CODEALTA_TEST_{Guid.NewGuid():N}";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, null);
            var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
            {
                ProviderKey = "openai",
                Enabled = true,
                ProviderType = "openai-responses",
                ApiKeyEnv = envName,
            });

            var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

            Assert.AreEqual(ModelProviderUiStatusKind.Error, snapshot.StatusKind);
            Assert.AreEqual("Missing credentials", snapshot.StatusText);
            Assert.IsTrue(snapshot.Entries.Any(static entry =>
                entry.Severity == ValidationSeverity.Error &&
                entry.Message.Contains("Enter an API key", StringComparison.Ordinal)));
            Assert.IsTrue(snapshot.Entries.Any(entry =>
                entry.Severity == ValidationSeverity.Warning &&
                entry.Message.Contains(envName, StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [TestMethod]
    public void Analyze_EnabledUntestedProvider_UsesConfiguredStatusInsteadOfSuccess()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            Enabled = true,
            ProviderType = "openai-responses",
            ApiKey = "secret",
        });

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Configured, snapshot.StatusKind);
        Assert.AreEqual("Ready to test", snapshot.StatusText);
    }

    [TestMethod]
    public void Analyze_LastSuccessfulTest_UsesSuccessStatus()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            Enabled = true,
            ProviderType = "openai-responses",
            ApiKey = "secret",
        });
        item.SetTestResult(success: true, "Connected successfully · 5 model(s) discovered.");

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Success, snapshot.StatusKind);
        Assert.AreEqual("Tested successfully", snapshot.StatusText);
        Assert.IsTrue(snapshot.Entries.Any(static entry =>
            entry.Message.Contains("Last test succeeded", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Analyze_CodexSubscriptionProvider_UsesProviderSpecificStatusStates()
    {
        var cases = new (string? Failure, string ExpectedStatus)[]
        {
            (null, "Ready"),
            ("ChatGPT login is required for the Codex subscription provider.", "Login required"),
            ("Token expired; refresh token is available.", "Token expired; refresh available"),
            ("ChatGPT/Codex account, workspace, plan, or policy does not allow this request.", "Account/workspace selection required"),
            ("ChatGPT/Codex rate limit or quota was reached.", "Rate or quota limited"),
            ("ChatGPT/Codex response stream did not match the expected protocol.", "Unsupported backend/protocol drift"),
        };

        foreach (var testCase in cases)
        {
            var item = CreateCodexSubscriptionItem(enabled: true, experimental: true);
            if (testCase.Failure is not null)
            {
                item.SetTestResult(success: false, testCase.Failure);
            }

            var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

            Assert.AreEqual(testCase.ExpectedStatus, snapshot.StatusText);
        }

        var notConfigured = CreateCodexSubscriptionItem(enabled: false, experimental: false);
        var notConfiguredSnapshot = ModelProviderEditorDiagnostics.Analyze(notConfigured, [notConfigured]);

        Assert.AreEqual(ModelProviderUiStatusKind.Disabled, notConfiguredSnapshot.StatusKind);
        Assert.AreEqual(
            "Not configured",
            notConfiguredSnapshot.StatusText);
        Assert.IsFalse(notConfiguredSnapshot.Entries.Any(static entry => entry.Severity == ValidationSeverity.Error));
    }

    [TestMethod]
    public void Analyze_CodexSubscriptionExperimentalAdvisory_DoesNotWarnInProviderStatus()
    {
        var item = CreateCodexSubscriptionItem(enabled: true, experimental: true);

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Configured, snapshot.StatusKind);
        Assert.AreEqual("Ready", snapshot.StatusText);
        Assert.IsTrue(snapshot.Entries.Any(static entry =>
            entry.Severity == ValidationSeverity.Warning &&
            entry.Message.Contains("Experimental ChatGPT/Codex subscription access", StringComparison.Ordinal)));

        item.SetTestResult(success: true, "Connected successfully · 5 model(s) discovered.");
        var testedSnapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Success, testedSnapshot.StatusKind);
        Assert.AreEqual("Tested successfully", testedSnapshot.StatusText);
    }

    [TestMethod]
    public void Analyze_CodexSubscriptionActionFailure_IsWarningNotError()
    {
        var item = CreateCodexSubscriptionItem(enabled: true, experimental: true);
        item.SetTestResult(success: false, "Codex model discovery failed with HTTP 403.");

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Warning, snapshot.StatusKind);
        Assert.AreEqual("Last action needs review", snapshot.StatusText);
        Assert.IsFalse(snapshot.Entries.Any(static entry => entry.Severity == ValidationSeverity.Error));
        Assert.IsTrue(snapshot.Entries.Any(static entry =>
            entry.Severity == ValidationSeverity.Warning &&
            entry.Message.Contains("Last test failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Analyze_NonCodexProviderValidation_RemainsUnchanged()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            Enabled = true,
            ProviderType = "openai-responses",
            ApiKey = null,
        });

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.AreEqual(ModelProviderUiStatusKind.Error, snapshot.StatusKind);
        Assert.AreEqual("Missing credentials", snapshot.StatusText);
    }

    [TestMethod]
    public void Analyze_ReservedCliProviders_CanUseBuiltInCliProviderTypes()
    {
        var codex = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "codex_cli",
            Enabled = true,
            ProviderType = "codex_cli",
        });
        var copilot = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "copilot_cli",
            Enabled = true,
            ProviderType = "copilot_cli",
        });

        var codexSnapshot = ModelProviderEditorDiagnostics.Analyze(codex, [codex, copilot]);
        var copilotSnapshot = ModelProviderEditorDiagnostics.Analyze(copilot, [codex, copilot]);

        Assert.IsTrue(codex.IsReserved);
        Assert.IsTrue(copilot.IsReserved);
        Assert.IsFalse(codexSnapshot.Entries.Any(static entry => entry.Message.Contains("built-in CLI provider types", StringComparison.Ordinal)));
        Assert.IsFalse(copilotSnapshot.Entries.Any(static entry => entry.Message.Contains("built-in CLI provider types", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Analyze_CustomProvider_CannotUseBuiltInCliProviderType()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "custom-codex",
            Enabled = true,
            ProviderType = "codex_cli",
        });

        var snapshot = ModelProviderEditorDiagnostics.Analyze(item, [item]);

        Assert.IsFalse(item.IsReserved);
        Assert.AreEqual(ModelProviderUiStatusKind.Error, snapshot.StatusKind);
        Assert.IsTrue(snapshot.Entries.Any(static entry =>
            entry.Severity == ValidationSeverity.Error &&
            entry.Message.Contains("reserved codex_cli/copilot_cli entries", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CodexSubscriptionEditor_RoundTripsAccountAuthAndModelDiscoveryFields()
    {
        var item = CreateCodexSubscriptionItem(enabled: true, experimental: true);
        item.UseDefaultAuthSource = false;
        item.AuthSource = "codex_auth_import";
        item.UseDefaultAccountId = false;
        item.AccountId = "acct_123";
        item.UseDefaultModelDiscovery = false;
        item.ModelDiscovery = "static";
        item.UseDefaultResponseTransport = false;
        item.ResponseTransport = "http";
        item.SetTestResult(success: false, "account selection required");

        var definition = item.ToDocument();

        Assert.AreEqual("codex_auth_import", definition.AuthSource);
        Assert.AreEqual("acct_123", definition.AccountId);
        Assert.AreEqual("static", definition.ModelDiscovery);
        Assert.AreEqual("http", definition.ResponseTransport);
        Assert.AreEqual(true, definition.Experimental);

        item.AccountId = "acct_456";
        Assert.AreEqual(ModelProviderLastTestState.None, item.LastTestState);
        Assert.IsNull(item.LastTestMessage);
    }

    private static ModelProviderEditorItemViewModel CreateCodexSubscriptionItem(bool enabled, bool experimental)
        => ModelProviderEditorItemViewModel.FromDocument(new CodeAlta.Catalog.CodeAltaProviderDocument
        {
            ProviderKey = "codex",
            Enabled = enabled,
            ProviderType = "codex",
            Experimental = experimental,
        });
}
