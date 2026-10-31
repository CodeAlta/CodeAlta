using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the page is given of the cloud sessions: refusals without an owned host, and the view of the Explorer.</summary>
[TestClass]
public sealed class CloudSessionsRpcTests
{
    [TestMethod]
    public async Task AWindowWithoutAnOwnedHost_RefusesEveryRequest()
    {
        var service = new CloudSessionsService();
        Assert.AreEqual("unconfigured", (await service.ReadinessAsync(new("e", "p", "claude"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await service.StartAsync(new("e", Guid.NewGuid().ToString(), "p", "claude", null, null, "Fix it"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", service.StartState(new("e", Guid.NewGuid().ToString())).Status);
        Assert.AreEqual("unconfigured", service.CancelStart(new("e", Guid.NewGuid().ToString())).Status);
        Assert.AreEqual("unconfigured", (await service.SendAsync(new("e", "s", "text"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await service.ContinueHereAsync(new("e", "s", false, false), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await service.DetailsAsync(new("e", "s"), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await service.BringHereAsync(new("e", "p", "claude", null, "session_01ABCDEFGHJK"), CancellationToken.None)).Status);
    }

    [TestMethod]
    public void TheExplorer_IsGivenTheCloudSession_WithOnlyAnAddressOfClaudeAi()
    {
        Assert.IsNull(WorkspaceSessionCloud.Of(null));
        var cloud = WorkspaceSessionCloud.Of(new AgentSessionCloud("session_01ABCDEFGHJK", "https://claude.ai/code/session_01ABCDEFGHJK", "Fix it", false, true))!;
        Assert.AreEqual(("session_01ABCDEFGHJK", "https://claude.ai/code/session_01ABCDEFGHJK", "Fix it", false, true),
            (cloud.Id, cloud.Url, cloud.Title, cloud.Continued, cloud.Continuing));
        Assert.IsNull(WorkspaceSessionCloud.Of(new AgentSessionCloud("session_01ABCDEFGHJK", "javascript:alert(1)", null, false, false))!.Url,
            "The page opens the address in the browser: only one of claude.ai.");
    }
}
