using CodeAlta.Tui.App.Context;

namespace CodeAlta.Tests;

[TestClass]
public sealed class WorkspaceRefreshContextTests
{
    [TestMethod]
    public void RefreshOperations_PublishTypedReasons()
    {
        var requests = new List<WorkspaceRefreshRequest>();
        var context = new WorkspaceRefreshContext(requests.Add);

        context.ApplySessionUsageProjection();
        context.ApplyHeaderProjection();

        CollectionAssert.AreEqual(
            new[]
            {
                new WorkspaceRefreshRequest(WorkspaceRefreshReason.SelectedSessionUsageInvalidated),
                new WorkspaceRefreshRequest(WorkspaceRefreshReason.HeaderAndSessionWorkspace),
            },
            requests);
    }
}
