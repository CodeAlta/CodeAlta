using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Which sessions the archive policies archive: those of their labels or of their automations, at the top of their family,
/// once old enough and beyond the most recent ones of their project.
/// </summary>
[TestClass]
public sealed class SessionArchivePolicySelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ALabelPolicy_ArchivesTheOldSessionsOfItsLabels_AndOfTheLabelsUnderThem()
    {
        var sessions = new[]
        {
            Session("old-audit", daysAgo: 10, labels: ["audit"]),
            Session("weekly", daysAgo: 9, labels: ["weekly"]),
            Session("new-audit", daysAgo: 1, labels: ["audit"]),
            Session("old-bug", daysAgo: 30, labels: ["bug"]),
            Session("old-unlabeled", daysAgo: 30),
            Session("archived", daysAgo: 30, labels: ["audit"], archived: true),
        };
        var expired = Select(sessions, Policy(labels: ["audit"], olderThanDays: 7));
        CollectionAssert.AreEqual(new[] { "old-audit", "weekly" }, expired.ToArray(), "The least recently active first; an archived session is left as it is.");
    }

    [TestMethod]
    public void KeepLast_KeepsTheMostRecentOfEachProject_AndWithAnAge_OnlyTheOldOnesBeyondThemGo()
    {
        var sessions = new[]
        {
            Session("a1", daysAgo: 1, labels: ["pr-review"], project: "a"),
            Session("a2", daysAgo: 2, labels: ["pr-review"], project: "a"),
            Session("a3", daysAgo: 20, labels: ["pr-review"], project: "a"),
            Session("b1", daysAgo: 30, labels: ["pr-review"], project: "b"),
        };
        CollectionAssert.AreEqual(new[] { "a3", "a2" }, Select(sessions, Policy(labels: ["pr-review"], keepLast: 1)).ToArray(), "b1 is the last of its project.");
        CollectionAssert.AreEqual(new[] { "a3" }, Select(sessions, Policy(labels: ["pr-review"], keepLast: 1, olderThanDays: 7)).ToArray());
        CollectionAssert.AreEqual(new[] { "a3" }, Select(sessions, Policy(labels: ["pr-review"], olderThanDays: 7), project: "a").ToArray(),
            "A policy of a project applies to its sessions only.");
    }

    [TestMethod]
    public void ChildSessions_FollowTheirParent_AndAnAutomationIsNamedByItsIdOrItsName()
    {
        var sessions = new[]
        {
            Session("run", daysAgo: 10, automation: "0193a5b2-0000-7000-8000-000000000001"),
            Session("child", daysAgo: 10, labels: ["audit"], parent: "run"),
            Session("orphan", daysAgo: 10, labels: ["audit"], parent: "gone"),
            Session("other-run", daysAgo: 10, automation: "0193a5b2-0000-7000-8000-000000000002"),
        };
        var names = new Dictionary<string, string> { ["0193a5b2-0000-7000-8000-000000000001"] = "Nightly review" };
        var expired = SessionArchivePolicyService.SelectExpired(sessions,
            [new(Policy(automations: ["nightly REVIEW"], olderThanDays: 7), null), new(Policy(labels: ["audit"], olderThanDays: 7), null)],
            id => [id], reference => names.ContainsKey(reference) ? reference : names.FirstOrDefault(pair => string.Equals(pair.Value, reference, StringComparison.OrdinalIgnoreCase)).Key, Now);
        CollectionAssert.AreEquivalent(new[] { "run", "orphan" }, expired.ToArray(),
            "A child is archived with its parent, never alone; one whose parent is not listed is at the top of its family.");
    }

    private static IReadOnlyList<string> Select(IReadOnlyList<SessionArchiveCandidate> sessions, SessionArchivePolicy policy, string? project = null)
        => SessionArchivePolicyService.SelectExpired(sessions, [new(policy, project)],
            id => id == "audit" ? ["audit", "weekly"] : [id], static _ => null, Now);

    private static SessionArchivePolicy Policy(string[]? labels = null, string[]? automations = null, int? olderThanDays = null, int? keepLast = null)
        => new(labels ?? [], automations ?? [], olderThanDays is { } days ? TimeSpan.FromDays(days) : null, keepLast);

    private static SessionArchiveCandidate Session(string id, int daysAgo, string[]? labels = null, string? project = "p", string? parent = null,
        string? automation = null, bool archived = false)
        => new(id, project, parent, labels ?? [], automation, Now.AddDays(-daysAgo), archived);
}
