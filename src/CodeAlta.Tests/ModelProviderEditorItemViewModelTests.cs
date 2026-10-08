using CodeAlta.Catalog;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderEditorItemViewModelTests
{
    [TestMethod]
    public void SetSuccessfulResultAndEnable_EnablesDisabledProviderAndStoresSuccess()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            ProviderType = "openai-responses",
            Enabled = false,
        });

        var changed = item.SetSuccessfulResultAndEnable("Connected successfully.");

        Assert.IsTrue(changed);
        Assert.IsTrue(item.Enabled);
        Assert.AreEqual(ModelProviderLastTestState.Success, item.LastTestState);
        Assert.AreEqual("Connected successfully.", item.LastTestMessage);
    }

    [TestMethod]
    public void SetSuccessfulResultAndEnable_PreservesAlreadyEnabledProviderAndStoresSuccess()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "copilot",
            ProviderType = "copilot",
            Enabled = true,
        });

        var changed = item.SetSuccessfulResultAndEnable("Login completed.");

        Assert.IsFalse(changed);
        Assert.IsTrue(item.Enabled);
        Assert.AreEqual(ModelProviderLastTestState.Success, item.LastTestState);
        Assert.AreEqual("Login completed.", item.LastTestMessage);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("default")]
    [DataRow("priority")]
    public void ToDocument_PreservesCodexServiceTier(string? tier)
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ServiceTier = tier,
        });

        Assert.AreEqual(tier, item.ToDocument().ServiceTier);
    }

    [TestMethod]
    public void ToDocument_KeepsTheIconAndTheColorOfTheProvider_WhichTheEditorDoesNotShow()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "team",
            ProviderType = "openai-chat",
            Icon = "mistral",
            Color = "#FA520F",
        });
        item.UseDefaultModel = false;
        item.Model = "model-b";

        var definition = item.ToDocument();

        Assert.AreEqual(("mistral", "#FA520F", "model-b"), (definition.Icon, definition.Color, definition.Model));
    }

    [TestMethod]
    public void ToDocument_PreservesConfiguredModelSorting()
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(new CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            ProviderType = "openai-responses",
            SortModels = true,
        });

        var definition = item.ToDocument();

        Assert.IsTrue(item.SortModels);
        Assert.AreEqual(true, definition.SortModels);
    }
}
