using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.StartPrompts;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class StartPromptsRpcTests
{
    private const string Epoch = "0c7e4b1a-2d3f-4e5a-9b6c-7d8e9f0a1b2c";
    private const string Review = StartPromptCatalog.PullRequestReviewId;

    [TestMethod]
    public async Task TheTextsOfAStartPrompt_AreListedAndWrittenFromSettings()
    {
        var root = Directory.CreateTempSubdirectory("codealta-start-rpc-").FullName;
        try
        {
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var service = new StartPromptsService(new StartPromptCatalog(options), projects, Epoch);

            var listed = await service.ListAsync(new(Epoch, project.Id), default);
            Assert.AreEqual("ok", listed.Status);
            var shipped = listed.Items.Single();
            Assert.AreEqual((Review, "builtin", false, (string?)null), (shipped.Id, shipped.Source, shipped.Overridden, shipped.File));
            StringAssert.StartsWith(shipped.Content, "Review the pull request {reference}");
            StringAssert.Contains(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.StartPromptsResponse), "\"source\":\"builtin\"");

            var mine = await service.SaveAsync(new(Epoch, null, Review, "Mine."), default);
            Assert.AreEqual(("ok", "global", "Mine."), (mine.Status, mine.Item!.Source, mine.Item.Content));
            Assert.AreEqual("ok", (await service.SaveAsync(new(Epoch, project.Id, "Pull-Request-Review", "The team's."), default)).Status);
            CollectionAssert.AreEqual(new[] { ("builtin", true), ("global", true), ("project", false) },
                (await service.ListAsync(new(Epoch, project.Id), default)).Items.Select(static item => (item.Source, item.Overridden)).ToArray());
            CollectionAssert.AreEqual(new[] { ("builtin", true), ("global", false) },
                (await service.ListAsync(new(Epoch, null), default)).Items.Select(static item => (item.Source, item.Overridden)).ToArray(), "A project's text is listed with that project only.");

            Assert.AreEqual("invalid", (await service.SaveAsync(new(Epoch, null, "issue", "Text."), default)).Status);
            var empty = await service.SaveAsync(new(Epoch, null, Review, " "), default);
            Assert.AreEqual("invalid", empty.Status);
            Assert.IsFalse(empty.Message!.Contains("Parameter", StringComparison.Ordinal));
            Assert.AreEqual("not_found", (await service.SaveAsync(new(Epoch, "unknown", Review, "Text."), default)).Status);
            Assert.AreEqual("stale_epoch", (await service.SaveAsync(new("another", null, Review, "Text."), default)).Status);
            Assert.AreEqual("not_found", (await service.ListAsync(new(Epoch, "unknown"), default)).Status);

            Assert.AreEqual("ok", (await service.DeleteAsync(new(Epoch, project.Id, Review), default)).Status);
            Assert.AreEqual("not_found", (await service.DeleteAsync(new(Epoch, project.Id, Review), default)).Status);
            Assert.AreEqual("not_found", (await service.DeleteAsync(new(Epoch, null, "../config"), default)).Status);
            Assert.AreEqual(2, (await service.ListAsync(new(Epoch, project.Id), default)).Items.Count);
            Assert.AreEqual("unavailable", (await new StartPromptsService().ListAsync(new(Epoch, null), default)).Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
