using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The providers of a node that sign in with an account, signed in from the desktop (<c>doc/fleet/node.md</c>, credentials on a
/// node): Mistral Vibe signs in on a page any browser opens, while the node asks the console until it is done and keeps the key.
/// </summary>
[TestClass]
public sealed class NodeProviderSignInTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MistralVibe_SignsInOnAPageOfTheDesktop_WhileTheNodeWaitsAndKeepsTheKey()
    {
        var token = TestContext.CancellationToken;
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), token);
        new CodeAltaConfigStore(node.Host.CatalogOptions).SaveGlobalProviderDefinitions(
            [new CodeAltaProviderDocument { ProviderKey = "vibe", ProviderType = "mistral-vibe", Enabled = false }]);
        var consoleSaid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? mode = null;
        var signIns = new NodeSignIns(node.Host, new NodeProviders(node.Host, null))
        {
            // The console of Mistral: the page of the sign-in, then the end of the wait of the node once the person signed in there.
            ProviderLogin = async (definition, _, way, onPrompt, cancellationToken) =>
            {
                mode = way;
                await onPrompt(new ProviderLoginPrompt(new Uri("https://console.mistral.ai/vibe/sign-in/p1"), null, DateTimeOffset.UtcNow.AddMinutes(10)), cancellationToken);
                await consoleSaid.Task.WaitAsync(cancellationToken);
                return new ProviderLoginStatus(true, null, null, null);
            },
        };

        var item = (await signIns.ListAsync(token)).Items.Single(static value => value.Key == "vibe");
        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Page }, item.Ways.ToArray(), "its page signs in from any browser: nothing comes back to the machine");
        Assert.IsNull(item.Detail);

        var prompt = await signIns.StartAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "vibe"), token);
        Assert.AreEqual((FleetMachineSignInWays.Page, "https://console.mistral.ai/vibe/sign-in/p1", (string?)null, (string?)null),
            (prompt.Way, prompt.Page, prompt.Redirect, prompt.UserCode));
        Assert.AreEqual("browser", mode, "the browser sign-in of Mistral Vibe, its verifier kept on the node");
        Assert.AreEqual(FleetMachineSignInStates.Waiting, (await signIns.WaitAsync(prompt.AttemptId, TimeSpan.Zero, token)).Status);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => signIns.CompleteAsync(new FleetMachineSignInCompleteParams(prompt.AttemptId, null, "code"), token),
            "nothing is given back to a sign-in on a page");

        consoleSaid.SetResult();
        Assert.AreEqual(FleetMachineSignInStates.Completed, (await signIns.WaitAsync(prompt.AttemptId, TimeSpan.FromSeconds(10), token)).Status);
        Assert.IsTrue(new CodeAltaConfigStore(node.Host.CatalogOptions).LoadGlobalProviderDefinitions(includeDisabled: true).Single().Enabled != false,
            "a provider signed in is turned on");

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => signIns.StartAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "vibe", Way: FleetMachineSignInWays.Browser), token),
            "no redirect comes back to the desktop");
    }
}
