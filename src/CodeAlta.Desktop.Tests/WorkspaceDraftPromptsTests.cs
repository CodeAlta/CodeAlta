using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceDraftPromptsTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task DraftChoicesUseExactScopeWithoutCreatingSessionOrProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-draft-prompts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var promptFolder = Path.Combine(projectPath, ".alta", "prompts", "agents");
            Directory.CreateDirectory(promptFolder);
            await File.WriteAllTextAsync(Path.Combine(promptFolder, "draft-fixture.prompt.md"), "---\nname: Draft fixture\n---\nprivate prompt body");
            var factories = 0;
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(new ModelProviderDescriptor(new("draft-fixture"), "Fixture"), () =>
                {
                    factories++;
                    throw new AssertFailedException("Prompt choices must not initialize a provider.");
                }),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var service = new WorkspaceService(host, Epoch);
            var request = new WorkspaceDraftPromptsRequest(Epoch, project.Id, projectPath);
            var result = await service.DraftPromptsAsync(request, CancellationToken.None);
            Assert.AreEqual("ok", result.Status);
            Assert.AreEqual(request.ProjectId, result.ProjectId);
            Assert.AreEqual(request.ProjectPath, result.ProjectPath);
            Assert.IsTrue(result.Prompts.Any(prompt => prompt.Id == "draft-fixture" && prompt.Name == "Draft fixture"));
            Assert.IsTrue(result.Prompts.Count <= 64);
            var json = JsonSerializer.Serialize(result, DesktopJsonContext.Default.WorkspaceDraftPromptsResponse);
            Assert.IsFalse(json.Contains("private prompt body", StringComparison.Ordinal));
            Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceDraftPromptsRequest),
                DesktopJsonContext.Default.WorkspaceDraftPromptsRequest));
            var globalResult = await service.DraftPromptsAsync(new(Epoch, null, null), CancellationToken.None);
            Assert.AreEqual("ok", globalResult.Status);
            Assert.IsFalse(globalResult.Prompts.Any(prompt => prompt.Id == "draft-fixture"));
            // A well-formed epoch from another host is stale; a malformed one is refused before the epoch comparison.
            Assert.AreEqual("stale_epoch", (await service.DraftPromptsAsync(request with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.DraftPromptsAsync(request with { ExpectedHostEpoch = "other" }, CancellationToken.None)).Status);
            Assert.AreEqual("invalid_scope", (await service.DraftPromptsAsync(request with { ProjectPath = null }, CancellationToken.None)).Status);
            Assert.AreEqual("unavailable", (await service.DraftPromptsAsync(request with { ProjectPath = global }, CancellationToken.None)).Status);
            Assert.AreEqual("unavailable", (await service.DraftPromptsAsync(request with { ProjectId = "missing" }, CancellationToken.None)).Status);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.DraftPromptsAsync(new(Epoch, null, null), canceled.Token));
            Assert.AreEqual(0, factories);
            Assert.IsEmpty((await service.SnapshotAsync(new(), CancellationToken.None)).Sessions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CatalogOnlyDraftDoesNotInventOwnedChoices()
    {
        var result = await new WorkspaceService((string?)null).DraftPromptsAsync(new(Epoch, null, null), CancellationToken.None);
        Assert.AreEqual("unconfigured", result.Status);
        Assert.IsEmpty(result.Prompts);
    }
}
