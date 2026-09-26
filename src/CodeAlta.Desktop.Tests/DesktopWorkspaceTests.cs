using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopWorkspaceTests
{
    [TestMethod]
    public async Task Projection_RecordedCreationUsesOnlyLoadedMetadata()
    {
        var created = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+14:00");
        var projectReads = 0;
        var sessionReads = 0;
        async IAsyncEnumerable<AgentSessionMetadata> Sessions([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            sessionReads++;
            yield return Session("created") with { CreatedAt = created };
            yield return Session("default") with { CreatedAt = default };
            yield return Session("year-one") with { CreatedAt = new DateTimeOffset(1, 2, 3, 0, 0, 0, TimeSpan.Zero) };
            await Task.CompletedTask;
        }
        var result = await WorkspaceService.ReadAsync(_ => { projectReads++; return Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]); }, Sessions, CancellationToken.None);
        Assert.AreEqual(1, projectReads);
        Assert.AreEqual(1, sessionReads);
        Assert.AreNotEqual(created, result.Sessions.Single(row => row.Id == "created").UpdatedAt);
        var mismatched = WorkspaceService.ProjectSnapshot([], [Session("created") with { CreatedAt = created }],
            new Dictionary<string, SessionViewJournalHeader> { ["created"] = new() { SessionId = "different", CreatedAt = created.AddDays(1) } });
        Assert.AreEqual(created, mismatched.Sessions[0].CreatedAt);
        Assert.IsNull(mismatched.Sessions[0].ScopeKind);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.WorkspaceSnapshot));
        foreach (var row in json.RootElement.GetProperty("sessions").EnumerateArray())
        {
            Assert.IsTrue(row.TryGetProperty("createdAt", out var value), "Recorded creation must be present independently of header scope.");
            if (row.GetProperty("id").GetString() == "created") Assert.AreEqual(created, value.GetDateTimeOffset());
            else Assert.AreEqual(System.Text.Json.JsonValueKind.Null, value.ValueKind);
        }
    }

    [TestMethod]
    public void Projection_CreationSerializationFitsExplicitAllowanceAndEnvelope()
    {
        DateTimeOffset[] dates = [default, DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+14:00"),
            DateTimeOffset.Parse("2026-01-02T03:04:05.1234567-14:00")];
        foreach (var date in dates)
        {
            var sessions = Enumerable.Range(0, 500).Select(i => Session($"s{i}") with
            {
                CreatedAt = date, WorkspacePath = new string('\u0001', 4096), ProviderKey = new string('\u0001', 256),
                Details = new RawApiSessionMetadataDetails(Title: new string('\u0001', 4096)),
            }).ToArray();
            var snapshot = WorkspaceService.ProjectSnapshot([], sessions);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot);
            var accounted = 2048 + snapshot.Sessions.Sum(row => 512 + 64 + 6 * (row.Id.Length + row.Title.Length + row.FullTitle.Length
                + (row.ParentSessionId?.Length ?? 0) + (row.WorkspacePath?.Length ?? 0) + (row.ProviderKey?.Length ?? 0) + (row.ProjectId?.Length ?? 0)));
            Assert.IsTrue(bytes.Length <= accounted && accounted <= 700 * 1024);
            Assert.IsTrue(snapshot.SessionsTruncated);
            using var json = System.Text.Json.JsonDocument.Parse(bytes);
            foreach (var row in json.RootElement.GetProperty("sessions").EnumerateArray())
            {
                var value = row.GetProperty("createdAt");
                var fieldBytes = System.Text.Encoding.UTF8.GetByteCount(",\"createdAt\":" + value.GetRawText());
                Assert.IsTrue(fieldBytes <= 64);
                if (date == default) Assert.AreEqual(System.Text.Json.JsonValueKind.Null, value.ValueKind);
                else
                {
                    Assert.AreEqual(date, value.GetDateTimeOffset());
                    Assert.AreEqual(date.Offset, value.GetDateTimeOffset().Offset);
                }
            }
        }
    }

    [TestMethod]
    [DataRow("browser only", true)]
    [DataRow("catalog", true)]
    [DataRow("reordered", true)]
    [DataRow("missing data", false)]
    [DataRow("relative data", false)]
    [DataRow("existing data directory", false)]
    [DataRow("existing data file", false)]
    [DataRow("alta data", false)]
    [DataRow("alta before traversal", false)]
    [DataRow("missing consent", false)]
    [DataRow("consent only", false)]
    [DataRow("relative catalog", false)]
    [DataRow("missing catalog", false)]
    [DataRow("catalog file", false)]
    [DataRow("alta catalog", false)]
    [DataRow("same roots", false)]
    [DataRow("browser beneath catalog", false)]
    [DataRow("catalog beneath browser", false)]
    [DataRow("catalog volume root", false)]
    [DataRow("duplicate", false)]
    [DataRow("mixed early flag", false)]
    public void RootAdmission_UsesExplicitSeparatedRoots(string row, bool expected)
    {
        // Lexical test paths only. Existence is supplied as inert facts, never probed.
        var parent = OperatingSystem.IsWindows() ? @"C:\desktop-fixture" : "/desktop-fixture";
        var data = Path.Combine(parent, "browser");
        var catalog = Path.Combine(parent, "copy");
        string[] args = ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache"];
        switch (row)
        {
            case "browser only": args = ["--data-root", data]; break;
            case "reordered": args = ["--allow-catalog-cache", "--catalog-root", catalog, "--data-root", data]; break;
            case "missing data": args = ["--catalog-root", catalog, "--allow-catalog-cache"]; break;
            case "relative data": args[1] = "relative"; break;
            case "alta data": args[1] = Path.Combine(parent, ".ALTA", "browser"); break;
            case "alta before traversal": args[1] = Path.Combine(parent, ".alta", "..", "browser"); break;
            case "missing consent": args = ["--data-root", data, "--catalog-root", catalog]; break;
            case "consent only": args = ["--data-root", data, "--allow-catalog-cache"]; break;
            case "relative catalog": args[3] = "relative"; break;
            case "alta catalog": args[3] = Path.Combine(parent, ".alta", "copy"); break;
            case "same roots": args[3] = data; break;
            case "browser beneath catalog": args[1] = Path.Combine(catalog, "browser"); break;
            case "catalog beneath browser": args[3] = Path.Combine(data, "copy"); break;
            case "catalog volume root": args[3] = Path.GetPathRoot(parent)!; break;
            case "duplicate": args = [.. args, "--data-root", data]; break;
            case "mixed early flag": args = ["--help", .. args]; break;
        }
        bool DirectoryExists(string path) =>
            (path == catalog && row is not "missing catalog" and not "catalog file") ||
            (path == data && row == "existing data directory") || (path == Path.GetPathRoot(parent) && row == "catalog volume root");
        bool FileExists(string path) =>
            (path == data && row == "existing data file") || (path == catalog && row == "catalog file");

        Assert.AreEqual(expected, DesktopCommandLine.TryParse(args, DirectoryExists, FileExists, out var options, out var error));
        if (expected)
        {
            Assert.IsNotNull(options);
            Assert.AreEqual(data, options.DataRoot);
            Assert.AreEqual(row == "browser only" ? null : catalog, options.CatalogRoot);
            Assert.IsNull(error);
        }
        else
        {
            Assert.IsNull(options);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }

    [TestMethod]
    [DataRow("populated")]
    [DataRow("empty")]
    [DataRow("project failure")]
    [DataRow("session failure")]
    [DataRow("canceled")]
    [DataRow("null project callback")]
    [DataRow("null session callback")]
    public async Task Read_UsesActualCallbacksAndPreservesFailure(string row)
    {
        var failure = new IOException("literal locked-cache failure");
        var token = new CancellationToken(row == "canceled");
        var projectCalls = 0;
        var sessionCalls = 0;
        Task<IReadOnlyList<ProjectDescriptor>> Projects(CancellationToken actual)
        {
            Assert.AreEqual(token, actual);
            projectCalls++;
            return row == "project failure"
                ? Task.FromException<IReadOnlyList<ProjectDescriptor>>(failure)
                : Task.FromResult<IReadOnlyList<ProjectDescriptor>>(row == "empty" ? [] : [Project("p")]);
        }
        IAsyncEnumerable<AgentSessionMetadata> Sessions(CancellationToken actual)
        {
            Assert.AreEqual(token, actual);
            sessionCalls++;
            return new LiteralSessions(row == "empty" ? [] : [Session("s")], token,
                row == "session failure" ? failure : null);
        }

        if (row == "null project callback")
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => WorkspaceService.ReadAsync(null!, Sessions, token));
            Assert.AreEqual(0, sessionCalls);
            return;
        }
        if (row == "null session callback")
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => WorkspaceService.ReadAsync(Projects, null!, token));
            Assert.AreEqual(0, projectCalls);
            return;
        }
        // Every callback returns completed/faulted/canceled work; no gates or concrete catalogs.
        var read = WorkspaceService.ReadAsync(Projects, Sessions, token);
        if (row is "project failure" or "session failure")
        {
            var actual = await Assert.ThrowsExactlyAsync<IOException>(async () => await read);
            Assert.AreSame(failure, actual);
        }
        else if (row == "canceled")
        {
            var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
            Assert.AreEqual(token, actual.CancellationToken);
        }
        else
        {
            var result = await read;
            Assert.IsTrue(result.Configured);
            Assert.AreEqual(row == "empty" ? 0 : 1, result.Projects.Length);
            Assert.AreEqual(row == "empty" ? 0 : 1, result.Sessions.Length);
        }
        Assert.AreEqual(1, projectCalls);
        Assert.AreEqual(row == "project failure" ? 0 : 1, sessionCalls);
    }

    [TestMethod]
    [DataRow("persisted metadata")]
    [DataRow("empty")]
    [DataRow("order")]
    [DataRow("row limits")]
    [DataRow("display scalar boundary")]
    [DataRow("oversized id")]
    [DataRow("oversized path")]
    [DataRow("invalid identity")]
    [DataRow("duplicate identity")]
    [DataRow("wire budget")]
    [DataRow("worst-case escaping")]
    public void Projection_PreservesMetadataMeaningAndBoundsResponse(string row)
    {
        ProjectDescriptor[] projects = [Project("p")];
        AgentSessionMetadata[] sessions = [Session("s")];
        switch (row)
        {
            case "empty": projects = []; sessions = []; break;
            case "order":
                projects = [Project("b"), Project("a")];
                sessions = [Session("a"), Session("b")];
                break;
            case "row limits":
                projects = Enumerable.Range(0, 201).Select(i => Project($"p{i:D3}")).ToArray();
                sessions = Enumerable.Range(0, 501).Select(i => Session($"s{i:D3}")).ToArray();
                break;
            case "display scalar boundary":
                projects[0].DisplayName = new string('x', 255) + "😀tail";
                sessions[0] = sessions[0] with { Details = new RawApiSessionMetadataDetails(Title: new string('x', 255) + "😀tail") };
                break;
            case "oversized id": sessions[0] = sessions[0] with { SessionId = new string('x', 257) }; break;
            case "oversized path": projects[0].ProjectPath = new string('x', 4097); break;
            case "invalid identity": sessions[0] = sessions[0] with { SessionId = "bad\uD800" }; break;
            case "duplicate identity": sessions = [Session("same"), Session("same")]; break;
            case "wire budget":
                projects = Enumerable.Range(0, 200).Select(i => { var p = Project($"p{i}"); p.ProjectPath = new string('界', 4096); return p; }).ToArray();
                break;
            case "worst-case escaping":
                projects = Enumerable.Range(0, 10).Select(i => new ProjectDescriptor
                {
                    Id = i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('\u0001', 253),
                    DisplayName = new string('\u0001', 256), ProjectPath = new string('\u0001', 4096),
                }).ToArray();
                sessions = Enumerable.Range(0, 500).Select(i => Session(i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('\u0001', 253)) with
                {
                    WorkspacePath = new string('\u0001', 4096), ProviderKey = new string('\u0001', 256),
                    Details = new RawApiSessionMetadataDetails(Title: new string('\u0001', 256)),
                }).ToArray();
                break;
        }
        if (row is "oversized id" or "oversized path" or "invalid identity" or "duplicate identity")
        {
            Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectSnapshot(projects, sessions));
            if (row == "oversized id")
            {
                Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectSnapshot([Project(new string('x', 257))], []));
                Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectSnapshot([], [Session("s") with { ProviderKey = new string('x', 257) }]));
            }
            if (row == "oversized path")
                Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectSnapshot([], [Session("s") with { WorkspacePath = new string('x', 4097) }]));
            if (row == "duplicate identity")
                Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectSnapshot([Project("same"), Project("same")], []));
            return;
        }
        var result = WorkspaceService.ProjectSnapshot(projects, sessions);
        Assert.IsTrue(result.Configured);
        Assert.IsTrue(result.Projects.Length <= 200);
        Assert.IsTrue(result.Sessions.Length <= 500);
        if (row == "persisted metadata")
        {
            Assert.AreEqual("s", result.Sessions[0].Id);
            Assert.AreEqual("Persisted title", result.Sessions[0].Title);
            Assert.AreEqual(sessions[0].UpdatedAt, result.Sessions[0].UpdatedAt);
            Assert.AreEqual("configured-provider", result.Sessions[0].ProviderKey);
            Assert.AreEqual("/literal/p", result.Sessions[0].WorkspacePath);
            Assert.IsTrue(result.Projects[0].Archived);
        }
        if (row == "empty") Assert.AreEqual(0, result.Projects.Length + result.Sessions.Length);
        if (row == "order")
        {
            Assert.AreEqual("a", result.Projects[0].Id);
            Assert.AreEqual("b", result.Sessions[0].Id);
        }
        if (row == "row limits")
        {
            Assert.AreEqual(200, result.Projects.Length);
            Assert.AreEqual(500, result.Sessions.Length);
            Assert.IsTrue(result.ProjectsTruncated && result.SessionsTruncated);
        }
        if (row == "display scalar boundary")
        {
            Assert.AreEqual(new string('x', 255), result.Projects[0].Name);
            Assert.AreEqual(new string('x', 255), result.Sessions[0].Title);
            Assert.IsTrue(result.DisplayTextTruncated);
        }
        if (row == "wire budget")
        {
            Assert.IsTrue(result.Projects.Length < 200);
            Assert.IsTrue(result.ProjectsTruncated);
        }
        if (row == "worst-case escaping")
        {
            Assert.IsTrue(result.Sessions.Length > 0 && result.Sessions.Length < 500);
            Assert.IsTrue(result.SessionsTruncated);
        }
        // Generated serialization only, not a live RPC host or native bridge.
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.WorkspaceSnapshot);
        Assert.IsTrue(bytes.Length < 768 * 1024);
    }

    private static ProjectDescriptor Project(string id) => new()
    {
        Id = id, DisplayName = id, ProjectPath = "/literal/" + id, Archived = true,
    };

    private static AgentSessionMetadata Session(string id) => new(
        id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1),
        WorkspacePath: "/literal/p", ProviderKey: "configured-provider",
        Details: new RawApiSessionMetadataDetails(Title: "Persisted title"));

    private sealed class LiteralSessions(AgentSessionMetadata[] rows, CancellationToken expected, Exception? failure)
        : IAsyncEnumerable<AgentSessionMetadata>
    {
        public IAsyncEnumerator<AgentSessionMetadata> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(expected, cancellationToken);
            return new Enumerator(rows, expected, failure);
        }

        private sealed class Enumerator(AgentSessionMetadata[] rows, CancellationToken token, Exception? failure)
            : IAsyncEnumerator<AgentSessionMetadata>
        {
            private int _index = -1;
            public AgentSessionMetadata Current => rows[_index];
            public ValueTask<bool> MoveNextAsync() => failure is not null ? ValueTask.FromException<bool>(failure)
                : token.IsCancellationRequested ? ValueTask.FromCanceled<bool>(token)
                : ValueTask.FromResult(++_index < rows.Length);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
