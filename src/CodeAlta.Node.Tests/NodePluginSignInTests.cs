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

        var prompt = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "cli", null, null), token);
        Assert.AreEqual((FleetMachineSignInWays.Device, "https://auth.example.com/device", "WXYZ-1234"), (prompt.Way, prompt.Page, prompt.UserCode));
        Assert.AreEqual("chatgpt-device", account.Method, "the method that shows a code, never the one of the browser of the node");
        account.Done.SetResult();
        var ended = await signIns.WaitAsync(prompt.AttemptId, TimeSpan.FromSeconds(10), token);
        Assert.AreEqual(FleetMachineSignInStates.Completed, ended.Status);

        var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => signIns.StartAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "terminal", null, null), token));
        StringAssert.Contains(refused.Message, "on the machine itself");
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

    // A plugin whose sign-in is a command of a terminal, as an ACP agent.
    private sealed class TerminalAccount : IPluginProviderAccount
    {
        public Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PluginProviderSignInMethod>>([new("login", "Sign in", null) { TerminalCommand = ["agent", "login"] }]);

        public Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
