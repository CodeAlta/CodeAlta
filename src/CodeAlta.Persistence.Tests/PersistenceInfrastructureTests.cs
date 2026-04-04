using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Persistence.Tests;

[TestClass]
public sealed class PersistenceInfrastructureTests
{
    [TestMethod]
    public async Task CodeAltaDb_InitializeAsync_CreatesCoreSchema()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);

        await using var connection = await db.CreateOpenConnectionAsync().ConfigureAwait(false);
        var tableNames = await ReadTableNamesAsync(connection).ConfigureAwait(false);

        CollectionAssert.Contains(tableNames, "schema_version");
        CollectionAssert.Contains(tableNames, "tasks");
        CollectionAssert.Contains(tableNames, "artifacts");
        CollectionAssert.Contains(tableNames, "documents");
        CollectionAssert.Contains(tableNames, "documents_fts");
        CollectionAssert.Contains(tableNames, "project_file_usage");
    }

    [TestMethod]
    public async Task TaskRepository_CreateUpdateAndEvents_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);
        var repository = new TaskRepository(db);

        var created = await repository.CreateAsync(
            new CreateTaskRequest
            {
                ProjectId = "project-1",
                Title = "Implement tests",
                AssignedAgentId = AgentId.NewVersion7().ToString(),
            }).ConfigureAwait(false);

        Assert.AreEqual(TaskStatus.Pending, created.Status);

        var updated = await repository.UpdateAsync(
            new UpdateTaskRequest
            {
                TaskId = created.TaskId,
                Status = TaskStatus.InProgress,
                Title = "Implement persistence tests",
            }).ConfigureAwait(false);

        Assert.IsNotNull(updated);
        Assert.AreEqual(TaskStatus.InProgress, updated.Status);
        Assert.AreEqual("Implement persistence tests", updated.Title);

        await repository.AddNoteAsync(created.TaskId, "work started").ConfigureAwait(false);

        var events = await repository.ListEventsAsync(created.TaskId).ConfigureAwait(false);
        Assert.AreEqual(3, events.Count);
        Assert.AreEqual("created", events[0].Kind);
        Assert.AreEqual("status_changed", events[1].Kind);
        Assert.AreEqual("note_added", events[2].Kind);
    }

    [TestMethod]
    public async Task TaskRepository_ListPageAsync_PaginatesWithCursor()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);
        var repository = new TaskRepository(db);

        for (var i = 0; i < 3; i++)
        {
            await repository.CreateAsync(
                new CreateTaskRequest
                {
                    ProjectId = "project-1",
                    Title = $"task-{i}",
                }).ConfigureAwait(false);
        }

        var first = await repository.ListPageAsync(
            projectId: "project-1",
            limit: 2).ConfigureAwait(false);

        Assert.AreEqual(2, first.Tasks.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.NextCursor));

        var second = await repository.ListPageAsync(
            projectId: "project-1",
            limit: 2,
            cursor: first.NextCursor).ConfigureAwait(false);

        Assert.AreEqual(1, second.Tasks.Count);
        Assert.IsNull(second.NextCursor);

        var ids = first.Tasks.Select(x => x.TaskId.ToString()).Concat(second.Tasks.Select(x => x.TaskId.ToString())).ToArray();
        Assert.AreEqual(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public async Task ArtifactStore_WriteReadAndExtractPlainText_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var store = new ArtifactStore();
        var path = Path.Combine(temp.Path, "repo", ".codealta", "knowledge", "record.md");

        var frontmatter = new ArtifactFrontmatter
        {
            Id = ArtifactId.NewVersion7().ToString(),
            Type = "knowledge.record",
            Title = "Project Overview",
            ProjectId = "project-1",
            ProjectKey = "repo-main",
            Source = new ArtifactSourceInfo
            {
                Kind = "generated",
                AgentId = AgentId.NewVersion7().ToString(),
            },
            Tags = ["architecture", "indexable"],
            Links = new ArtifactLinks
            {
                Tasks = ["task-1"],
                Files =
                [
                    new ArtifactFileLink
                    {
                        Path = "src/Foo.cs",
                        Range = new ArtifactLineRange { StartLine = 5, EndLine = 12 },
                    },
                ],
            },
        };

        var body =
            """
            # Title

            This is the first paragraph.

            ```csharp
            Console.WriteLine("Hello");
            ```
            """;

        await store.WriteMarkdownAsync(
            path,
            new ArtifactDocument
            {
                Frontmatter = frontmatter,
                Body = body,
            }).ConfigureAwait(false);

        var reloaded = await store.ReadMarkdownAsync(path).ConfigureAwait(false);
        Assert.AreEqual(frontmatter.Id, reloaded.Frontmatter.Id);
        Assert.AreEqual(frontmatter.Type, reloaded.Frontmatter.Type);
        Assert.AreEqual("Project Overview", reloaded.Frontmatter.Title);
        Assert.AreEqual("repo-main", reloaded.Frontmatter.ProjectKey);
        Assert.AreEqual(body.Trim(), reloaded.Body.Trim());

        var plainText = store.ExtractPlainText(body);
        StringAssert.Contains(plainText, "This is the first paragraph.");
        StringAssert.Contains(plainText, "Console.WriteLine");
    }

    [TestMethod]
    public async Task ArtifactRepository_UpsertListAndLinks_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);
        var repository = new ArtifactRepository(db);

        var artifact = new ArtifactRecord
        {
            ArtifactId = ArtifactId.NewVersion7(),
            Uri = "artifact://project-1/knowledge/overview",
            ProjectId = "project-1",
            Type = "knowledge.record",
            Path = Path.Combine(temp.Path, "repo", ".codealta", "knowledge", "overview.md"),
            FrontmatterJson = JsonSerializer.Serialize(new { tags = new[] { "architecture" } }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await repository.UpsertAsync(artifact).ConfigureAwait(false);
        var loaded = await repository.GetByIdAsync(artifact.ArtifactId).ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(artifact.Uri, loaded.Uri);

        var listed = await repository.ListAsync(
            new ArtifactQuery
            {
                ProjectId = "project-1",
                Type = "knowledge.record",
                Limit = 10,
            }).ConfigureAwait(false);

        Assert.AreEqual(1, listed.Count);

        await repository.AddLinkAsync(
            new ArtifactLinkRecord
            {
                FromArtifactId = artifact.ArtifactId,
                ToKind = "task",
                ToId = "task-1",
            }).ConfigureAwait(false);

        var links = await repository.ListLinksAsync(artifact.ArtifactId).ConfigureAwait(false);
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual("task", links[0].ToKind);
        Assert.AreEqual("task-1", links[0].ToId);
    }

    [TestMethod]
    public async Task AgentRepository_UpsertAgentAndSession_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);
        var repository = new AgentRepository(db);

        var agent = new AgentRecord
        {
            AgentId = AgentId.NewVersion7(),
            Role = "planner.project",
            ScopeKind = "project",
            ScopeId = "project-1",
            BackendId = "codex",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await repository.UpsertAgentAsync(agent).ConfigureAwait(false);
        var loaded = await repository.GetAgentAsync(agent.AgentId).ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("planner.project", loaded.Role);

        await repository.UpsertSessionAsync(
            new AgentSessionRecord
            {
                SessionId = "session-1",
                AgentId = agent.AgentId,
                BackendSessionId = "backend-thread-1",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);

        var sessions = await repository.ListSessionsAsync(agent.AgentId).ConfigureAwait(false);
        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual("session-1", sessions[0].SessionId);
    }

    [TestMethod]
    public async Task ProjectFileUsageRepository_RecordAndListRecent_RoundTrips()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = await CreateInitializedDbAsync(dbPath).ConfigureAwait(false);
        var repository = new ProjectFileUsageRepository(db);

        var firstAccess = DateTimeOffset.UtcNow.AddMinutes(-5);
        var secondAccess = DateTimeOffset.UtcNow;

        await repository.RecordAccessAsync(
            projectRoot: temp.Path,
            relativePath: "src/CodeAlta/App",
            kind: "directory",
            accessedAt: firstAccess,
            accessKind: "popup_accepted").ConfigureAwait(false);
        var updated = await repository.RecordAccessAsync(
            projectRoot: temp.Path,
            relativePath: "src/CodeAlta/App",
            kind: "directory",
            accessedAt: secondAccess,
            accessKind: "prompt_inserted").ConfigureAwait(false);
        await repository.RecordAccessAsync(
            projectRoot: temp.Path,
            relativePath: "readme.md",
            kind: "file",
            accessedAt: firstAccess,
            accessKind: "editor_opened").ConfigureAwait(false);

        Assert.AreEqual(2L, updated.AccessCount);
        Assert.AreEqual("prompt_inserted", updated.LastAccessKind);

        var recent = await repository.ListRecentAsync(temp.Path, limit: 5).ConfigureAwait(false);
        Assert.AreEqual(2, recent.Count);
        Assert.AreEqual("src/CodeAlta/App", recent[0].RelativePath);
        Assert.AreEqual(2L, recent[0].AccessCount);

        var all = await repository.ListByProjectAsync(temp.Path).ConfigureAwait(false);
        Assert.AreEqual(2, all.Count);
    }

    private static async Task<CodeAltaDb> CreateInitializedDbAsync(string path)
    {
        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = path,
            });

        await db.InitializeAsync().ConfigureAwait(false);
        return db;
    }

    private static async Task<List<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type IN ('table', 'view')
            ORDER BY name;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
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
                $"CodeAlta.Persistence.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
