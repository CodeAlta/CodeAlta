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
    public void ValidateGlobalConfigContent_LegacyKeyNamedInAString_IsNotALegacyConfig()
    {
        // The prompt of an automation is free text: naming an old key in it does not make the file an old one.
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """"
            [providers.openai]
            type = "openai-chat"
            api_url = "https://api.openai.com/v1"
            display_name = 'is_default = "true"'

            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77]
            name = "Check \"wire_api = x\" lines"
            prompt = '''
            [backends.openai]
            provider = "openai"
            base_uri = "https://example.invalid"
            '''
            notes = """
            provider = "it's quoted"
            """
            """",
            "config.toml");

        Assert.IsTrue(result.IsValid, result.Message);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_LegacyKeyAfterAString_IsStillReported()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """"
            [chat]
            note = "it's a # sign, not a comment"
            description = """
            two lines
            """
            wire_api = "responses"
            """",
            "config.toml");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "Legacy CodeAlta config keys");
        Assert.AreEqual(6, result.Line);
        Assert.AreEqual(1, result.Column);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_InvalidProvider_ReturnsInvalidWithoutStartingProviders()
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
