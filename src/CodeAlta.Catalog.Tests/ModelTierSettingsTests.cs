using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;
using TierTarget = CodeAlta.Catalog.ModelTiers.ModelTierTarget;

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
        Assert.IsTrue(TierTarget.TryParse(" Claude-Code:opus@high,xhigh ", out var target, out var error), error);
        Assert.AreEqual("claude-code", target.ProviderKey);
        Assert.AreEqual("opus", target.ModelId);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.High, AgentReasoningEffort.XHigh }, target.Reasoning.ToArray());
        Assert.AreEqual(AgentReasoningEffort.High, target.PreferredReasoning);
        Assert.AreEqual("claude-code:opus@high,xhigh", target.Format());

        Assert.IsTrue(TierTarget.TryParse("ollama:llama3:8b", out target, out _));
        Assert.AreEqual("llama3:8b", target.ModelId);
        Assert.IsNull(target.PreferredReasoning);
        Assert.AreEqual("ollama:llama3:8b", target.Format());
    }

    [TestMethod]
    public void ReplaceProvider_RemovesOrRenamesTheTargetsOfAProvider()
    {
        static TierTarget Target(string value) => TierTarget.TryParse(value, out var target, out var error) ? target : throw new AssertFailedException(error ?? value);
        static string[] Written(IReadOnlyList<TierTarget> targets) => [.. targets.Select(static target => target.Format())];
        var tiers = new ModelTierSettings([Target("work:opus@high"), Target("codex:gpt-6.1")], [Target("home:sonnet"), Target("work:sonnet@low")], [Target("work:haiku")]);
        Assert.AreSame(tiers, tiers.ReplaceProvider("other", null), "Tiers that do not name the provider are not changed.");

        var removed = tiers.ReplaceProvider("WORK", null);
        CollectionAssert.AreEqual(new[] { "codex:gpt-6.1" }, Written(removed.Complex));
        CollectionAssert.AreEqual(new[] { "home:sonnet" }, Written(removed.Standard));
        Assert.AreEqual(0, removed.Quick.Count);

        // A model the tier already lists under the new key keeps its first place only.
        var renamed = tiers.ReplaceProvider("work", "Home");
        CollectionAssert.AreEqual(new[] { "home:opus@high", "codex:gpt-6.1" }, Written(renamed.Complex));
        CollectionAssert.AreEqual(new[] { "home:sonnet" }, Written(renamed.Standard));
        CollectionAssert.AreEqual(new[] { "home:haiku" }, Written(renamed.Quick));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow(":gpt-6.1")]
    [DataRow("codex:")]
    [DataRow("codex:@high")]
    [DataRow("codex:gpt-6.1@")]
    [DataRow("codex:gpt-6.1@huge")]
    [DataRow("codex:gpt-6.1@high,high")]
    [DataRow("co dex:gpt-6.1")]
    public void Target_RefusesWhatIsNotATarget(string value)
    {
        Assert.IsFalse(TierTarget.TryParse(value, out _, out var error));
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
    public void Config_RoundTripsTheResumeAfterALimit_AndOmitsItsDefaults()
    {
        WithStore(store =>
        {
            var tiers = Read(complex: [], standard: ["claude-code:opus@high"], quick: []);
            var resuming = new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: true) { Resume = true, MaxResumes = 3 };
            store.SaveGlobalModelSettings(new ModelSettings(tiers, resuming));

            var content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "resume = true");
            StringAssert.Contains(content, "max_resumes = 3");
            Assert.AreEqual(resuming, store.LoadGlobalModelSettings().Fallback);

            // The usual number of resumes is not written; nor is the resume while it is off.
            store.SaveGlobalModelSettings(new ModelSettings(tiers, resuming with { MaxResumes = ModelFallbackSettings.DefaultMaxResumes }));
            content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "resume = true");
            Assert.DoesNotContain("max_resumes", content);
            store.SaveGlobalModelSettings(new ModelSettings(tiers, resuming with { Resume = false, MaxResumes = ModelFallbackSettings.DefaultMaxResumes }));
            Assert.DoesNotContain("resume", File.ReadAllText(store.ConfigPath));
            Assert.IsFalse(store.LoadGlobalModelSettings().Fallback.Resume);
        });

        // Off, five resumes in a row, unless the file says otherwise.
        Assert.IsFalse(ModelFallbackSettings.Default.Resume);
        Assert.AreEqual(5, ModelFallbackSettings.Default.MaxResumes);
        var errors = ModelSettings.Validate(new CodeAltaModelSettingsDocument { Fallback = new CodeAltaModelFallbackDocument { Resume = true, MaxResumes = 0 } });
        StringAssert.StartsWith(errors.Single(), "models.fallback.max_resumes:");
        Assert.IsFalse(CodeAltaConfigStore.ValidateGlobalConfigContent("[models.fallback]\nmax_resumes = 51\n").IsValid);
        Assert.IsTrue(CodeAltaConfigStore.ValidateGlobalConfigContent("[models.fallback]\nenabled = true\nresume = true\nmax_resumes = 50\n").IsValid);
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
