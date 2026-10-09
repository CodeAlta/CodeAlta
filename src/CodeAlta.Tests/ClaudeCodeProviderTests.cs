using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// The parts of the Claude Code provider that run no turn: finding the executable, starting it, and reading
/// what it says of its models and its account.
/// </summary>
[TestClass]
public sealed class ClaudeCodeProviderTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string Home = Path.Combine(Root, "home", "user");

    [TestMethod]
    public void Locator_FindsTheExecutableOnPath()
    {
        // The folders have no drive: the separator of the PATH of the described system is the ':' a drive has.
        var root = Path.DirectorySeparatorChar.ToString();
        var expected = Path.Combine(root, "tools", "claude");
        var environment = CreateEnvironment(isWindows: false, path: $"relative{Path.PathSeparator}{Path.Combine(root, "usr", "bin")}{Path.PathSeparator}{Path.Combine(root, "tools")}", expected);

        var resolution = ClaudeCodeCliLocator.Resolve(null, environment);

        Assert.AreEqual(expected, resolution.Path);
        Assert.IsNull(resolution.Error);
    }

    [TestMethod]
    public void Locator_LooksInTheFoldersOfTheInstallersWhenPathHasNone()
    {
        // A desktop application started from the Dock or the Start menu does not have the PATH of the user's shell.
        var expected = Path.Combine(Home, ".local", "bin", "claude");
        var environment = CreateEnvironment(isWindows: false, path: Path.Combine(Root, "usr", "bin"), expected);

        Assert.AreEqual(expected, ClaudeCodeCliLocator.Resolve("  ", environment).Path);
    }

    [TestMethod]
    public void Locator_ReportsHowToInstallWhenNothingIsFound()
    {
        var resolution = ClaudeCodeCliLocator.Resolve(null, CreateEnvironment(isWindows: false, path: Path.Combine(Root, "usr", "bin")));

        Assert.IsFalse(resolution.Found);
        StringAssert.Contains(resolution.Error, "Claude Code was not found");
        StringAssert.Contains(resolution.Error, "command");
    }

    [TestMethod]
    public void Locator_UsesTheConfiguredPathAndDoesNotSearchWhenItIsMissing()
    {
        var configured = Path.Combine(Root, "opt", "claude", "claude");
        var onPath = Path.Combine(Root, "tools", "claude");

        var found = ClaudeCodeCliLocator.Resolve(configured, CreateEnvironment(isWindows: false, path: Path.Combine(Root, "tools"), configured, onPath));
        var missing = ClaudeCodeCliLocator.Resolve(configured, CreateEnvironment(isWindows: false, path: Path.Combine(Root, "tools"), onPath));

        Assert.AreEqual(configured, found.Path);
        Assert.IsFalse(missing.Found, "A configured executable that is missing is an error, not a reason to run another one.");
        StringAssert.Contains(missing.Error, configured);
    }

    [TestMethod]
    public void Locator_ExpandsTheHomeFolderOfAConfiguredPath()
    {
        var expected = Path.Combine(Home, "bin", "claude");

        Assert.AreEqual(expected, ClaudeCodeCliLocator.Resolve("~/bin/claude", CreateEnvironment(isWindows: false, path: null, expected)).Path);
    }

    [TestMethod]
    public void Locator_OnWindowsPrefersTheNativeExecutableToAShimEarlierOnPath()
    {
        var shim = Path.Combine(Root, "npm", "claude.cmd");
        var native = Path.Combine(Root, "native", "claude.exe");
        var path = $"{Path.Combine(Root, "npm")};{Path.Combine(Root, "native")}";

        Assert.AreEqual(native, ClaudeCodeCliLocator.Resolve(null, CreateEnvironment(isWindows: true, path, shim, native)).Path);
    }

    [TestMethod]
    public void Locator_OnWindowsRefusesABatchShim()
    {
        var shim = Path.Combine(Root, "npm", "claude.cmd");

        var found = ClaudeCodeCliLocator.Resolve(null, CreateEnvironment(isWindows: true, Path.Combine(Root, "npm"), shim));
        var configured = ClaudeCodeCliLocator.Resolve(shim, CreateEnvironment(isWindows: true, null, shim));

        Assert.IsFalse(found.Found);
        StringAssert.Contains(found.Error, "batch script");
        Assert.IsFalse(configured.Found);
        Assert.IsTrue(ClaudeCodeCliLocator.IsBatchScript("claude.BAT"));
        Assert.IsTrue(ClaudeCodeCliLocator.IsBatchScript("claude.cmd. "));
        Assert.IsFalse(ClaudeCodeCliLocator.IsBatchScript("claude.exe"));
    }

    [TestMethod]
    public void Launcher_StartsTheCliInStreamJsonModeWithoutTouchingItsAuthentication()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            PermissionMode = "acceptEdits",
            ExtraArguments = ["--add-dir", "/data"],
            ClientApp = "codealta/1.2.3",
        };

        var launch = ClaudeCodeLauncher.Create(
            "/bin/claude",
            options,
            new ClaudeCodeLaunchKey("/work", "sonnet", "xhigh"),
            newSessionId: "0f0e0d0c-0b0a-4908-8706-050403020100",
            resumeSessionId: null,
            withTools: true,
            showReasoning: true);

        CollectionAssert.AreEqual(
            new[]
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--replay-user-messages", "--permission-prompt-tool", "stdio", "--permission-mode", "acceptEdits", "--model", "sonnet",
                "--effort", "xhigh", "--session-id=0f0e0d0c-0b0a-4908-8706-050403020100", "--mcp-config",
                """{"mcpServers":{"codealta":{"type":"sdk","name":"codealta"}}}""", "--allowedTools=mcp__codealta",
                "--thinking-display", "summarized", "--add-dir", "/data",
            },
            launch.Arguments.ToArray());
        Assert.AreEqual("/bin/claude", launch.FileName);
        Assert.AreEqual("/work", launch.WorkingDirectory);
        Assert.AreEqual("codealta/1.2.3", launch.Environment["CLAUDE_AGENT_SDK_CLIENT_APP"]);
        Assert.AreEqual("1", launch.Environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"]);

        // Only the marks of a Claude Code session CodeAlta was started from are removed. The CLI authenticates with
        // what the user configured: no credential, endpoint or provider variable is set or removed, unless the
        // API key is to be left out (below).
        var removed = launch.Environment.Where(static pair => pair.Value is null).Select(static pair => pair.Key).ToArray();
        CollectionAssert.Contains(removed, "CLAUDECODE");
        CollectionAssert.Contains(removed, "CLAUDE_CODE_ENTRYPOINT");
        foreach (var name in launch.Environment.Keys)
        {
            Assert.IsFalse(
                name.StartsWith("ANTHROPIC_", StringComparison.Ordinal) ||
                name.Contains("TOKEN", StringComparison.Ordinal) && name != "CLAUDE_CODE_MESSAGING_TOKEN" ||
                name.Contains("API_KEY", StringComparison.Ordinal) ||
                name is "CLAUDE_CONFIG_DIR" or "CLAUDE_CODE_USE_BEDROCK" or "CLAUDE_CODE_USE_VERTEX",
                name);
        }

        Assert.IsFalse(launch.Arguments.Contains("--bare"));
        Assert.IsFalse(launch.Arguments.Contains("--dangerously-skip-permissions"));
    }

    [TestMethod]
    public void Launcher_LeavesOutTheApiKeyOnlyWhenAskedTo()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code" };
        var key = new ClaudeCodeLaunchKey(null, null, null);

        var without = ClaudeCodeLauncher.Create("/bin/claude", options, key, null, null, withTools: true, withoutApiKey: true);
        var with = ClaudeCodeLauncher.Create("/bin/claude", options, key, null, null, withTools: true);

        Assert.IsTrue(without.Environment.TryGetValue("ANTHROPIC_API_KEY", out var removed));
        Assert.IsNull(removed, "A null value removes the inherited variable.");
        Assert.IsFalse(with.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        // Nothing else of the authentication of the CLI is touched: its login is what it falls back to.
        CollectionAssert.AreEquivalent(
            with.Environment.Keys.Append("ANTHROPIC_API_KEY").ToArray(),
            without.Environment.Keys.ToArray());
    }

    [TestMethod]
    [DataRow(null, ClaudeCodeApiKeyPolicy.FollowClaudeCode, null, "NoKey")]
    [DataRow(" ", ClaudeCodeApiKeyPolicy.Use, null, "NoKey")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.Use, null, "Use")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.Ignore, null, "Ignore")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, null, "Undecided")]
    // Claude Code saves the last 20 characters of the trimmed key.
    [DataRow(" sk-ant-api03-xxxx0123456789abcdefKLMN ", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"approved":[],"rejected":["0123456789abcdefKLMN"]}}""", "Ignore")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"approved":["0123456789abcdefKLMN"],"rejected":["0123456789abcdefKLMN"]}}""", "Use")]
    [DataRow("short-key", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"approved":["short-key"]}}""", "Use")]
    // An answer for another key, or a file of another shape, is no answer.
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"rejected":["another-key-entirely"]}}""", "Undecided")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"customApiKeyResponses":{"rejected":"0123456789abcdefKLMN"}}""", "Undecided")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, """{"apiKeyResponses":{}}""", "Undecided")]
    [DataRow("sk-ant-api03-xxxx0123456789abcdefKLMN", ClaudeCodeApiKeyPolicy.FollowClaudeCode, "{ not json", "Undecided")]
    public void ApiKey_IsDecidedByThePolicyThenByTheAnswerClaudeCodeSaved(string? key, ClaudeCodeApiKeyPolicy policy, string? config, string expected)
    {
        var decision = ClaudeCodeApiKey.Decide(policy, name => name == "ANTHROPIC_API_KEY" ? key : null, () => config);

        Assert.AreEqual(Enum.Parse<ClaudeCodeApiKeyDecision>(expected), decision);
    }

    [TestMethod]
    [DataRow("CLAUDE_CODE_USE_BEDROCK", "1")]
    [DataRow("CLAUDE_CODE_USE_VERTEX", "true")]
    [DataRow("CLAUDE_CODE_USE_FOUNDRY", " ON ")]
    public void ApiKey_OfACliThatUsesACloudProvider_IsLeftAlone(string variable, string value)
    {
        var environment = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-ant-api03-key", [variable] = value };

        var decision = ClaudeCodeApiKey.Decide(ClaudeCodeApiKeyPolicy.FollowClaudeCode, name => environment.GetValueOrDefault(name), static () => null);

        Assert.AreEqual(ClaudeCodeApiKeyDecision.NoKey, decision);
    }

    [TestMethod]
    public void ApiKey_AnswersAreReadWhereClaudeCodeKeepsThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), "claude-config");

        Assert.AreEqual(Path.Combine(folder, ".claude.json"), ClaudeCodeApiKey.ClaudeConfigPath(name => name == "CLAUDE_CONFIG_DIR" ? folder : null));
        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
            ClaudeCodeApiKey.ClaudeConfigPath(static _ => null));
    }

    [TestMethod]
    [DataRow("use", ClaudeCodeApiKeyPolicy.Use)]
    [DataRow(" ignore ", ClaudeCodeApiKeyPolicy.Ignore)]
    [DataRow(null, ClaudeCodeApiKeyPolicy.FollowClaudeCode)]
    [DataRow("", ClaudeCodeApiKeyPolicy.FollowClaudeCode)]
    public void ApiKeyPolicy_IsReadFromTheSettingOfTheProvider(string? value, ClaudeCodeApiKeyPolicy expected)
        => Assert.AreEqual(expected, ClaudeCodeModelProviderRuntimeOptions.ParseApiKeyPolicy(value));

    [TestMethod]
    public void Launcher_ResumesAndLeavesTheDefaultModelToTheCli()
    {
        var launch = ClaudeCodeLauncher.Create(
            "/bin/claude",
            new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code" },
            new ClaudeCodeLaunchKey(null, ClaudeCodeLauncher.ToModelOption("default"), ClaudeCodeLauncher.ToEffort(AgentReasoningEffort.None)),
            newSessionId: "ignored",
            resumeSessionId: "-starts-with-a-dash",
            withTools: true);

        CollectionAssert.Contains(launch.Arguments.ToArray(), "--resume=-starts-with-a-dash", "The value is bound to its option whatever it is.");
        Assert.IsFalse(launch.Arguments.Any(static argument => argument.StartsWith("--session-id", StringComparison.Ordinal)));
        Assert.IsFalse(launch.Arguments.Contains("--model"));
        Assert.IsFalse(launch.Arguments.Contains("--effort"));
        Assert.IsFalse(launch.Arguments.Contains("--permission-mode"));
        Assert.AreEqual("low", ClaudeCodeLauncher.ToEffort(AgentReasoningEffort.Minimal));

        // The mode of a session is the one the CLI starts in, rather than the one of the provider.
        var session = ClaudeCodeLauncher.Create(
            "/bin/claude",
            new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code", PermissionMode = "acceptEdits" },
            new ClaudeCodeLaunchKey(null, null, null),
            newSessionId: "0f0e0d0c-0b0a-4908-8706-050403020100",
            resumeSessionId: null,
            withTools: true,
            permissionMode: "bypassPermissions");
        CollectionAssert.IsSubsetOf(new[] { "--permission-mode", "bypassPermissions" }, session.Arguments.ToArray());
        Assert.IsFalse(session.Arguments.Contains("acceptEdits"));
        Assert.IsFalse(session.Arguments.Any(static argument => argument.Contains("dangerously", StringComparison.Ordinal)));
        Assert.AreEqual("max", ClaudeCodeLauncher.ToEffort(AgentReasoningEffort.Max));
        Assert.AreEqual("claude-fable-5-1[1m]", ClaudeCodeLauncher.ToModelOption(" claude-fable-5-1[1m] "));
    }

    [TestMethod]
    public async Task Provider_ReportsThePermissionModesOfTheCliAndItsOwn()
    {
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code", PermissionMode = " auto " });

        var expected = new[] { "default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions" };
        CollectionAssert.AreEqual(expected, runtime.Descriptor.PermissionModes.ToArray());
        CollectionAssert.AreEqual(expected, runtime.RuntimeDescriptor.Profile!.PermissionModes.ToArray());
        Assert.AreEqual("auto", runtime.Descriptor.DefaultPermissionMode);
        Assert.IsNull(ClaudeCodeModelProviderRuntime.CreateDescriptor(new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "other" }).DefaultPermissionMode);

        // Providers without modes report none.
        Assert.AreEqual(0, new ModelProviderDescriptor(new ModelProviderId("openai"), "OpenAI").PermissionModes.Count);
        Assert.AreEqual(0, new AgentProviderProfile().PermissionModes.Count);
    }

    [TestMethod]
    public async Task Probe_ListsTheModelsTheCliOffers()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        CollectionAssert.AreEqual(new[] { "claude-test-1", "sonnet", "haiku" }, probe.Models.Select(static model => model.Id).ToArray(), "An entry the CLI disables is not offered, and its default is named.");
        Assert.AreEqual("claude-test-1", probe.Models[0].DisplayName);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh, AgentReasoningEffort.Max },
            probe.Models[0].SupportedReasoningEfforts!.ToArray());
        Assert.IsNull(probe.Models[2].SupportedReasoningEfforts);

        // The CLI does not say which models take images: all the ones it runs do, and the composer only lets
        // an image be pasted for a model that says so.
        Assert.IsTrue(probe.Models.All(static model => AgentImageInputCapability.Read(model) == true));
        StringAssert.Contains(probe.StatusMessage, "/fake/bin/claude");
        StringAssert.Contains(probe.StatusMessage, "Claude Max");
        Assert.IsFalse(probe.StatusMessage!.Contains("someone@example.test", StringComparison.Ordinal), "The identity of the account stays in the CLI.");
        Assert.IsFalse(probe.StatusMessage.Contains("Organization", StringComparison.Ordinal));

        var process = cli.Processes.Single();
        Assert.IsTrue(process.IsDisposed, "The process that answered is stopped.");
        CollectionAssert.Contains(process.Launch.Arguments.ToArray(), "--no-session-persistence");
        Assert.AreEqual(0, process.UserMessages.Count, "No model is called to list the models.");

        // The answer is kept for a while: the dialogs that list models do not start a process each.
        await runtime.ProbeAsync();
        Assert.AreEqual(1, cli.Processes.Count);
    }

    [TestMethod]
    public async Task Probe_ReportsASignedOutCli()
    {
        var cli = new ClaudeCodeFakeCli { SignedIn = false };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Failed, probe.Availability);
        Assert.AreEqual("claude-code-signed-out", probe.ErrorCategory);
        StringAssert.Contains(probe.StatusMessage, "/login");
        Assert.AreEqual(3, probe.Models.Count);
    }

    [TestMethod]
    public async Task Probe_AsksAgainAfterASignedOutCli()
    {
        var cli = new ClaudeCodeFakeCli { SignedIn = false };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        Assert.AreEqual(ModelProviderAvailability.Failed, (await runtime.ProbeAsync()).Availability);

        // The user signs in in a terminal and tests the provider again: the answer of before is not the one to give.
        cli.SignedIn = true;
        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        Assert.AreEqual(2, cli.Processes.Count, "A signed-out answer is not kept.");
    }

    [TestMethod]
    public async Task Probe_ReportsAMissingCli()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            ResolveCli = static () => new ClaudeCodeCliResolution(null, "Claude Code was not found: no `claude` executable is on PATH."),
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(options);

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Failed, probe.Availability);
        Assert.AreEqual("claude-code-not-found", probe.ErrorCategory);
        StringAssert.Contains(probe.StatusMessage, "was not found");
    }

    [TestMethod]
    public async Task Probe_ReportsACliThatDoesNotStart()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null),
            TransportFactory = new FailingTransportFactory(),
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(options);

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Failed, probe.Availability);
        Assert.AreEqual("claude-code-unavailable", probe.ErrorCategory, "An executable that is there and does not run is not a missing one.");
        StringAssert.Contains(probe.StatusMessage, "could not be started");
    }

    [TestMethod]
    public async Task Probe_OfADisabledProviderStartsNothing()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            IsEnabled = false,
            TransportFactory = cli,
            ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null),
        });

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Disabled, probe.Availability);
        Assert.AreEqual(0, cli.Processes.Count);
    }

    [TestMethod]
    public async Task Catalog_AppliesTheModelFilters()
    {
        var cli = new ClaudeCodeFakeCli();
        var filtered = cli.CreateOptions();
        filtered = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = filtered.ProviderKey,
            TransportFactory = cli,
            ResolveCli = filtered.ResolveCli,
            ModelsIncludeRegex = "^(sonnet|haiku)$",
        };
        var pinned = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = filtered.ProviderKey,
            TransportFactory = cli,
            ResolveCli = filtered.ResolveCli,
            SingleModelId = "claude-custom-9",
        };
        await using var filteredRuntime = new ClaudeCodeModelProviderRuntime(filtered);
        await using var pinnedRuntime = new ClaudeCodeModelProviderRuntime(pinned);

        CollectionAssert.AreEqual(new[] { "sonnet", "haiku" }, (await filteredRuntime.ProbeAsync()).Models.Select(static model => model.Id).ToArray());
        var pinnedModels = (await pinnedRuntime.ProbeAsync()).Models;
        CollectionAssert.AreEqual(new[] { "claude-custom-9" }, pinnedModels.Select(static model => model.Id).ToArray());
        Assert.AreEqual(true, AgentImageInputCapability.Read(pinnedModels[0]), "A model the CLI does not list takes images like the others.");
        Assert.AreEqual("claude-custom-9", pinnedRuntime.Descriptor.DefaultModelId);
    }

    [TestMethod]
    public void Models_NameTheModelTheCliChoosesByDefault()
    {
        static string[] Read(string models) => [.. ClaudeCodeModelCatalog.ReadModels(JsonDocument.Parse($$"""{"models":[{{models}}]}""").RootElement).Select(static model => $"{model.Id}={model.DisplayName}")];

        // What Claude Code 2.1.292 lists: its default and the alias of the same model, then the others.
        const string Default = """{"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","description":"Opus 5.5 · Best for everyday, complex tasks"}""";
        const string Opus = """{"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5"}""";
        const string Sonnet = """{"value":"sonnet","resolvedModel":"claude-sonnet-5-5","displayName":"Sonnet 5.5"}""";

        CollectionAssert.AreEqual(new[] { "opus=Opus 5.5", "sonnet=Sonnet 5.5" }, Read($"{Default},{Opus},{Sonnet}"));
        CollectionAssert.AreEqual(new[] { "opus=Opus 5.5", "sonnet=Sonnet 5.5" }, Read($"{Default},{Sonnet},{Opus}"), "The model of the default stays the first one, which a session without a model takes.");
        CollectionAssert.AreEqual(new[] { "claude-opus-5-5=claude-opus-5-5", "sonnet=Sonnet 5.5" }, Read($"{Default},{Sonnet}"), "A default that no other entry names is listed under the name of its model.");
        CollectionAssert.AreEqual(new[] { "sonnet=Sonnet 5.5" }, Read("""{"value":"default","displayName":"Default (recommended)"},""" + Sonnet), "A default that does not say its model is not a model to select.");
        Assert.AreEqual(0, Read("""{"value":"default","displayName":"Default (recommended)"}""").Length);

        // A session or a setting that still names the default runs with the choice of the CLI.
        Assert.IsNull(ClaudeCodeLauncher.ToModelOption("default"));
    }

    [TestMethod]
    public async Task Catalog_OffersNamedModelsWhenTheCliCannotBeAsked()
    {
        var options = new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = "claude-code",
            ResolveCli = static () => new ClaudeCodeCliResolution(null, "Claude Code was not found."),
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(options);

        var models = await runtime.ModelCatalog!.ListModelsAsync(runtime.RuntimeDescriptor);

        CollectionAssert.AreEqual(new[] { "sonnet", "opus", "haiku" }, models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public void Account_IsSignedInWithAnyMethodTheCliSupports()
    {
        static (bool IsSignedIn, string? Summary) Read(string account) => ClaudeCodeModelCatalog.ReadAccount(JsonDocument.Parse($$"""{"account":{{account}}}""").RootElement);

        // The two shapes Claude Code 2.1.289 and 2.1.292 write: signed out, and signed in with a plan.
        Assert.AreEqual((false, null), Read("""{"tokenSource":"none","apiProvider":"firstParty"}"""));
        Assert.AreEqual((true, "Claude Max"), Read("""{"email":"a@b.test","organization":"Org","subscriptionType":"Claude Max","apiProvider":"firstParty"}"""));
        Assert.AreEqual((true, null), Read("""{"apiProvider":"firstParty"}"""), "Only a CLI that says it has no token is signed out.");
        Assert.AreEqual((true, "API key"), Read("""{"tokenSource":"none","apiKeySource":"ANTHROPIC_API_KEY","apiProvider":"firstParty"}"""));
        Assert.AreEqual((true, "bedrock"), Read("""{"apiProvider":"bedrock"}"""));
        Assert.AreEqual((true, null), ClaudeCodeModelCatalog.ReadAccount(JsonDocument.Parse("{}").RootElement), "A CLI that does not describe its account is tried.");
    }

    [TestMethod]
    public void Runtime_DescribesAProviderThatKeepsItsOwnContext()
    {
        var runtime = new ClaudeCodeModelProviderRuntime(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = " my-claude ",
            DefaultModelId = "opus",
            DefaultReasoningEffort = AgentReasoningEffort.High,
        });

        Assert.AreEqual("my-claude", runtime.Descriptor.ProviderId.Value);
        Assert.AreEqual("claude-code", runtime.Descriptor.ProviderType);
        Assert.AreEqual("opus", runtime.Descriptor.DefaultModelId);
        Assert.AreEqual(AgentReasoningEffort.High, runtime.Descriptor.DefaultReasoningEffort);
        Assert.AreEqual("claude-code", runtime.RuntimeDescriptor.ProtocolFamily);
        Assert.IsFalse(runtime.RuntimeDescriptor.Compaction!.Enabled, "The CLI compacts its context: CodeAlta does not summarize it.");
    }

    [TestMethod]
    public void HistoryPreamble_RendersWhatWasSaidBefore()
    {
        var arguments = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone();
        var preamble = ClaudeCodePrompts.CreateHistoryPreamble(
        [
            new(Agent.Runtime.AgentConversationRole.User, [new Agent.Runtime.AgentMessagePart.Text("fix the bug")]),
            new(Agent.Runtime.AgentConversationRole.Assistant, [new Agent.Runtime.AgentMessagePart.Reasoning("thinking"), new Agent.Runtime.AgentMessagePart.ToolCall("call_1", "read_file", arguments)]),
            new(Agent.Runtime.AgentConversationRole.Tool, [new Agent.Runtime.AgentMessagePart.ToolResult("call_1", new AgentToolResult(true, [new AgentToolResultItem.Text("1: content")]))]),
            new(Agent.Runtime.AgentConversationRole.Assistant, [new Agent.Runtime.AgentMessagePart.Text("fixed")]),
        ]);

        Assert.IsNotNull(preamble);
        StringAssert.Contains(preamble, "[user]\nfix the bug");
        StringAssert.Contains(preamble, "[assistant called tool read_file]");
        StringAssert.Contains(preamble, "1: content");
        StringAssert.Contains(preamble, "[assistant]\nfixed");
        Assert.IsFalse(preamble.Contains("thinking", StringComparison.Ordinal), "The reasoning of another model is not passed on.");
        Assert.IsNull(ClaudeCodePrompts.CreateHistoryPreamble([]));
    }

    private sealed class FailingTransportFactory : IClaudeCodeTransportFactory
    {
        public IClaudeCodeTransport Start(ClaudeCodeLaunch launch)
            => throw new InvalidOperationException($"Claude Code could not be started from '{launch.FileName}': access is denied.");
    }

    private static ClaudeCodeCliEnvironment CreateEnvironment(bool isWindows, string? path, params string[] files)
    {
        var existing = new HashSet<string>(files, StringComparer.Ordinal);
        // The separator of PATH is the one of the described system.
        var pathVariable = path is null ? null : path.Replace(Path.PathSeparator, isWindows ? ';' : ':');
        return new ClaudeCodeCliEnvironment(isWindows, pathVariable, Home, Path.Combine(Home, "AppData", "Roaming"), existing.Contains);
    }

    [TestMethod]
    public async Task Models_AreDescribedByWhatModelsDevKnowsOfTheAnthropicModelTheyRun()
    {
        await using var catalog = new CodeAlta.Agent.ModelCatalog.ModelsDevCatalogService();
        var models = new ClaudeCodeModelCatalog(new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code", ModelCatalog = catalog });
        var read = ClaudeCodeModelCatalog.ReadModels(JsonDocument.Parse("""
            {"models":[{"value":"sonnet","resolvedModel":"claude-sonnet-5-5","displayName":"Sonnet 5.5"},
                       {"value":"sonnet[1m]","resolvedModel":"claude-sonnet-5-5[1m]","displayName":"Sonnet 5.5 (1M context)"},
                       {"value":"claude-opus-5-5","displayName":"Opus 5.5"},
                       {"value":"experimental","resolvedModel":"claude-not-listed-anywhere","displayName":"Experimental"}]}
            """).RootElement);

        var sonnet = models.Describe(read[0]);
        Assert.AreEqual(("sonnet", "Sonnet 5.5"), (sonnet.Id, sonnet.DisplayName), "The entry keeps the name the CLI gave it.");
        Assert.AreEqual(("anthropic", "claude-sonnet-5-5"), (sonnet.Capabilities!["modelsDevProviderId"], sonnet.Capabilities["modelsDevModelId"]));
        Assert.IsTrue(sonnet.Capabilities.ContainsKey("contextWindowTokens") && sonnet.Capabilities.ContainsKey("family"));
        Assert.AreEqual(true, sonnet.Capabilities["supportsImageInput"], "What the CLI is known to take is kept.");

        // A larger context window is not the one models.dev lists for the model: its limits are left to the CLI.
        var large = models.Describe(read[1]);
        Assert.AreEqual("claude-sonnet-5-5", large.Capabilities!["modelsDevModelId"]);
        Assert.IsFalse(large.Capabilities.ContainsKey("contextWindowTokens") || large.Capabilities.ContainsKey("contextWindow"));

        Assert.AreEqual("claude-opus-5-5", models.Describe(read[2]).Capabilities!["modelsDevModelId"], "A model named by its own id is looked up by it.");
        Assert.AreSame(read[3], models.Describe(read[3]), "A model models.dev does not know stays as the CLI described it.");
        var bare = new ClaudeCodeModelCatalog(new ClaudeCodeModelProviderRuntimeOptions { ProviderKey = "claude-code" });
        Assert.AreSame(read[0], bare.Describe(read[0]));
    }
}
