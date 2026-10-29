using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ClaudeCodeContextTests
{
    private const string DisableContext = "CLAUDE_CODE_DISABLE_1M_CONTEXT";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Discovery_PairsExactEntriesWithoutInventingTheUnsuffixedDefault()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var options = cli.CreateOptions("anthropic");
        var models = await DiscoverAsync(options);
        var standard = models.Single(model => model.Id == "alias");
        var extended = models.Single(model => model.Id == "alias[1m]");
        Assert.AreEqual("anthropic", standard.Provider);
        Assert.IsNotNull(standard.ContextSizes);
        Assert.IsNull(standard.ContextSizes.DefaultTokens);
        Assert.IsNull(AgentModelContextSizes.DefaultCapacity(standard, null));
        CollectionAssert.AreEqual(new long[] { 200_000, 1_000_000 }, standard.ContextSizes.TokenCounts.ToArray());
        Assert.AreEqual(1_000_000L, extended.ContextSizes!.DefaultTokens);
        Assert.AreEqual(200_000L, AgentModelContextResolver.Resolve(standard, "alias", "anthropic", 200_000, null)!.EffectiveContextWindowTokens);
        Assert.IsNull(AgentModelContextResolver.Resolve(extended, extended.Id, "anthropic", null, null)!.EffectiveContextWindowTokens,
            "Default must keep the CLI launch unchanged, not become an explicit 1M request.");
        Assert.IsTrue(cli.Processes.All(process => process.UserMessages.Count == 0), "Discovery sends no prompt.");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("disabled-base")]
    [DataRow("disabled-extended")]
    [DataRow("different-model")]
    [DataRow("unresolved")]
    [DataRow("different-alias")]
    [DataRow("synthetic-default")]
    public async Task Discovery_RequiresAnEnabledExactCounterpartResolvingToTheSameModel(string variant)
    {
        var entries = Pair();
        switch (variant)
        {
            case "missing": entries.RemoveAt(0); break;
            case "disabled-base": entries[0]!["disabled"] = true; break;
            case "disabled-extended": entries[1]!["disabled"] = true; break;
            case "different-model": entries[0]!["resolvedModel"] = "another-version"; break;
            case "unresolved": entries[0]!.AsObject().Remove("resolvedModel"); break;
            case "different-alias": entries[0]!["value"] = "other-alias"; break;
            case "synthetic-default":
                entries[0]!["value"] = "default";
                entries[0]!["resolvedModel"] = "alias";
                entries[1]!["resolvedModel"] = "alias[1m]";
                break;
        }
        var cli = new ClaudeCodeFakeCli { Models = entries };
        var models = await DiscoverAsync(cli.CreateOptions());
        Assert.IsTrue(models.All(model => model.ContextSizes?.TokenCounts.Count is null or 0));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Discovery_DoesNotOfferExpansionWhenProviderOrInheritedEnvironmentDisablesIt(bool inherited)
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var options = cli.CreateOptions(inheritedContextRestriction: inherited ? "1" : null,
            environmentVariables: inherited ? null : new Dictionary<string, string> { [DisableContext] = "1" });
        var models = await DiscoverAsync(options);
        Assert.IsTrue(models.All(model => model.ContextSizes?.Supports(1_000_000) != true));
        Assert.IsTrue(cli.Processes.All(process => process.UserMessages.Count == 0));
    }

    [TestMethod]
    public async Task ProviderRestriction_UsesThePlatformsEnvironmentNameComparison()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var options = cli.CreateOptions(environmentVariables: new Dictionary<string, string>
        {
            [DisableContext.ToLowerInvariant()] = "1",
        });
        var models = await DiscoverAsync(options);
        Assert.AreEqual(!OperatingSystem.IsWindows(), models.Single(model => model.Id == "alias").ContextSizes?.Supports(1_000_000) == true);
    }

    [TestMethod]
    public void RestrictedLaunch_RemovesWindowsCaseVariantsBeforeForcing200K()
    {
        var lower = DisableContext.ToLowerInvariant();
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "fixture",
            EnvironmentVariables = new Dictionary<string, string> { [lower] = "0", ["unrelated"] = "keep" },
        };
        var sessionEnvironment = new AgentProcessEnvironment([new(DisableContext, "0")]);
        var launch = ClaudeCodeLauncher.Create("/fake/claude", options,
            new ClaudeCodeLaunchKey(null, "alias", null, Environment: sessionEnvironment, ContextWindowTokens: 200_000),
            null, null, withTools: false);
        var equivalentKeys = launch.Environment.Where(variable => string.Equals(variable.Key, DisableContext, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.HasCount(1, equivalentKeys);
            Assert.AreEqual("1", equivalentKeys[0].Value);
        }
        else
        {
            Assert.AreEqual("0", launch.Environment[lower], "Other platforms have case-sensitive variable names.");
        }
        Assert.AreEqual("1", launch.Environment[DisableContext]);
        Assert.AreEqual("keep", launch.Environment["unrelated"]);
        Assert.AreEqual("0", sessionEnvironment.Get(DisableContext));
        Assert.AreEqual("0", options.EnvironmentVariables[lower]);
    }

    [TestMethod]
    public async Task ContextChanges_MapCliModelsResumeAndLeaveOtherSessionsAndDefaultAlone()
    {
        var cli = new ClaudeCodeFakeCli
        {
            Models = Pair(),
            ModelUsage = () => new JsonObject { ["claude-test-1"] = new JsonObject { ["contextWindow"] = 173000 } },
        };
        var options = cli.CreateOptions();
        var models = await DiscoverAsync(options);
        var model = models.Single(model => model.Id == "alias");
        await using var executor = new ClaudeCodeTurnExecutor(options);

        var first = await ExecuteAsync(executor, Request(model, 1_000_000, "large"));
        var large = cli.Last;
        AssertLaunch(large, "alias[1m]", disabled: false);
        var smallResponse = await ExecuteAsync(executor, Request(model, 200_000, "small"));
        var small = cli.Last;
        AssertLaunch(small, "alias", disabled: true);
        Assert.IsFalse(large.IsDisposed, "Another session's selection must not restart this one.");

        var lowered = await ExecuteAsync(executor, Request(model, 200_000, "large", first));
        var lowerProcess = cli.Last;
        AssertLaunch(lowerProcess, "alias", disabled: true);
        Assert.IsTrue(large.IsDisposed);
        Assert.AreEqual(large.SessionId, lowerProcess.ResumedSessionId);
        var reset = await ExecuteAsync(executor, Request(model, null, "large", lowered) with
        {
            Conversation = [User("one"), first.AssistantMessage, User("next"), lowered.AssistantMessage, User("reset")],
        });
        var resetProcess = cli.Last;
        AssertLaunch(resetProcess, "alias", disabled: false);
        Assert.AreEqual(large.SessionId, resetProcess.ResumedSessionId);
        Assert.IsTrue(lowerProcess.IsDisposed);

        var count = cli.Processes.Count;
        await ExecuteAsync(executor, Request(model, 200_000, "small", smallResponse));
        Assert.AreEqual(count, cli.Processes.Count);
        Assert.IsFalse(small.IsDisposed);
        AssertLaunch(small, "alias", disabled: true);
        Assert.AreEqual(173_000L, first.Usage!.TokenLimit, "The CLI's actual reported window, not the selected capacity.");
        var selected = AgentModelContextResolver.Resolve(model, model.Id, "claude-code", 1_000_000, null)!;
        Assert.AreSame(first.Usage, AgentUsageFactory.AttachModelInfo(first.Usage, selected, replaceCapacity: true));
        Assert.AreEqual(173_000L, reset.Usage!.TokenLimit);
        Assert.IsNull((options.GetEnvironmentVariable!)(DisableContext));
        Assert.IsFalse(options.EnvironmentVariables.ContainsKey(DisableContext));
    }

    [TestMethod]
    public async Task ExtendedDefault_DoesNotRewriteTheModelOrAddAnEnvironmentOverride()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var options = cli.CreateOptions();
        var model = (await DiscoverAsync(options)).Single(model => model.Id == "alias[1m]");
        await using var executor = new ClaudeCodeTurnExecutor(options);
        await ExecuteAsync(executor, Request(model, null, "default"));
        AssertLaunch(cli.Last, "alias[1m]", disabled: false);
    }

    [TestMethod]
    public async Task ExtendedSelection_RoundTripsItsLaunchMappingAndCanRestrictTheSameModel()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var options = cli.CreateOptions();
        var model = (await DiscoverAsync(options)).Single(model => model.Id == "alias[1m]");
        model = JsonSerializer.Deserialize(JsonSerializer.Serialize(model, AgentJsonSerializerContext.Default.AgentModelInfo),
            AgentJsonSerializerContext.Default.AgentModelInfo)!;
        await using var executor = new ClaudeCodeTurnExecutor(options);
        await ExecuteAsync(executor, Request(model, 200_000, "restricted"));
        AssertLaunch(cli.Last, "alias", disabled: true);
        Assert.AreEqual("alias[1m]", model.Id);
    }

    [TestMethod]
    public async Task StaleSelection_CannotBypassAnInheritedOrProviderRestriction()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var model = (await DiscoverAsync(cli.CreateOptions())).Single(model => model.Id == "alias");
        foreach (var inherited in new[] { false, true })
        {
            var options = cli.CreateOptions(inheritedContextRestriction: inherited ? "1" : null,
                environmentVariables: inherited ? null : new Dictionary<string, string> { [DisableContext] = "1" });
            await using var executor = new ClaudeCodeTurnExecutor(options);
            var before = cli.Processes.Count;
            await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(executor, Request(model, 1_000_000, "restricted")));
            Assert.AreEqual(before, cli.Processes.Count, "Reject before launching or sending a prompt.");
        }
    }

    [TestMethod]
    public async Task Default_KeepsTheProviderRestrictionAndDiscoveryRefreshesAfterItChanges()
    {
        var cli = new ClaudeCodeFakeCli { Models = Pair() };
        var environment = new Dictionary<string, string> { [DisableContext] = "1" };
        var options = cli.CreateOptions(environmentVariables: environment);
        await using var provider = new ClaudeCodeModelProviderRuntime(options);
        var models = await provider.ModelCatalog!.ListModelsAsync(provider.RuntimeDescriptor);
        var model = models.Single(model => model.Id == "alias");
        Assert.IsNull(model.ContextSizes);
        await using var executor = new ClaudeCodeTurnExecutor(options);
        await ExecuteAsync(executor, Request(model, null, "default"));
        AssertLaunch(cli.Last, "alias", disabled: true);

        // A change of the user's provider environment invalidates the model probe, not another session's launch.
        environment.Remove(DisableContext);
        models = await provider.ModelCatalog.ListModelsAsync(provider.RuntimeDescriptor);
        Assert.IsTrue(models.Single(model => model.Id == "alias").ContextSizes!.Supports(1_000_000));
    }

    [TestMethod]
    public async Task Runtime_UsesThePublicModelAndPreservesCliUsageAcrossContextResume()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli
        {
            Models = Pair(),
            ModelUsage = () => new JsonObject { ["claude-test-1"] = new JsonObject { ["contextWindow"] = 173000 } },
        };
        await using var provider = new ClaudeCodeModelProviderRuntime(cli.CreateOptions("anthropic"));
        await using var runtime = new AgentRuntime(new ModelProviderId("anthropic"), "Claude fixture", new AgentRuntimeOptions
        {
            StateRootPath = directory.Path,
            Providers = [provider.CreateProviderRegistration()],
        });
        await runtime.ListModelsAsync();
        string sessionId;
        await using (var session = await runtime.CreateSessionAsync(new()
        {
            ProviderKey = "anthropic", Model = "alias", WorkingDirectory = directory.Path,
            ContextWindowTokens = 1_000_000, InstructionsAlreadyComposed = true,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        }))
        {
            sessionId = session.SessionId;
            await session.SendAsync(new() { Input = AgentInput.Text("one") }).WaitAsync(Timeout);
            AssertLaunch(cli.Last, "alias[1m]", disabled: false);
        }
        var before = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!;
        Assert.AreEqual("alias", before.ModelId);
        Assert.AreEqual(173_000L, before.Usage!.TokenLimit);
        await using var resumed = await runtime.ResumeSessionAsync(sessionId, new()
        {
            ProviderKey = "anthropic", Model = "alias", WorkingDirectory = directory.Path,
            ContextWindowTokens = 200_000, InstructionsAlreadyComposed = true,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        });
        var after = (await runtime.SessionStore.GetSessionSummaryAsync(sessionId))!;
        Assert.AreEqual(before.Usage.TokenLimit, after.Usage!.TokenLimit, "Resume cannot replace CLI usage with a local estimate.");
        await resumed.SendAsync(new() { Input = AgentInput.Text("two") }).WaitAsync(Timeout);
        AssertLaunch(cli.Last, "alias", disabled: true);
        Assert.IsNotNull(cli.Last.ResumedSessionId);
    }

    private static JsonArray Pair() => new(
        new JsonObject { ["value"] = "alias", ["resolvedModel"] = "opaque-version" },
        new JsonObject { ["value"] = "alias[1m]", ["resolvedModel"] = "opaque-version[1m]" });

    private static async Task<IReadOnlyList<AgentModelInfo>> DiscoverAsync(ClaudeCodeModelProviderRuntimeOptions options)
    {
        await using var provider = new ClaudeCodeModelProviderRuntime(options);
        return await provider.ModelCatalog!.ListModelsAsync(provider.RuntimeDescriptor);
    }

    private static void AssertLaunch(ClaudeCodeFakeProcess process, string model, bool disabled)
    {
        Assert.AreEqual(model, process.Launch.Arguments.SkipWhile(argument => argument != "--model").Skip(1).First());
        Assert.AreEqual(disabled ? "1" : null, process.Launch.Environment.GetValueOrDefault(DisableContext));
        Assert.IsFalse(process.Launch.Arguments.Contains("--autocompact"), "A compaction threshold is not a capacity.");
    }

    private static Task<AgentTurnResponse> ExecuteAsync(ClaudeCodeTurnExecutor executor, AgentTurnRequest request)
        => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).WaitAsync(Timeout);

    private static AgentTurnRequest Request(AgentModelInfo model, long? context, string sessionId, AgentTurnResponse? previous = null)
        => new()
        {
            Provider = new()
            {
                ProviderKey = "claude-code", ProtocolFamily = "claude-code", DisplayName = "Fixture",
                TransportKind = AgentTransportKind.ClaudeCodeCli, Compaction = AgentCompactionSettings.Default with { Enabled = false },
            },
            ProviderId = new("claude-code"), SessionId = sessionId, RunId = new("run"),
            ModelId = model.Id,
            ModelInfo = AgentModelContextResolver.Resolve(model, model.Id, "claude-code", context, null),
            WorkingDirectory = Path.GetTempPath(),
            Conversation = previous is null ? [User("one")] : [User("one"), previous.AssistantMessage, User("next")],
            Tools = [],
            State = new()
            {
                SessionId = sessionId, ProviderSessionId = previous?.ProviderSessionId,
                ProviderState = previous?.ProviderState, UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);
}
