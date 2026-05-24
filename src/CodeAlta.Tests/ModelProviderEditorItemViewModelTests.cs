using CodeAlta.Catalog;
using CodeAlta.ViewModels;

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
}
