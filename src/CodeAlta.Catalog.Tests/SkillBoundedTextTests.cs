using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillBoundedTextTests
{
    [TestMethod]
    public async Task BoundedTextRead_ExactByteLimitAndBomMatchOrdinarySkillText()
    {
        using var temp = new TestRoot();
        var skillFile = Path.Combine(temp.Path, "SKILL.md");
        var content = "---\nname: sample\ndescription: café\n---\n# Sample\n";
        await File.WriteAllTextAsync(skillFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var bytes = await File.ReadAllBytesAsync(skillFile);

        var complete = await SkillBoundedTextReader.ReadAsync(skillFile, bytes.Length, CancellationToken.None);
        Assert.AreEqual(SkillBoundedReadStatus.Complete, complete.Status);
        Assert.AreEqual(await File.ReadAllTextAsync(skillFile), complete.Content);

        var truncated = await SkillBoundedTextReader.ReadAsync(skillFile, bytes.Length - 1, CancellationToken.None);
        Assert.AreEqual(SkillBoundedReadStatus.TooLarge, truncated.Status);
        Assert.IsNull(truncated.Content);

        await File.WriteAllTextAsync(skillFile, new string('x', 100_000));
        var oversized = await SkillBoundedTextReader.ReadAsync(skillFile, 20_000, CancellationToken.None);
        Assert.AreEqual(SkillBoundedReadStatus.TooLarge, oversized.Status);
        Assert.IsNull(oversized.Content);
    }

    [TestMethod]
    public async Task BoundedTextRead_DistinguishesMissingReadErrorAndCancellation()
    {
        using var temp = new TestRoot();
        var missing = await SkillBoundedTextReader.ReadAsync(Path.Combine(temp.Path, "missing"), 64, CancellationToken.None);
        Assert.AreEqual(SkillBoundedReadStatus.Missing, missing.Status);
        var error = await SkillBoundedTextReader.ReadAsync(temp.Path, 64, CancellationToken.None);
        Assert.AreEqual(SkillBoundedReadStatus.ReadError, error.Status);
        Assert.IsNull(error.Content);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await SkillBoundedTextReader.ReadAsync(temp.Path, 64, canceled.Token));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await SkillBoundedTextReader.ReadAsync(temp.Path, SkillBoundedTextReader.MaximumBytes + 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedDisabledNames_EquivalentToExistingForCompleteGlobalAndProjectConfigs()
    {
        using var temp = new TestRoot();
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, ".alta"));
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot });
        var globalText = "[skills]\ndisabled = [\"alpha\", \"beta\"]\n";
        var projectText = "[skills]\ndisabled = [\"gamma\"]\n";
        await File.WriteAllTextAsync(Path.Combine(globalRoot, "config.toml"), globalText);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, ".alta", "config.toml"), projectText);

        var global = await store.LoadGlobalDisabledSkillNamesBoundedAsync(Encoding.UTF8.GetByteCount(globalText));
        var project = await store.LoadProjectDisabledSkillNamesBoundedAsync(projectRoot, Encoding.UTF8.GetByteCount(projectText));
        Assert.AreEqual(SkillBoundedReadStatus.Complete, global.Status);
        Assert.AreEqual(SkillBoundedReadStatus.Complete, project.Status);
        CollectionAssert.AreEquivalent(store.LoadGlobalDisabledSkillNames().ToArray(), global.Names.ToArray());
        CollectionAssert.AreEquivalent(store.LoadProjectDisabledSkillNames(projectRoot).ToArray(), project.Names.ToArray());
    }

    [TestMethod]
    public async Task BoundedDisabledNames_OversizeAndInvalidCannotBeMistakenForEnabled()
    {
        using var temp = new TestRoot();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var path = Path.Combine(temp.Path, "config.toml");
        await File.WriteAllTextAsync(path, "[skills]\ndisabled = [\"hidden\"]\n" + new string(' ', 100));
        var tooLarge = await store.LoadGlobalDisabledSkillNamesBoundedAsync(32);
        Assert.AreEqual(SkillBoundedReadStatus.TooLarge, tooLarge.Status);
        Assert.AreEqual(0, tooLarge.Names.Count);

        await File.WriteAllTextAsync(path, "[skills]\ndisabled = [oops");
        var invalid = await store.LoadGlobalDisabledSkillNamesBoundedAsync(256);
        Assert.AreEqual(SkillBoundedReadStatus.Invalid, invalid.Status);
        Assert.AreEqual(0, invalid.Names.Count);

        File.Delete(path);
        var missing = await store.LoadGlobalDisabledSkillNamesBoundedAsync(256);
        Assert.AreEqual(SkillBoundedReadStatus.Missing, missing.Status);
        Assert.AreEqual(0, missing.Names.Count);

        Directory.CreateDirectory(path);
        var error = await store.LoadGlobalDisabledSkillNamesBoundedAsync(256);
        Assert.AreEqual(SkillBoundedReadStatus.ReadError, error.Status);
        Assert.AreEqual(0, error.Names.Count);
        Assert.AreEqual(SkillBoundedReadStatus.Missing,
            (await store.LoadProjectDisabledSkillNamesBoundedAsync(null, 256)).Status);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.LoadProjectDisabledSkillNamesBoundedAsync(" ", 256));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await store.LoadGlobalDisabledSkillNamesBoundedAsync(256, canceled.Token));
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.SkillBoundedTextTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
