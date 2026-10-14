using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ModelTierSettingsTests
{
    [TestMethod]
    public void Defaults_HaveNoTierAndFallbackOff()
    {
        Assert.AreSame(ModelSettings.Default.Tiers, ModelSettings.Read(null).Tiers);
        Assert.AreEqual(ModelFallbackSettings.Default, ModelSettings.Read(null).Fallback);
        Assert.IsFalse(ModelFallbackSettings.Default.Enabled);
        Assert.IsTrue(ModelSettings.Read(new CodeAltaModelSettingsDocument()).Tiers.IsEmpty);
        WithStore(store => Assert.IsTrue(store.LoadGlobalModelSettings().Tiers.IsEmpty));
    }

    [TestMethod]
    public void Target_ParsesProviderModelAndOrderedReasoning()
    {
        Assert.IsTrue(ModelTierTarget.TryParse(" Claude-Code:opus@high, xhigh ", out var target, out var error), error);
        Assert.AreEqual("claude-code", target.ProviderKey);
        Assert.AreEqual("opus", target.ModelId);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.High, AgentReasoningEffort.XHigh }, target.Reasoning.ToArray());
        Assert.AreEqual(AgentReasoningEffort.High, target.PreferredReasoning);
        Assert.AreEqual("claude-code:opus@high,xhigh", target.Format());

        Assert.IsTrue(ModelTierTarget.TryParse("ollama:llama3:8b", out target, out _));
        Assert.AreEqual("llama3:8b", target.ModelId);
        Assert.IsNull(target.PreferredReasoning);
        Assert.AreEqual("ollama:llama3:8b", target.Format());
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow(":gpt-6.1")]
    [DataRow("codex:")]
    [DataRow("codex:@high")]
    [DataRow("codex:gpt-6.1@")]
    [DataRow("codex:gpt-6.1@huge")]
    [DataRow("codex:gpt-6.1@high,high")]
    public void Target_RefusesWhatIsNotATarget(string value)
    {
        Assert.IsFalse(ModelTierTarget.TryParse(value, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void Validate_NamesTheSettingOfEachError()
    {
        var errors = ModelSettings.Validate(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument
            {
                Complex = ["codex:gpt-6.1@xhigh", "codex:gpt-6.1@high"],
                Quick = ["claude-code:haiku@fast"],
            },
            Fallback = new CodeAltaModelFallbackDocument { On = ["quota", "tests"] },
        });

        Assert.HasCount(3, errors);
        StringAssert.StartsWith(errors[0], "models.tiers.complex[2]:");
        StringAssert.Contains(errors[0], "listed twice");
        StringAssert.StartsWith(errors[1], "models.tiers.quick[1]:");
        StringAssert.Contains(errors[1], "'fast' is not a reasoning effort");
        StringAssert.StartsWith(errors[2], "models.fallback.on:");
        Assert.Throws<InvalidDataException>(() => ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Standard = ["nothing"] },
        }));
    }

    [TestMethod]
    public void FindTierOf_TakesTheTierWhereTheModelComesFirst()
    {
        var tiers = Read(
            complex: ["claude-code:opus@high", "codex:gpt-6.1@xhigh"],
            standard: ["codex:gpt-6.1@medium", "claude-code:opus@medium"],
            quick: ["claude-code:haiku@low"]);

        Assert.AreEqual(ModelTier.Standard, tiers.FindTierOf("codex", "gpt-6.1"));
        Assert.AreEqual(ModelTier.Complex, tiers.FindTierOf("CLAUDE-CODE", "opus"));
        Assert.AreEqual(ModelTier.Quick, tiers.FindTierOf("claude-code", "haiku"));
        Assert.IsNull(tiers.FindTierOf("copilot", "gpt-6.1"));
    }

    [TestMethod]
    public void Config_RoundTripsTiersAndFallback_PreservesUnknownKeys_OmitsDefaults()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.ConfigPath, "[models]\nfuture = 1\n[custom]\nkeep = 'yes'\n");
            var settings = new ModelSettings(
                Read(complex: ["claude-code:opus@high,xhigh", "codex:gpt-6.1@xhigh"], standard: [], quick: ["claude-code:haiku@low"]),
                new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: false));
            store.SaveGlobalModelSettings(settings);

            var content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "\"claude-code:opus@high,xhigh\"");
            StringAssert.Contains(content, "enabled = true");
            StringAssert.Contains(content, "on = [\"quota\"]");
            Assert.DoesNotContain("standard", content);
            var loaded = store.LoadGlobalModelSettings();
            CollectionAssert.AreEqual(settings.Tiers.Complex.ToArray(), loaded.Tiers.Complex.ToArray());
            CollectionAssert.AreEqual(settings.Tiers.Quick.ToArray(), loaded.Tiers.Quick.ToArray());
            Assert.AreEqual(settings.Fallback, loaded.Fallback);

            store.SaveGlobalModelSettings(ModelSettings.Default);
            content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "future = 1");
            StringAssert.Contains(content, "keep =");
            Assert.DoesNotContain("tiers", content);
            Assert.DoesNotContain("fallback", content);
        });
    }

    [TestMethod]
    public void InvalidTargets_FailTheValidationOfTheFile()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent("[models.tiers]\nquick = [\"haiku\"]\n");
        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "models.tiers.quick[1]");
        Assert.IsTrue(CodeAltaConfigStore.ValidateGlobalConfigContent("[models.tiers]\nquick = [\"claude-code:haiku@low\"]\n").IsValid);
    }

    private static ModelTierSettings Read(string[] complex, string[] standard, string[] quick)
        => ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Complex = [.. complex], Standard = [.. standard], Quick = [.. quick] },
        }).Tiers;

    private static void WithStore(Action<CodeAltaConfigStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-model-tiers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root })); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
