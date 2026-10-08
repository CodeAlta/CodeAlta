using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.WorkItems;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// What a session started for a piece of work runs with, and which provider is the default one of the application.
/// </summary>
[TestClass]
public sealed class SessionStartChoiceTests
{
    private static readonly ModelProviderDescriptor Anthropic = new(new ModelProviderId("anthropic"), "Anthropic", "anthropic");
    private static readonly ModelProviderDescriptor Codex = new(new ModelProviderId("codex"), "Codex", "codex");
    private static readonly ModelProviderDescriptor Copilot = new(new ModelProviderId("copilot"), "Copilot", "copilot");
    private static readonly IReadOnlyList<ModelProviderDescriptor> Providers = [Anthropic, Codex, Copilot];

    private static readonly IReadOnlyList<AgentModelInfo> CodexModels =
    [
        new("gpt-a", SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
        new("gpt-b"),
    ];

    private static Task<IReadOnlyList<AgentModelInfo>> Models(ModelProviderId provider)
        => Task.FromResult(provider.Value == "codex" ? CodexModels : []);

    private static Task<SessionStartChoice> Choose(WorkItemSelection? asked = null, WorkItemSelection? like = null, WorkItemSelection? recorded = null, string? configured = null,
        IReadOnlyList<ModelProviderDescriptor>? providers = null)
        => SessionStartChoice.ChooseAsync(providers ?? Providers, configured, asked, like, recorded, Models);

    private static (string? Provider, string? Model, AgentReasoningEffort? Effort) Of(SessionStartChoice choice)
        => (choice.Provider?.ProviderId.Value, choice.ModelId, choice.Effort);

    [TestMethod]
    public async Task WithoutAnything_TheDefaultProviderOfTheConfiguration_ThenTheFirstEnabledOne()
    {
        Assert.AreEqual(("codex", null, null), Of(await Choose(configured: "codex")));
        Assert.AreEqual(("codex", null, null), Of(await Choose(configured: " Codex ")), "A provider key has no case.");
        Assert.AreEqual(("anthropic", null, null), Of(await Choose()), "No default is configured: the first enabled provider.");
        Assert.AreEqual(("anthropic", null, null), Of(await Choose(configured: "disabled")), "The configured default is not enabled.");

        var none = await Choose(providers: []);
        Assert.IsNull(none.Provider);
        Assert.AreEqual("No model provider is enabled.", none.Problem);
    }

    [TestMethod]
    public async Task TheSessionThatProposedTheWork_GivesItsProviderModelAndEffort()
    {
        var recorded = new WorkItemSelection("codex", "gpt-a", "high");
        Assert.AreEqual(("codex", "gpt-a", AgentReasoningEffort.High), Of(await Choose(recorded: recorded, configured: "anthropic")));

        // What is no longer there is not asked of the provider: its own model, or the default provider.
        Assert.AreEqual(("codex", null, null), Of(await Choose(recorded: recorded with { ModelId = "retired" })), "The provider is kept with its own model.");
        Assert.AreEqual(("codex", "gpt-a", null), Of(await Choose(recorded: recorded with { ReasoningEffort = "max" })), "The model is kept with its own effort.");
        Assert.AreEqual(("codex", "gpt-b", AgentReasoningEffort.Low), Of(await Choose(recorded: new("codex", "gpt-b", "low"))), "A model that lists no effort takes the one it is given.");
        Assert.AreEqual(("copilot", null, null), Of(await Choose(recorded: new("gone", "gpt-a", "high"), configured: "copilot")), "Its provider is not enabled any more.");
        Assert.AreEqual(("codex", null, null), Of(await Choose(recorded: new("codex"))));

        // A provider that cannot list its models says nothing of them: what was recorded is kept.
        Assert.AreEqual(("copilot", "any-model", AgentReasoningEffort.Medium), Of(await Choose(recorded: new("copilot", "any-model", "medium"))));
    }

    [TestMethod]
    public async Task TheSessionThatShowsTheWork_ComesBeforeTheOneThatProposedIt_AndWhatTheUserChoseBeforeBoth()
    {
        var like = new WorkItemSelection("copilot", "c-1", null);
        var recorded = new WorkItemSelection("codex", "gpt-a", "high");
        Assert.AreEqual(("copilot", "c-1", null), Of(await Choose(like: like, recorded: recorded)));
        Assert.AreEqual(("codex", "gpt-a", AgentReasoningEffort.High), Of(await Choose(like: like with { ProviderKey = "gone" }, recorded: recorded)));
        Assert.AreEqual(("codex", "gpt-b", null), Of(await Choose(asked: new("codex", "gpt-b"), like: like, recorded: recorded)));
        Assert.AreEqual(("anthropic", null, null), Of(await Choose(asked: new("anthropic"), like: like, recorded: recorded)));
    }

    [TestMethod]
    public async Task WhatTheUserChose_IsNeverReplaced()
    {
        Assert.AreEqual(("codex", "gpt-a", AgentReasoningEffort.Low), Of(await Choose(asked: new("codex", "gpt-a", "LOW"))));

        // The user sees what was chosen: the work does not start with something else.
        StringAssert.Contains((await Choose(asked: new("gone", "m"), configured: "codex")).Problem, "'gone' is not enabled");
        StringAssert.Contains((await Choose(asked: new("codex", "retired"))).Problem, "does not offer the model 'retired'");
        StringAssert.Contains((await Choose(asked: new("codex", "gpt-a", "max"))).Problem, "no reasoning effort 'max'");
        StringAssert.Contains((await Choose(asked: new("codex", "gpt-a", "3"))).Problem, "not a reasoning effort");
        Assert.IsNull((await Choose(asked: new("codex", "retired"))).Provider);
    }

    [TestMethod]
    public void TheDefaultProvider_IsTheConfiguredOneWhenItIsEnabled_AndIsReadAgainWhenTheFileChanges()
    {
        var root = Directory.CreateTempSubdirectory("codealta-default-provider-").FullName;
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            var defaults = new DesktopDefaultProvider(options);
            Assert.IsNull(defaults.Configured(), "No configuration file names no default.");
            Assert.AreEqual("anthropic", defaults.Of(Providers)!.ProviderId.Value);

            File.WriteAllText(options.ConfigPath, "[chat]\ndefault_provider = \"copilot\"\n");
            Assert.AreEqual("copilot", defaults.Configured());
            Assert.AreEqual("copilot", defaults.Of(Providers)!.ProviderId.Value);
            Assert.AreEqual("anthropic", defaults.Of([Anthropic, Codex])!.ProviderId.Value, "The configured default is not enabled: the first enabled provider.");
            Assert.IsNull(defaults.Of([]));

            // The configuration of a project comes first for a session of that project.
            var project = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            Directory.CreateDirectory(Path.Combine(project, ".alta"));
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[chat]\ndefault_provider = \"codex\"\n");
            Assert.AreEqual("codex", defaults.Of(Providers, project)!.ProviderId.Value);
            Assert.AreEqual("copilot", defaults.Of(Providers)!.ProviderId.Value);

            File.WriteAllText(options.ConfigPath, "[chat]\ndefault_provider = \"codex\"\n# changed\n");
            Assert.AreEqual("codex", defaults.Configured());

            // A file that does not parse names no default, and fails nothing.
            File.WriteAllText(options.ConfigPath, "[chat\ndefault_provider = ");
            Assert.IsNull(defaults.Configured());
            Assert.AreEqual("anthropic", defaults.Of(Providers)!.ProviderId.Value);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ThePage_IsToldOneDefaultProvider_TheOneANewSessionStartsWith()
    {
        var root = Directory.CreateTempSubdirectory("codealta-default-provider-").FullName;
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            await using var registry = new ModelProviderRegistry();
            // Every provider says it is the default option of its own definition: that is not the default provider.
            foreach (var (key, name, enabled) in new[] { ("anthropic", "Anthropic", true), ("codex", "Codex", true), ("zeta", "Alpha first by name", false) })
            {
                registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId(key), name, key) { IsDefault = true, IsEnabled = enabled },
                    static () => throw new AssertFailedException("The inventory starts no provider."));
            }

            var service = new ConfigurationService(registry, null, new DesktopDefaultProvider(options));
            string[] Defaults() => [.. service.Snapshot(new()).Providers.Where(static provider => provider.IsDefault).Select(static provider => provider.Id)];

            CollectionAssert.AreEqual(new[] { "anthropic" }, Defaults(), "Without a configured default: the first enabled provider, never a disabled one.");
            File.WriteAllText(options.ConfigPath, "[chat]\ndefault_provider = \"codex\"\n");
            CollectionAssert.AreEqual(new[] { "codex" }, Defaults());
            File.WriteAllText(options.ConfigPath, "[chat]\ndefault_provider = \"zeta\"\n# disabled\n");
            CollectionAssert.AreEqual(new[] { "anthropic" }, Defaults());

            var catalog = new ModelCatalogService(registry, new ModelProviderInitializationService(registry), "epoch", new DesktopDefaultProvider(options));
            File.WriteAllText(options.ConfigPath, "[chat]\ndefault_provider = \"codex\"\n");
            CollectionAssert.AreEqual(new[] { "codex" },
                catalog.Providers(new("epoch")).Providers.Where(static provider => provider.IsDefault).Select(static provider => provider.Id).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
