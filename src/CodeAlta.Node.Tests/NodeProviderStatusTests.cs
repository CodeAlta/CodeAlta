using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node.Tests;

/// <summary>
/// What the page of a machine says of a provider that is not ready, and how the Claude Code of a node is found to sign it in
/// (#414): never "its log says why" for a log that says nothing, never a terminal on a machine nobody sits at.
/// </summary>
[TestClass]
public sealed class NodeProviderStatusTests
{
    public TestContext TestContext { get; set; } = null!;

    // A provider written into the configuration after the node started its providers can run: the node has not applied it yet. One
    // whose environment names a secret the machine does not have names that secret.
    [TestMethod]
    public async Task AProviderTheNodeHasNotStarted_SaysWhy()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, "[providers.claude]\ntype = \"claude-code\"\n\n"
            + "[providers.claude-token]\ntype = \"claude-code\"\nenv = { CLAUDE_CODE_OAUTH_TOKEN = \"${secret:CLAUDE_TOKEN_GONE}\" }\n");

        var providers = (await new NodeProviders(node.Host, null).ListAsync(token)).Providers;

        var written = providers.Single(static provider => provider.Key == "claude");
        Assert.AreEqual((FleetProviderStates.NotReady, FleetProviderReasons.Pending, "cli"), (written.State, written.Reason, written.Credential));
        var named = providers.Single(static provider => provider.Key == "claude-token");
        Assert.AreEqual((FleetProviderReasons.SecretMissing, "CLAUDE_TOKEN_GONE"), (named.Reason, named.Detail));
    }

    // The sign-in runs the Claude Code the sessions of the provider run: its command here, not only a claude on the PATH of the node.
    [TestMethod]
    public async Task ClaudeCode_SignsInWithTheCliItsSessionsRun()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/script")) Assert.Inconclusive("claude setup-token runs under script on macOS and Linux.");
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        var folder = Directory.CreateDirectory(Path.Combine(root.Root, "elsewhere")).FullName;
        var claude = Path.Combine(folder, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(claude, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(node.Host.CatalogOptions.ConfigPath, $"[providers.claude]\ntype = \"claude-code\"\ncommand = '{claude}'\n");

        var item = (await new NodeSignIns(node.Host, new NodeProviders(node.Host, null)).ListAsync(token)).Items.Single(static value => value.Key == "claude");

        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Code }, item.Ways.ToArray(), item.Detail);
    }
}