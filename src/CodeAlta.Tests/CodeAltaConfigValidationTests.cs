using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigValidationTests
{
    [TestMethod]
    public void ValidateGlobalConfigContent_ValidConfig_ReturnsValid()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"
            api_url = "https://api.openai.com/v1"
            """,
            "config.toml");

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Message);
        Assert.IsNull(result.Line);
        Assert.IsNull(result.Column);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_SyntaxError_ReturnsDiagnosticLocation()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.openai]
            type = "openai-chat"
            api_url = "https://api.openai.com/v1
            """,
            "config.toml");

        Assert.IsFalse(result.IsValid);
        Assert.IsNotNull(result.Message);
        Assert.IsNotNull(result.Line);
        Assert.IsNotNull(result.Column);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_LegacyConfig_ReturnsMarkerLocation()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [chat]
            default_provider = "openai"

            [backends.openai]
            provider = "openai"
            """,
            "config.toml");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "Legacy CodeAlta config keys");
        Assert.AreEqual(4, result.Line);
        Assert.AreEqual(1, result.Column);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_InvalidProvider_ReturnsInvalidWithoutStartingBackends()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.custom]
            type = "unknown"
            """,
            "config.toml");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "providers.custom type must be one of");
    }
}
