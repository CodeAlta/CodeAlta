using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionLineageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static AgentSessionMetadata Session(string id, int minute, string? parent = null, string? createdBy = null, string? viewParent = null)
        => new(id, Start.AddMinutes(minute), Start.AddMinutes(minute), ParentSessionId: parent, CreatedBySessionId: createdBy,
            ViewState: viewParent is null ? null : new AgentSessionViewStateMetadata(ParentSessionId: viewParent));

    [TestMethod]
    public void EveryLinkMakesAChild_AndTheTreeIsReadFromTheTopOldestFirst()
    {
        var sessions = new[]
        {
            Session("root", 0),
            Session("late-child", 5, parent: "root"),
            Session("early-child", 1, createdBy: "root"),
            Session("grandchild", 2, viewParent: "early-child"),
            Session("great-grandchild", 3, parent: "GRANDCHILD"),
            Session("unrelated", 4, parent: "elsewhere"),
        };
        var tree = SessionLineage.Descendants("root", sessions);
        CollectionAssert.AreEqual(new[]
        {
            ("early-child", "root", 1), ("grandchild", "early-child", 2), ("great-grandchild", "grandchild", 3), ("late-child", "root", 1),
        }, tree.Select(descendant => (descendant.Session.SessionId, descendant.ParentSessionId, descendant.Depth)).ToArray());
        Assert.IsEmpty(SessionLineage.Descendants("unrelated", sessions));
    }

    [TestMethod]
    public void ASessionReachedTwiceIsListedOnceUnderTheNearest_AndACycleEnds()
    {
        var sessions = new[]
        {
            Session("root", 0, parent: "loop"),
            Session("child", 1, parent: "root"),
            // Created by the root, its parent recorded as the child: it is shown under the root, the nearer of the two.
            Session("both", 2, parent: "child", createdBy: "root"),
            Session("loop", 3, parent: "child"),
            Session("self", 4, parent: "self"),
        };
        var tree = SessionLineage.Descendants("root", sessions);
        CollectionAssert.AreEqual(new[] { ("both", "root", 1), ("child", "root", 1), ("loop", "child", 2) },
            tree.Select(descendant => (descendant.Session.SessionId, descendant.ParentSessionId, descendant.Depth)).OrderBy(value => value.Item3)
                .ThenBy(value => value.Item1).ToArray());
        Assert.IsFalse(tree.Any(descendant => descendant.Session.SessionId == "root"), "The session is never its own descendant.");
        Assert.IsEmpty(SessionLineage.Descendants("self", sessions));
        Assert.IsFalse(SessionLineage.IsChildOf(sessions[4], "self"));
    }
}
