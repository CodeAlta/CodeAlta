namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectFilePathFilterTests
{
    [TestMethod]
    public void AnItemMatchesItsNameOrPathAnywhere_AndWhatSuchAFolderHolds()
    {
        var filter = Parse("src/app, *.md, bin");

        Assert.IsFalse(filter.IsEmpty);
        Assert.IsTrue(filter.IsMatch("src/app/main.cs"));
        Assert.IsTrue(filter.IsMatch("web/src/app/deep/view.ts"));
        Assert.IsTrue(filter.IsMatch("readme.md"));
        Assert.IsTrue(filter.IsMatch("docs/guide/intro.md"));
        Assert.IsTrue(filter.IsMatch("bin/tool.dll"));
        Assert.IsTrue(filter.IsMatch("src/lib/bin/Debug/lib.dll"));
        // A name is a whole name: neither a longer one nor a part of one.
        Assert.IsFalse(filter.IsMatch("src/application/main.cs"));
        Assert.IsFalse(filter.IsMatch("src/other/main.cs"));
        Assert.IsFalse(filter.IsMatch("cabin/tool.dll"));
        Assert.IsFalse(filter.IsMatch("notes.mdx"));
    }

    [TestMethod]
    public void ItemsAreTrimmed_AndTakeEitherSlash()
    {
        var filter = Parse("  ./src\\app/ ,, *.ts ,");

        Assert.IsTrue(filter.IsMatch("src/app/main.cs"));
        Assert.IsTrue(filter.IsMatch("lib/index.ts"));
        Assert.IsFalse(filter.IsMatch("lib/index.tsx"));
    }

    [TestMethod]
    public void NamesCompareWithoutCase_OnlyWhereFileNamesDo()
    {
        Assert.AreEqual(OperatingSystem.IsWindows(), Parse("*.TS, Docs").IsMatch("src/a.ts"));
        Assert.AreEqual(OperatingSystem.IsWindows(), Parse("*.TS, Docs").IsMatch("docs/guide.md"));
        Assert.IsTrue(Parse("*.TS").IsMatch("src/a.TS"));
    }

    [TestMethod]
    public void AListWithNoItem_MatchesNothing()
    {
        foreach (var list in new[] { null, "", "   ", ",", " , ./ , / " })
        {
            var filter = Parse(list);
            Assert.IsTrue(filter.IsEmpty, list);
            Assert.IsFalse(filter.IsMatch("src/a.ts"), list);
        }
    }

    [TestMethod]
    public void AnItemThatIsNotAGlob_IsRefused()
    {
        Assert.IsFalse(ProjectFilePathFilter.TryParse("*.ts, [abc", out var filter));
        Assert.IsNull(filter);
        Assert.ThrowsExactly<ArgumentNullException>(() => Parse("*.ts").IsMatch(null!));
    }

    private static ProjectFilePathFilter Parse(string? list)
    {
        Assert.IsTrue(ProjectFilePathFilter.TryParse(list, out var filter), list);
        return filter;
    }
}
