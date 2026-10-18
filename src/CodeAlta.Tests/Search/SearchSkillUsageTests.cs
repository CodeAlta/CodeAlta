using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>A skill used much in the last days comes a little first among the skills that match about as well.</summary>
[TestClass]
public sealed class SearchSkillUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SearchFixture> FixtureAsync()
    {
        var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new SearchSessionInfo("s1", "A session", "p1", null, false);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState("skills", "1", null, null, 0, null, Now),
        [
            new SearchDocument("skill:user/deploy-staging", SearchKind.Skill, "deploy-staging", "Deploy the site to the staging server.", DateTimeOffset.UnixEpoch),
            new SearchDocument("skill:user/deploy-preview", SearchKind.Skill, "deploy-preview", "Deploy the site to the preview server.", DateTimeOffset.UnixEpoch),
            new SearchDocument("skill:user/pdf", SearchKind.Skill, "pdf", "Read and fill PDF forms.", DateTimeOffset.UnixEpoch),
        ]) { Complete = true });
        return fixture;
    }

    // Asked by a session of the project p1: its project decides which uses count most.
    private static SearchRequest Query(string text) => new(text, new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Skill } }) { Mode = SearchMode.Keyword, OriginSessionId = "s1" };

    [TestMethod]
    public async Task ASkillUsedMuch_ComesFirst_AmongThoseThatMatchAsWell_AndNeverMakesAHit()
    {
        await using var fixture = await FixtureAsync();
        var plain = await fixture.Service.SearchAsync(Query("deploy site server"));
        var order = plain.Hits.Select(static hit => hit.Title).ToArray();
        Assert.AreEqual(2, order.Length);
        var second = order[1];

        // The one that came second is used much: it comes first. The PDF skill, used even more, matches nothing.
        fixture.Host.SkillUsage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [second] = 12, ["pdf"] = 40 };
        var used = await fixture.Service.SearchAsync(Query("deploy site server"));
        CollectionAssert.AreEqual(new[] { second, order[0] }, used.Hits.Select(static hit => hit.Title).ToArray());
        Assert.AreEqual(("p1", "default"), fixture.Host.SkillUsageAsked[^1]);

        // A skill that matches clearly better stays first, however much the other is used.
        var exact = await fixture.Service.SearchAsync(Query("deploy-staging"));
        Assert.AreEqual("deploy-staging", exact.Hits[0].Title);
        fixture.Host.SkillUsage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["deploy-preview"] = 1000 };
        Assert.AreEqual("deploy-staging", (await fixture.Service.SearchAsync(Query("deploy-staging"))).Hits[0].Title);
    }

    [TestMethod]
    public async Task TheUsageIsNotAsked_ForAQueryWithoutSkills()
    {
        await using var fixture = await FixtureAsync();
        await fixture.Service.SearchAsync(new SearchRequest("deploy", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Tool } }));
        Assert.AreEqual(0, fixture.Host.SkillUsageAsked.Count);
    }
}
