using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OwnedPermissionDefaultsTests
{
    [TestMethod]
    public async Task OwnedAutoApprovalIsExplicitAndHonorsCancellation()
    {
        await using var restricted = new SessionPermissionService();
        await using var automatic = new SessionPermissionService(autoApproveOwnedPermissions: true);
        AgentPermissionRequest[] requests = [
            new AgentCommandPermissionRequest(new ModelProviderId("test"), "session", DateTimeOffset.UtcNow,
                null, "command", null, "write a file", "project", null, null, null, null, null),
            new AgentFileChangePermissionRequest(new ModelProviderId("test"), "session", DateTimeOffset.UtcNow,
                null, "file", null, null),
        ];
        foreach (var request in requests)
        {
            Assert.AreEqual(AgentPermissionDecisionKind.Deny,
                (await restricted.OwnedDefaultPermissionHandler(request, CancellationToken.None)).Kind);
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce,
                (await automatic.OwnedDefaultPermissionHandler(request, CancellationToken.None)).Kind);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel,
                (await automatic.OwnedDefaultPermissionHandler(request, new CancellationToken(true))).Kind);
        }
        await automatic.DisposeAsync();
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel,
            (await automatic.OwnedDefaultPermissionHandler(requests[0], CancellationToken.None)).Kind);
    }
}
