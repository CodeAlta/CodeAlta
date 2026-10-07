using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.PullRequests;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PullRequestPromptsRpcTests
{
    private const string Epoch = "5f1c2d3e-8a7b-4c6d-9e0f-1a2b3c4d5e6f";

    [TestMethod]
    public async Task TheKindsOfAProject_AreListedForItsSessions_AndWrittenFromSettings()
    {
        var root = Directory.CreateTempSubdirectory("codealta-pr-rpc-").FullName;
        try
        {
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var service = new PullRequestPromptsService(new PullRequestPromptCatalog(options), projects, Epoch);

            var listed = await service.ListAsync(new(Epoch, project.Id), default);
            Assert.AreEqual("ok", listed.Status);
            var shipped = listed.Items.Single();
            Assert.AreEqual(("default", "Default", "builtin", false, (string?)null), (shipped.Id, shipped.Name, shipped.Source, shipped.Overridden, shipped.File));
            StringAssert.StartsWith(shipped.Content, "Create a pull request for the work of this session.");
            StringAssert.Contains(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.PullRequestPromptsResponse), "\"source\":\"builtin\"");

            // A kind of the project replaces the one that ships for the sessions of that project only.
            var saved = await service.SaveAsync(new(Epoch, project.Id, "Default", "Team", "As the team does it.", "The way of the team."), default);
            Assert.AreEqual(("ok", "project", "Team"), (saved.Status, saved.Item!.Source, saved.Item.Name));
            Assert.AreEqual("ok", (await service.SaveAsync(new(Epoch, null, "release", "Release", null, "For a release."), default)).Status);
            CollectionAssert.AreEqual(new[] { "The way of the team.", "For a release." }, (await service.ListAsync(new(Epoch, project.Id), default)).Items.Select(static item => item.Content).ToArray());
            CollectionAssert.AreEqual(new[] { ("default", "builtin", true), ("release", "global", false), ("default", "project", false) },
                (await service.ListAsync(new(Epoch, project.Id, All: true), default)).Items.Select(static item => (item.Id, item.Source, item.Overridden)).ToArray(), "Settings shows what is replaced too.");
            Assert.AreEqual(2, (await service.ListAsync(new(Epoch, null), default)).Items.Count);

            Assert.AreEqual("invalid", (await service.SaveAsync(new(Epoch, null, "bad name", null, null, "Text."), default)).Status);
            var empty = await service.SaveAsync(new(Epoch, null, "empty", null, null, " "), default);
            Assert.AreEqual("invalid", empty.Status);
            Assert.IsFalse(empty.Message!.Contains("Parameter", StringComparison.Ordinal));
            Assert.AreEqual("not_found", (await service.SaveAsync(new(Epoch, "unknown", "x", null, null, "Text."), default)).Status);
            Assert.AreEqual("stale_epoch", (await service.SaveAsync(new("another", null, "x", null, null, "Text."), default)).Status);
            Assert.AreEqual("not_found", (await service.ListAsync(new(Epoch, "unknown"), default)).Status);

            Assert.AreEqual("ok", (await service.DeleteAsync(new(Epoch, project.Id, "default"), default)).Status);
            Assert.AreEqual("not_found", (await service.DeleteAsync(new(Epoch, project.Id, "default"), default)).Status);
            Assert.AreEqual("builtin", (await service.ListAsync(new(Epoch, project.Id), default)).Items[0].Source);
            Assert.AreEqual("unavailable", (await new PullRequestPromptsService().ListAsync(new(Epoch, null), default)).Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
