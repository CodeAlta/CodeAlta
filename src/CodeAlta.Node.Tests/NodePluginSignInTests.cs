using CodeAlta.Agent.Harness;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The providers of plugins signed in from the desktop (<c>doc/fleet/node.md</c>, credentials on a node): a sign-in that shows a
/// code runs on the node and the desktop shows the code; one that needs a browser or a terminal there is signed in on the machine.
/// </summary>
[TestClass]
public sealed class NodePluginSignInTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AProviderOfAPlugin_SignsInWithTheCodeItShows_OnTheNode()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.cli]\ntype = \"fake-agent\"\n\n[providers.terminal]\ntype = \"terminal-agent\"\n");
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var account = new CodeAccount();
        var types = new PluginModelProviderTypes(endpoints);
        types.Attach(() => [Contribution("fake-agent", account), Contribution("terminal-agent", new TerminalAccount())]);
        var signIns = new NodeSignIns(node.Host, new NodeProviders(node.Host, null)) { PluginProviderTypes = types };

        var items = (await signIns.ListAsync(token)).Items.Where(static item => item.Kind == FleetMachineSignInKinds.Provider).ToArray();
        var cli = items.Single(static item => item.Key == "cli");
        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Device }, cli.Ways.ToArray());
        Assert.AreEqual(FleetMachineSignInStates.Unknown, cli.State, "the node does not know whether a plugin is signed in");
        var terminal = items.Single(static item => item.Key == "terminal");
        Assert.AreEqual(0, terminal.Ways.Count, "a sign-in in a terminal of the machine does not run from another computer");
        Assert.AreEqual("It signs in in a terminal of the machine: run agent login there.", terminal.Detail, "the list says the command to run there");

        var prompt = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "cli", null, null), token);
        Assert.AreEqual((FleetMachineSignInWays.Device, "https://auth.example.com/device", "WXYZ-1234"), (prompt.Way, prompt.Page, prompt.UserCode));
        Assert.AreEqual("chatgpt-device", account.Method, "the method that shows a code, never the one of the browser of the node");
        account.Done.SetResult();
        var ended = await signIns.WaitAsync(prompt.AttemptId, TimeSpan.FromSeconds(10), token);
        Assert.AreEqual(FleetMachineSignInStates.Completed, ended.Status);

        // A sign-out while a sign-in of the target waits ends that sign-in first: it cannot store its credential afterwards.
        var waiting = new CodeAccount();
        types.Attach(() => [Contribution("fake-agent", waiting), Contribution("terminal-agent", new TerminalAccount())]);
        var pending = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "cli", null, null), token);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => signIns.SignOutAsync(new FleetMachineSignInTarget(FleetMachineSignInKinds.Provider, "cli", null), token),
            "a provider of a plugin keeps its own login");
        Assert.AreEqual((FleetMachineSignInStates.Failed, "canceled"), ((await signIns.WaitAsync(pending.AttemptId, TimeSpan.FromSeconds(10), token)) is var state ? (state.Status, state.Code) : default));

        var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => signIns.StartAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "terminal", null, null), token));
        StringAssert.Contains(refused.Message, "run agent login there");
    }

    // An ACP agent that reads an API key from its environment (env_var): the person types it on the desktop, the node keeps it as a
    // secret of the machine and names it in the env of the provider, whose runtime is given the value; never written in the clear.
    [TestMethod]
    public async Task AnAgentThatReadsAKeyFromItsEnvironment_IsGivenItFromTheDesktop_AsASecretOfTheMachine()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.agent]\ntype = \"key-agent\"\ndisplay_name = \"Agent\"\nenv = { MODE = \"fast\" }\n\n"
            + "[providers.browser]\ntype = \"browser-agent\"\n");
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var types = new PluginModelProviderTypes(endpoints);
        PluginProviderSettings? started = null;
        types.Attach(() => [Contribution("key-agent", new KeyAccount()) with { CreateRuntime = context => { started = context.Settings; throw new NotSupportedException("No runtime."); } },
            Contribution("browser-agent", new BrowserAccount())]);
        var signIns = new NodeSignIns(node.Host, new NodeProviders(node.Host, null)) { PluginProviderTypes = types };

        var items = (await signIns.ListAsync(token)).Items.Where(static item => item.Kind == FleetMachineSignInKinds.Provider).ToArray();
        var agent = items.Single(static item => item.Key == "agent");
        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Secret }, agent.Ways.ToArray());
        Assert.AreEqual((FleetMachineSignInStates.Unknown, "Agent"), (agent.State, agent.Title));
        var browser = items.Single(static item => item.Key == "browser");
        Assert.AreEqual(0, browser.Ways.Count);
        StringAssert.Contains(browser.Detail, "comes back to that machine only", "a browser on the loopback of the machine is said plainly");

        var prompt = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "agent"), token);
        Assert.AreEqual((FleetMachineSignInWays.Secret, "https://platform.example.com/api-keys", "API key (OPENAI_API_KEY)"), (prompt.Way, prompt.Page, prompt.Ask));
        var ended = await signIns.CompleteAsync(new FleetMachineSignInCompleteParams(prompt.AttemptId, null, "sk-typed-on-the-desktop"), token);
        Assert.AreEqual(FleetMachineSignInStates.Completed, ended.Status);

        var name = NodeSignIns.ProviderSecretName("OPENAI_API_KEY", "agent");
        var config = File.ReadAllText(node.Host.CatalogOptions.ConfigPath);
        StringAssert.Contains(config, $"OPENAI_API_KEY = \"${{secret:{name}}}\"");
        StringAssert.Contains(config, "MODE = \"fast\"", "the rest of its environment stays");
        Assert.IsFalse(config.Contains("sk-typed", StringComparison.Ordinal), "the value is a secret, never written in the configuration");
        Assert.IsTrue(CodeAlta.Catalog.Secrets.SecretReferenceResolver.ForMachine(node.Host.CatalogOptions.GlobalRoot)
            .TryGet(new CodeAlta.Catalog.Secrets.SecretReference(CodeAlta.Catalog.Secrets.SecretReferenceKind.Secret, name), out var kept));
        Assert.AreEqual("sk-typed-on-the-desktop", kept);
        // Registered again by its plugin: its runtime is given the value.
        try { _ = await node.Host.ModelProviderRegistry.GetOrCreateRuntimeAsync(new CodeAlta.Agent.ModelProviderId("agent")); }
        catch (Exception exception) when (exception is not OperationCanceledException) { /* No runtime in this test: its settings are what is checked. */ }
        Assert.AreEqual("sk-typed-on-the-desktop", started!.GetMap("env")["OPENAI_API_KEY"]);

        Assert.AreEqual(FleetMachineSignInStates.SignedIn, (await signIns.ListAsync(token)).Items.Single(static item => item.Key == "agent").State);
        var signedOut = await signIns.SignOutAsync(new FleetMachineSignInTarget(FleetMachineSignInKinds.Provider, "agent"), token);
        Assert.AreEqual(FleetMachineSignInStates.Unknown, signedOut.State);
        Assert.IsFalse(File.ReadAllText(node.Host.CatalogOptions.ConfigPath).Contains("OPENAI_API_KEY", StringComparison.Ordinal));
        Assert.IsFalse(new CodeAlta.Catalog.Secrets.LocalSecretStore(node.Host.CatalogOptions.GlobalRoot).ListSecrets().Any(secret => secret.Name == name));
    }

    // Claude Code keeps a token of the account as a secret of the machine, which its CLAUDE_CODE_OAUTH_TOKEN names; signing it out
    // from the desktop takes both away.
    [TestMethod]
    public async Task ClaudeCode_IsSignedInByItsTokenSecret_AndSignedOutFromTheDesktop()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.claude]\ntype = \"claude-code\"\n");
        var providers = new NodeProviders(node.Host, null);
        var signIns = new NodeSignIns(node.Host, providers) { ClaudeExecutable = Path.Combine(root.Root, "missing", "claude.exe") };

        var item = (await signIns.ListAsync(token)).Items.Single(static value => value.Key == "claude");
        Assert.AreEqual(FleetMachineSignInStates.Unknown, item.State, "the CLI may keep its own login on the machine");
        Assert.AreEqual(0, item.Ways.Count);
        StringAssert.Contains(item.Detail, "not installed");

        var name = NodeSignIns.ClaudeSecretName("claude");
        new CodeAlta.Catalog.Secrets.LocalSecretStore(node.Host.CatalogOptions.GlobalRoot).SetSecret(name, "sk-ant-oat01-test-token-value-0123456789", "test");
        Assert.IsTrue(providers.SetEnvironment("claude", NodeSignIns.ClaudeTokenVariable, $"${{secret:{name}}}"));
        item = (await signIns.ListAsync(token)).Items.Single(static value => value.Key == "claude");
        Assert.AreEqual(FleetMachineSignInStates.SignedIn, item.State);
        Assert.IsNotNull(item.ExpiresAt, "a token of claude setup-token lasts a year");

        var signedOut = await signIns.SignOutAsync(new FleetMachineSignInTarget(FleetMachineSignInKinds.Provider, "claude", null), token);
        Assert.AreEqual(FleetMachineSignInStates.Unknown, signedOut.State);
        Assert.IsFalse(new CodeAlta.Catalog.Secrets.LocalSecretStore(node.Host.CatalogOptions.GlobalRoot).ListSecrets().Any(secret => secret.Name == name));
        Assert.IsFalse(File.ReadAllText(node.Host.CatalogOptions.ConfigPath).Contains(NodeSignIns.ClaudeTokenVariable, StringComparison.Ordinal));
    }

    // On Windows, claude setup-token runs in a pseudoconsole of the node: what it prints there gives the page, the code the person
    // pastes on the desktop is typed into it, and the token it prints becomes a secret of the machine. A script of cmd.exe plays it.
    [TestMethod]
    public async Task ClaudeCode_SignsInOnAWindowsMachine_InAPseudoconsoleOfTheNode()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The pseudoconsole of Windows; macOS and Linux run it under script.");
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.claude]\ntype = \"claude-code\"\n");
        var claude = Path.Combine(root.Root, "fake-claude", "claude.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(claude)!);
        File.WriteAllText(claude, string.Join("\r\n",
            "@echo off",
            "if not \"%1\"==\"setup-token\" exit /b 2",
            "echo Browser didn't open? Use the url below to sign in:",
            "echo.",
            "echo https://claude.ai/oauth/authorize?code=true^&client_id=x^&state=s",
            "echo.",
            "set /p CODE=Paste code here if prompted ^> ",
            "echo.",
            "echo sk-ant-oat01-ABCDEFGHIJKLMNOPQRSTUVWXYZ_%CODE%",
            "echo.",
            "echo Store this token securely.",
            "pause >nul",
            ""));
        var signIns = new NodeSignIns(node.Host, new NodeProviders(node.Host, null)) { ClaudeExecutable = claude };

        var item = (await signIns.ListAsync(token)).Items.Single(static value => value.Key == "claude");
        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Code }, item.Ways.ToArray(), "a Windows machine signs Claude Code in from the desktop too");
        var prompt = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "claude"), token);
        Assert.AreEqual((FleetMachineSignInWays.Code, "https://claude.ai/oauth/authorize?code=true&client_id=x&state=s"), (prompt.Way, prompt.Page));
        var ended = await signIns.CompleteAsync(new FleetMachineSignInCompleteParams(prompt.AttemptId, null, "pasted42"), token);
        if (ended.Status == FleetMachineSignInStates.Waiting) ended = await signIns.WaitAsync(prompt.AttemptId, TimeSpan.FromSeconds(20), token);
        Assert.AreEqual(FleetMachineSignInStates.Completed, ended.Status, ended.Message);

        var name = NodeSignIns.ClaudeSecretName("claude");
        Assert.IsTrue(CodeAlta.Catalog.Secrets.SecretReferenceResolver.ForMachine(node.Host.CatalogOptions.GlobalRoot)
            .TryGet(new CodeAlta.Catalog.Secrets.SecretReference(CodeAlta.Catalog.Secrets.SecretReferenceKind.Secret, name), out var kept));
        Assert.AreEqual("sk-ant-oat01-ABCDEFGHIJKLMNOPQRSTUVWXYZ_pasted42", kept, "the code pasted on the desktop was typed into the console");
        StringAssert.Contains(File.ReadAllText(node.Host.CatalogOptions.ConfigPath), $"${{secret:{name}}}");
    }

    private static PluginModelProviderContribution Contribution(string type, IPluginProviderAccount account) => new()
    {
        ProviderType = type,
        DisplayName = type,
        Account = account,
        CreateRuntime = static _ => throw new NotSupportedException("No runtime in this test."),
    };

    // A plugin with a browser sign-in and one that shows a code, as Codex CLI.
    private sealed class CodeAccount : IPluginProviderAccount
    {
        public string? Method { get; private set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>([new("chatgpt", "Browser", null), new("chatgpt-device", "Code", null) { ShowsCode = true }]);

        public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public async Task SignInAsync(PluginProviderSettings settings, string methodId, Func<PluginProviderSignInPrompt, CancellationToken, ValueTask> onPrompt,
            CancellationToken cancellationToken)
        {
            Method = methodId;
            await onPrompt(new PluginProviderSignInPrompt(new Uri("https://auth.example.com/device"), "WXYZ-1234", null), cancellationToken);
            await Done.Task.WaitAsync(cancellationToken);
        }
    }

    // An ACP agent that reads its API key from its environment (env_var), and one optional variable.
    private sealed class KeyAccount : IPluginProviderAccount
    {
        public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>([new("browser", "Browser", null), new("openai", "OpenAI API key", null)
            {
                Variables = [new("OPENAI_API_KEY", "API key"), new("OPENAI_ORG", null) { Optional = true, Secret = false }],
                EnvironmentSetting = "env",
                VariablesPage = new Uri("https://platform.example.com/api-keys"),
            }]);

        public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken) => throw new InvalidOperationException("Nothing runs for a key.");
    }

    // An ACP agent that signs in with a browser it opens on its own machine.
    private sealed class BrowserAccount : IPluginProviderAccount
    {
        public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>([new("oauth", "Log in with Google", null)]);

        public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    // A plugin whose sign-in is a command of a terminal, as an ACP agent.
    private sealed class TerminalAccount : IPluginProviderAccount
    {
        public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>([new("login", "Sign in", null) { TerminalCommand = ["agent", "login"] }]);

        public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
