using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>How the sources that change in place are kept fresh: stamps first, then the hash of each text.</summary>
[TestClass]
public sealed class SearchSourcesTests
{
    [TestMethod]
    public async Task AFolder_IsReadWhenItsStampChanges_AndOnlyChangedTextsAreWritten()
    {
        await using var fixture = new SearchFixture();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "plans")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "# Alpha\n\nThe pelican plan.");
        File.WriteAllText(Path.Combine(folder, "b.md"), "# Beta\n\nThe heron plan.");
        var source = new FolderSearchSource("plans:p1", [(folder, SearchKind.Plan, "plan")],
            (path, kind, text) => new SearchDocument("plan:p1/" + Path.GetFileNameWithoutExtension(path), kind, Path.GetFileName(path), text, DateTimeOffset.UtcNow) { ProjectId = "p1" });

        Assert.AreEqual(2, await ApplyAsync(fixture.Store, source));
        // Nothing changed: the stamp says so and no file is read.
        Assert.IsNull(await source.ReadAsync((await fixture.Store.GetSourceAsync("plans:p1"))!.Stamp, default));

        // Touched without a change (a checkout, a copy): read again, and nothing is written.
        File.SetLastWriteTimeUtc(Path.Combine(folder, "a.md"), DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(0, await ApplyAsync(fixture.Store, source));

        // One file changed: only it is written.
        File.WriteAllText(Path.Combine(folder, "b.md"), "# Beta\n\nThe heron plan, with a stork.");
        Assert.AreEqual(1, await ApplyAsync(fixture.Store, source));
        Assert.HasCount(1, (await fixture.SearchAsync("stork")).Hits);

        // A file deleted: its item goes.
        File.Delete(Path.Combine(folder, "a.md"));
        Assert.AreEqual(0, await ApplyAsync(fixture.Store, source));
        Assert.IsEmpty((await fixture.SearchAsync("pelican")).Hits);
        Assert.AreEqual(1, (await fixture.Store.CountAsync()).Items[SearchKind.Plan]);
    }

    [TestMethod]
    public async Task TheGuideOfANewVersion_WritesItsNewAndChangedPagesOnly()
    {
        await using var fixture = new SearchFixture();
        var pages = new List<SearchDocument>
        {
            new("doc:a.md", SearchKind.Doc, "Alpha", "The first page.", DateTimeOffset.UnixEpoch),
            new("doc:b.md", SearchKind.Doc, "Beta", "The second page.", DateTimeOffset.UnixEpoch),
        };
        var source = new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. pages]));
        Assert.AreEqual(2, await ApplyAsync(fixture.Store, source));

        // The same guide again (a dev build without a new version, a start): nothing to do.
        Assert.IsNull(await source.ReadAsync((await fixture.Store.GetSourceAsync("docs"))!.Stamp, default));

        // A page changed and a page added by a new version: those two are written, the other is kept as it is.
        pages[1] = pages[1] with { Text = "The second page, rewritten." };
        pages.Add(new("doc:c.md", SearchKind.Doc, "Gamma", "A page of the new version.", DateTimeOffset.UnixEpoch));
        Assert.AreEqual(2, await ApplyAsync(fixture.Store, source));
        Assert.HasCount(1, (await fixture.SearchAsync("rewritten")).Hits);
        Assert.AreEqual(3, (await fixture.Store.CountAsync()).Items[SearchKind.Doc]);
    }

    private static async Task<int> ApplyAsync(SearchIndexStore store, ISearchSource source)
    {
        var previous = await store.GetSourceAsync(source.Key);
        var read = await source.ReadAsync(previous?.Stamp, default);
        if (read is null) return 0;
        return await store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState(source.Key, read.Stamp, null, null, 0, null, DateTimeOffset.UtcNow), read.Documents) { Complete = true });
    }
}
