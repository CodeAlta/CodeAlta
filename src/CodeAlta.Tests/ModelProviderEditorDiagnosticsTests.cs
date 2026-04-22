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
}
