using System.Text.Json;
using CodeAlta.DotNet;
using CodeAlta.Mcp;
using CodeAlta.Persistence;
using CodeAlta.Search;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bootstrap;
using CodeAlta.Catalog.Roles;
using CodeAlta.Catalog.Skills;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace CodeAlta.Mcp.Tests;

[TestClass]
public sealed class McpInfrastructureTests
{
    [TestMethod]
    public async Task Mcp_InProcess_CanListTools()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);
        var tools = await context.Connection.Client.ListToolsAsync().ConfigureAwait(false);

        var names = tools.Select(static x => x.Name).ToArray();
        CollectionAssert.Contains(names, "codealta.tasks.create");
        CollectionAssert.Contains(names, "codealta.tasks.get");
        CollectionAssert.Contains(names, "codealta.artifacts.write_markdown");
        CollectionAssert.Contains(names, "codealta.search.query");
        CollectionAssert.Contains(names, "codealta.projects.resolve_scope");
        CollectionAssert.Contains(names, "codealta.agents.register");
        CollectionAssert.Contains(names, "codealta.roles.list");
        CollectionAssert.Contains(names, "codealta.skills.list");
        CollectionAssert.Contains(names, "codealta.skills.get");
        CollectionAssert.Contains(names, "codealta.skills.get_resource");
        CollectionAssert.Contains(names, "codealta.skills.validate");
        CollectionAssert.Contains(names, "codealta.skills.activate");
        CollectionAssert.Contains(names, "codealta.bootstrap.ensure_global_repo");
        CollectionAssert.Contains(names, "codealta.dotnet.list_projects");
    }

    [TestMethod]
    public async Task Mcp_Tasks_CreateThenGet_RoundTrips()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var createResult = await context.Connection.Client.CallToolAsync(
            "codealta.tasks.create",
            new Dictionary<string, object?>
            {
                ["title"] = "Implement MCP tools",
                ["projectId"] = "project-1",
            }).ConfigureAwait(false);

        var createPayload = ParseJson(ReadTextContent(createResult));
        var taskId = createPayload.RootElement.GetProperty("taskId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(taskId));

        var getResult = await context.Connection.Client.CallToolAsync(
            "codealta.tasks.get",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId!,
            }).ConfigureAwait(false);

        var getPayload = ParseJson(ReadTextContent(getResult));
        var task = getPayload.RootElement.GetProperty("task");
        Assert.AreEqual(taskId, task.GetProperty("taskId").GetString());
        Assert.AreEqual("Implement MCP tools", task.GetProperty("title").GetString());
        Assert.AreEqual("pending", task.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Mcp_Tasks_List_UsesCursorPagination()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await context.Connection.Client.CallToolAsync(
                "codealta.tasks.create",
                new Dictionary<string, object?>
                {
                    ["title"] = $"task-{i}",
                    ["projectId"] = "project-1",
                }).ConfigureAwait(false);
        }

        var firstResult = await context.Connection.Client.CallToolAsync(
            "codealta.tasks.list",
            new Dictionary<string, object?>
            {
                ["projectId"] = "project-1",
                ["limit"] = 2,
            }).ConfigureAwait(false);

        var firstPayload = ParseJson(ReadTextContent(firstResult));
        var firstItems = firstPayload.RootElement.GetProperty("items");
        Assert.AreEqual(2, firstItems.GetArrayLength());
        var cursor = firstPayload.RootElement.GetProperty("nextCursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        var secondResult = await context.Connection.Client.CallToolAsync(
            "codealta.tasks.list",
            new Dictionary<string, object?>
            {
                ["projectId"] = "project-1",
                ["limit"] = 2,
                ["cursor"] = cursor,
            }).ConfigureAwait(false);

        var secondPayload = ParseJson(ReadTextContent(secondResult));
        var secondItems = secondPayload.RootElement.GetProperty("items");
        Assert.AreEqual(1, secondItems.GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, secondPayload.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [TestMethod]
    public async Task Mcp_Search_Query_ReturnsLinkedArtifacts()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var artifactUri = "artifact://wk-core/knowledge/perf";
        await context.Connection.Client.CallToolAsync(
            "codealta.search.index",
            new Dictionary<string, object?>
            {
                ["sourceKind"] = "artifact",
                ["sourceId"] = artifactUri,
                ["title"] = "Performance Notes",
                ["text"] = "Use Span<T> and ArrayPool<T> to reduce allocations.",
                ["projectId"] = "project-1",
                ["processNow"] = true,
            }).ConfigureAwait(false);

        var queryResult = await context.Connection.Client.CallToolAsync(
            "codealta.search.query",
            new Dictionary<string, object?>
            {
                ["text"] = "allocations span",
                ["projectId"] = "project-1",
                ["limit"] = 5,
            }).ConfigureAwait(false);

        var queryPayload = ParseJson(ReadTextContent(queryResult));
        var results = queryPayload.RootElement;
        Assert.AreEqual(JsonValueKind.Array, results.ValueKind);
        Assert.IsTrue(results.GetArrayLength() > 0);
        Assert.AreEqual(artifactUri, results[0].GetProperty("sourceId").GetString());
    }

    [TestMethod]
    public async Task Mcp_Roles_List_ReturnsBuiltIns()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var listResult = await context.Connection.Client.CallToolAsync(
            "codealta.roles.list",
            new Dictionary<string, object?>
            {
                ["kind"] = "global",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(listResult));
        if (payload.RootElement.ValueKind == JsonValueKind.Object &&
            payload.RootElement.TryGetProperty("error", out var error))
        {
            Assert.Fail($"roles.list returned error: {error.GetString()}");
        }

        Assert.AreEqual(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.IsTrue(payload.RootElement.EnumerateArray().Any(x =>
            string.Equals(x.GetProperty("roleId").GetString(), "coordinator", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Mcp_Skills_List_FindsRepoSkills()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var listResult = await context.Connection.Client.CallToolAsync(
            "codealta.skills.list",
            new Dictionary<string, object?>
            {
                ["kind"] = "project",
                ["projectSlug"] = "repo-main",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(listResult));
        Assert.AreEqual(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.IsTrue(payload.RootElement.EnumerateArray().Any(x =>
            string.Equals(x.GetProperty("name").GetString(), "sample-skill", StringComparison.OrdinalIgnoreCase)));
        var sample = payload.RootElement.EnumerateArray().Single(x => string.Equals(x.GetProperty("name").GetString(), "sample-skill", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(sample.GetProperty("isModelVisible").GetBoolean());
        Assert.AreEqual("ProjectAlta", sample.GetProperty("sourceKind").GetString());
    }

    [TestMethod]
    public async Task Mcp_Skills_Get_ReturnsParsedMetadataAndBody()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.skills.get",
            new Dictionary<string, object?>
            {
                ["kind"] = "project",
                ["projectSlug"] = "repo-main",
                ["skillName"] = "sample-skill",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        Assert.AreEqual("sample-skill", payload.RootElement.GetProperty("name").GetString());
        Assert.AreEqual("Apache-2.0", payload.RootElement.GetProperty("frontmatter").GetProperty("license").GetString());
        StringAssert.Contains(payload.RootElement.GetProperty("body").GetString()!, "Demo skill used for MCP tests.");
        StringAssert.Contains(payload.RootElement.GetProperty("rawContent").GetString()!, "allowed-tools: Read");
    }

    [TestMethod]
    public async Task Mcp_Skills_GetResource_ReturnsResourceTextAndBase64()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.skills.get_resource",
            new Dictionary<string, object?>
            {
                ["kind"] = "project",
                ["projectSlug"] = "repo-main",
                ["skillName"] = "sample-skill",
                ["relativePath"] = "references/guide.md",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        Assert.AreEqual("guide text", payload.RootElement.GetProperty("text").GetString());
        Assert.AreEqual("references/guide.md", payload.RootElement.GetProperty("relativePath").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("contentBase64").GetString()));
    }

    [TestMethod]
    public async Task Mcp_Skills_Validate_ReturnsStructuredDiagnostics()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.skills.validate",
            new Dictionary<string, object?>
            {
                ["kind"] = "project",
                ["projectSlug"] = "repo-main",
                ["skillName"] = "broken-skill",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        Assert.AreEqual(1, payload.RootElement.GetArrayLength());
        var diagnostics = payload.RootElement[0].GetProperty("diagnostics");
        Assert.IsTrue(diagnostics.EnumerateArray().Any(x => string.Equals(x.GetProperty("code").GetString(), "unknown-frontmatter-field", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Mcp_Skills_Activate_ReturnsCanonicalPayload()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.skills.activate",
            new Dictionary<string, object?>
            {
                ["kind"] = "project",
                ["projectSlug"] = "repo-main",
                ["skillName"] = "sample-skill",
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        StringAssert.Contains(payload.RootElement.GetProperty("payload").GetString()!, "<skill_content name=\"sample-skill\"");
        Assert.IsTrue(payload.RootElement.GetProperty("files").EnumerateArray().Any(x => string.Equals(x.GetString(), "references/guide.md", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Mcp_Bootstrap_EnsureGlobalRepo_CreatesRepo()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);
        var repoRoot = Path.Combine(context.Temp.Path, "global-repo");

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.bootstrap.ensure_global_repo",
            new Dictionary<string, object?>
            {
                ["globalRepoRoot"] = repoRoot,
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        Assert.AreEqual(Path.GetFullPath(repoRoot), payload.RootElement.GetProperty("globalRepoRoot").GetString());
        Assert.IsTrue(Directory.Exists(Path.Combine(repoRoot, "projects")));
        Assert.IsTrue(Directory.Exists(Path.Combine(repoRoot, "checkouts")));
    }

    [TestMethod]
    public async Task Mcp_DotNet_ListProjects_DiscoversCsproj()
    {
        await using var context = await TestContext.CreateAsync().ConfigureAwait(false);

        var repoRoot = Path.Combine(context.Temp.Path, "dotnet-repo");
        Directory.CreateDirectory(repoRoot);
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """).ConfigureAwait(false);

        var result = await context.Connection.Client.CallToolAsync(
            "codealta.dotnet.list_projects",
            new Dictionary<string, object?>
            {
                ["repoRoot"] = repoRoot,
            }).ConfigureAwait(false);

        var payload = ParseJson(ReadTextContent(result));
        Assert.AreEqual(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.IsTrue(payload.RootElement.EnumerateArray().Any(x =>
            string.Equals(x.GetProperty("name").GetString(), "App", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ReadTextContent(CallToolResult result)
    {
        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(static x => x.Text)
            .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(text))
        {
            Assert.Fail("Expected a text content block in tool result.");
        }

        return text!;
    }

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"Expected JSON payload but received: {json}\n{ex.Message}");
            throw;
        }
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly IServiceProvider _services;

        private TestContext(
            TempDirectory temp,
            IServiceProvider services,
            InProcessMcpConnection connection)
        {
            Temp = temp;
            _services = services;
            Connection = connection;
        }

        public TempDirectory Temp { get; }

        public InProcessMcpConnection Connection { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var temp = TempDirectory.Create();
            var stateRoot = Path.Combine(temp.Path, "state", "db");
            var dbPath = Path.Combine(stateRoot, "codealta.db");

            var db = new CodeAltaDb(
                new CodeAltaDbOptions
                {
                    DatabasePath = dbPath,
                });
            await db.InitializeAsync().ConfigureAwait(false);

            var taskRepository = new TaskRepository(db);
            var artifactStore = new ArtifactStore();
            var artifactRepository = new ArtifactRepository(db);
            var agentRepository = new AgentRepository(db);
            var indexingQueue = new IndexingQueue();
            var documentIndexStore = new DocumentIndexStore(db);
            var embeddingManager = new EmbeddingModelManager(new HashEmbedder());
            var indexer = new Indexer(indexingQueue, documentIndexStore, embeddingManager);
            var searchService = new SearchService(documentIndexStore, embeddingManager);
            var projectCatalog = new ProjectCatalog(
                new CatalogOptions
                {
                    GlobalRoot = temp.Path,
                });
            var projectResolver = new ProjectResolver(projectCatalog);

            await SeedProjectFixtureAsync(temp.Path).ConfigureAwait(false);

            var roles = new RoleProfileStore();
            var skills = new SkillCatalog();
            var git = new GitService();
            var globalRepoBootstrapper = new GlobalRepoBootstrapper(git);
            var globalRepoSync = new GlobalRepoSyncService(git);
            var projectBootstrapPlanner = new ProjectBootstrapPlanner();
            var projectBootstrapper = new ProjectBootstrapper(projectBootstrapPlanner, git);

            var dotNetWorkspace = new DotNetWorkspaceService();
            var symbolIndex = new SymbolIndexService();
            var dotNetContext = new DotNetContextProvider(dotNetWorkspace, symbolIndex);
            var dotNetOptions = new DotNetOptions
            {
                ArtifactRoot = Path.Combine(temp.Path, "knowledge", "dotnet"),
            };
            var dotNetIndex = new DotNetIndexService(
                dotNetWorkspace,
                symbolIndex,
                artifactStore,
                artifactRepository,
                indexer,
                dotNetOptions);
            var dotNetDiagnostics = new DotNetDiagnosticsService(
                artifactStore,
                artifactRepository,
                indexer,
                dotNetOptions);
            var options = new CodeAltaMcpOptions
            {
                ServerName = "CodeAlta.Tests",
                ServerVersion = "1.0.0-test",
                ArtifactRoot = Path.Combine(temp.Path, "artifacts"),
            };

            var services = new ServiceCollection();
            services.AddSingleton(taskRepository);
            services.AddSingleton(artifactStore);
            services.AddSingleton(artifactRepository);
            services.AddSingleton(agentRepository);
            services.AddSingleton(indexer);
            services.AddSingleton(searchService);
            services.AddSingleton(projectCatalog);
            services.AddSingleton(projectResolver);
            services.AddSingleton(roles);
            services.AddSingleton(skills);
            services.AddSingleton(git);
            services.AddSingleton(globalRepoBootstrapper);
            services.AddSingleton(globalRepoSync);
            services.AddSingleton(projectBootstrapPlanner);
            services.AddSingleton(projectBootstrapper);
            services.AddSingleton(dotNetWorkspace);
            services.AddSingleton(symbolIndex);
            services.AddSingleton(dotNetContext);
            services.AddSingleton(dotNetIndex);
            services.AddSingleton(dotNetDiagnostics);
            services.AddSingleton(options);
            services.AddSingleton(new McpSessionRegistry());

            var provider = services.BuildServiceProvider();
            var factory = new CodeAltaMcpServerFactory(
                provider,
                provider.GetRequiredService<McpSessionRegistry>(),
                provider.GetRequiredService<CodeAltaMcpOptions>());
            var connection = await InProcessMcpConnection.CreateAsync(factory).ConfigureAwait(false);

            return new TestContext(temp, provider, connection);
        }

        private static async Task SeedProjectFixtureAsync(string globalRepoRoot)
        {
            var projectsRoot = Path.Combine(globalRepoRoot, "projects");
            Directory.CreateDirectory(projectsRoot);

            var projectId = ProjectId.NewVersion7();

            await File.WriteAllTextAsync(
                Path.Combine(projectsRoot, "repo-main.md"),
                $$"""
                ---
                kind: "project"
                id: "{{projectId}}"
                slug: "repo-main"
                name: "Repo.Main"
                display_name: "Main Repo"
                path: "{{Path.Combine(globalRepoRoot, "remote.git").Replace("\\", "\\\\")}}"
                default_branch: "main"
                checkout:
                  path_template: "{projectName}"
                ---

                # Main Repo
                """).ConfigureAwait(false);

            var checkoutRoot = Path.Combine(globalRepoRoot, "checkouts", "Repo.Main");
            var skillRoot = Path.Combine(checkoutRoot, ".alta", "skills", "sample-skill");
            var brokenSkillRoot = Path.Combine(checkoutRoot, ".agents", "skills", "broken-skill");
            Directory.CreateDirectory(skillRoot);
            Directory.CreateDirectory(brokenSkillRoot);
            await File.WriteAllTextAsync(
                Path.Combine(skillRoot, "SKILL.md"),
                """
                ---
                name: sample-skill
                description: Demo skill used for MCP tests.
                license: Apache-2.0
                compatibility: Requires test checkout access.
                metadata:
                  owner: tests
                allowed-tools: Read
                ---
                # Sample Skill

                Demo skill used for MCP tests.
                """).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(skillRoot, "references"));
            await File.WriteAllTextAsync(Path.Combine(skillRoot, "references", "guide.md"), "guide text").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(brokenSkillRoot, "SKILL.md"),
                """
                ---
                name: broken-skill
                description: Broken skill used for diagnostics.
                unsupported: true
                ---
                # Broken Skill
                """).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);

            switch (_services)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;

                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            Temp.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Mcp.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}


