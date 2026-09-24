using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PromptCatalogRpcTests
{
    [TestMethod]
    public async Task ScopedEffectiveCatalogProjectsContentWithoutPathsOrSystemPrompts()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-prompt-rpc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = Path.Combine(root, "app");
            var global = Path.Combine(root, "global");
            var project = Path.Combine(root, "project");
            Write(app, "content/prompts/agents/default.prompt.md", "---\nname: Default\ndescription: Built in\n---\nbase body");
            Write(global, "prompts/agents/default.prompt.md", "---\nmode: append\n---\nprivate global body");
            Write(project, ".alta/prompts/agents/custom.prompt.md", "---\nname: Project name\ndescription: Project description\n---\nproject body");
            Write(project, ".alta/prompts/system/default.system-prompt.md", "system secret should not appear");
            var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(app));
            var calls = 0;
            var service = new PromptCatalogService((id, _) => {
                calls++;
                Assert.AreEqual("owned-session", id);
                return Task.FromResult<IReadOnlyList<AgentPromptDescriptor>?>(catalog.ListEffectivePrompts(new AgentPromptCatalogQuery
                {
                    AppBaseDirectory = app, UserCodeAltaRoot = global, UserProfileRoot = Path.Combine(root, "profile"),
                    ProjectRoot = project, ProjectPromptResourcesTrusted = true,
                }));
            }, "epoch");
            Assert.AreEqual("stale_epoch", (await service.List(new("other", "owned-session"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.List(new("epoch", " ../project"), CancellationToken.None)).Status);
            Assert.AreEqual(0, calls);
            var result = await service.List(new("epoch", "owned-session"), CancellationToken.None);
            Assert.AreEqual("ok", result.Status);
            Assert.HasCount(2, result.Prompts);
            var effective = result.Prompts.Single(prompt => prompt.Id == "default");
            Assert.AreEqual("Default", effective.Name);
            Assert.AreEqual("Built in", effective.Description);
            Assert.AreEqual("UserGlobal", effective.Scope);
            Assert.IsTrue(effective.Appended);
            StringAssert.Contains(effective.Body, "base body");
            StringAssert.Contains(effective.Body, "private global body");
            Assert.AreEqual("Project", result.Prompts.Single(prompt => prompt.Id == "custom").Scope);
            var json = JsonSerializer.Serialize(result, DesktopJsonContext.Default.PromptCatalogResponse);
            Assert.IsFalse(json.Contains(root, StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("system secret", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidScopeFailuresAndWorstCaseEscapingRemainBounded()
    {
        var calls = 0;
        var service = new PromptCatalogService((_, _) => {
            calls++;
            return Task.FromResult<IReadOnlyList<AgentPromptDescriptor>?>(Enumerable.Range(0, 75)
                .Select(index => new AgentPromptDescriptor($"prompt-{index:D2}", new string('\u0001', 256), new string('\u0001', 1024),
                    "default", new string('\u0001', 3000), AgentPromptSourceKind.BuiltIn, 0, "private path", "hash",
                    PromptCompositionMode.Replace, false, null)).ToArray());
        }, "epoch");
        var result = await service.List(new("epoch", "exact"), CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.Prompts.Count is > 0 and < 64);
        Assert.IsTrue(result.Prompts[0].BodyTruncated);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.PromptCatalogResponse);
        Assert.IsTrue(bytes.Length <= PromptCatalogService.MaximumResponseBytes);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("private path", StringComparison.Ordinal));
        Assert.AreEqual("unconfigured", (await new PromptCatalogService().List(new("epoch", "exact"), CancellationToken.None)).Status);
        var failed = new PromptCatalogService((_, _) => throw new InvalidOperationException("private failure"), "epoch");
        Assert.AreEqual("read_failed", (await failed.List(new("epoch", "exact"), CancellationToken.None)).Status);
        var missing = new PromptCatalogService((_, _) => Task.FromResult<IReadOnlyList<AgentPromptDescriptor>?>(null), "epoch");
        Assert.AreEqual("unavailable", (await missing.List(new("epoch", "exact"), CancellationToken.None)).Status);
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
