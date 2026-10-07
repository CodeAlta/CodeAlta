using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;

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
        var expected = Path.Combine(Root, "tools", "claude");
        var environment = CreateEnvironment(isWindows: false, path: $"relative{Path.PathSeparator}{Path.Combine(Root, "usr", "bin")}{Path.PathSeparator}{Path.Combine(Root, "tools")}", expected);

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
            withTools: true);

        CollectionAssert.AreEqual(
            new[]
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--replay-user-messages", "--permission-prompt-tool", "stdio", "--permission-mode", "acceptEdits", "--model", "sonnet",
                "--effort", "xhigh", "--session-id=0f0e0d0c-0b0a-4908-8706-050403020100", "--mcp-config",
                """{"mcpServers":{"codealta":{"type":"sdk","name":"codealta"}}}""", "--allowedTools=mcp__codealta", "--add-dir", "/data",
            },
            launch.Arguments.ToArray());
        Assert.AreEqual("/bin/claude", launch.FileName);
        Assert.AreEqual("/work", launch.WorkingDirectory);
        Assert.AreEqual("codealta/1.2.3", launch.Environment["CLAUDE_AGENT_SDK_CLIENT_APP"]);
        Assert.AreEqual("1", launch.Environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"]);

        // Only the marks of a Claude Code session CodeAlta was started from are removed. The CLI authenticates with
        // what the user configured: no credential, endpoint or provider variable is set or removed.
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
        Assert.AreEqual("max", ClaudeCodeLauncher.ToEffort(AgentReasoningEffort.Max));
        Assert.AreEqual("claude-fable-5-1[1m]", ClaudeCodeLauncher.ToModelOption(" claude-fable-5-1[1m] "));
    }

    [TestMethod]
    public async Task Probe_ListsTheModelsTheCliOffers()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());

        var probe = await runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        CollectionAssert.AreEqual(new[] { "default", "sonnet", "haiku" }, probe.Models.Select(static model => model.Id).ToArray(), "An entry the CLI disables is not offered.");
        Assert.AreEqual("Default (recommended)", probe.Models[0].DisplayName);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh, AgentReasoningEffort.Max },
            probe.Models[0].SupportedReasoningEfforts!.ToArray());
        Assert.IsNull(probe.Models[2].SupportedReasoningEfforts);
        StringAssert.Contains(probe.StatusMessage, "/fake/bin/claude");
        StringAssert.Contains(probe.StatusMessage, "max");
        Assert.IsFalse(probe.StatusMessage!.Contains("someone@example.test", StringComparison.Ordinal), "The identity of the account stays in the CLI.");

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
        Assert.AreEqual("claude-code-unavailable", probe.ErrorCategory);
        StringAssert.Contains(probe.StatusMessage, "was not found");
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
        CollectionAssert.AreEqual(new[] { "claude-custom-9" }, (await pinnedRuntime.ProbeAsync()).Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual("claude-custom-9", pinnedRuntime.Descriptor.DefaultModelId);
    }

    [TestMethod]
    public void Account_IsSignedInWithAnyMethodTheCliSupports()
    {
        static (bool IsSignedIn, string? Summary) Read(string account) => ClaudeCodeModelCatalog.ReadAccount(JsonDocument.Parse($$"""{"account":{{account}}}""").RootElement);

        Assert.AreEqual((false, null), Read("""{"tokenSource":"none","apiProvider":"firstParty"}"""));
        Assert.AreEqual((true, "pro"), Read("""{"tokenSource":"claude.ai","subscriptionType":"pro","apiProvider":"firstParty"}"""));
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

    private static ClaudeCodeCliEnvironment CreateEnvironment(bool isWindows, string? path, params string[] files)
    {
        var existing = new HashSet<string>(files, StringComparer.Ordinal);
        // The separator of PATH is the one of the described system.
        var pathVariable = path is null ? null : path.Replace(Path.PathSeparator, isWindows ? ';' : ':');
        return new ClaudeCodeCliEnvironment(isWindows, pathVariable, Home, Path.Combine(Home, "AppData", "Roaming"), existing.Contains);
    }
}
