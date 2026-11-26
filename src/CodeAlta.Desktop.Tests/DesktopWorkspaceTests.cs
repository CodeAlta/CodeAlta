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
    public void Projection_SaysWhichSessionsHaveAConversationOfTheirProvider_OnlyForThem()
    {
        var snapshot = WorkspaceService.ProjectSnapshot([],
        [
            Session("cli") with { Details = new RawApiSessionMetadataDetails(ProviderSessionId: "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01", Title: "With") },
            Session("none"),
        ]);
        Assert.IsTrue(snapshot.Sessions.Single(row => row.Id == "cli").ProviderConversation);
        Assert.IsNull(snapshot.Sessions.Single(row => row.Id == "none").ProviderConversation);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot));
        var rows = json.RootElement.GetProperty("sessions").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!);
        Assert.IsTrue(rows["cli"].GetProperty("providerConversation").GetBoolean());
        Assert.IsFalse(rows["none"].TryGetProperty("providerConversation", out _), "Absent, not false: the row costs nothing more.");
        Assert.IsFalse(rows["cli"].GetRawText().Contains("5b0c8b0e", StringComparison.Ordinal), "The identifier itself is not listed.");
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
        var project = Path.Combine(parent, "project");
        var home = Path.Combine(parent, "home");
        var builtin = Path.Combine(parent, "builtin");
        string[] owned = ["--allow-owned-host", "--project-root", project, "--discovery-home", home,
            "--instruction-root", parent, "--builtin-skill-root", builtin];
        string[] args = ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache", .. owned];
        switch (row)
        {
            case "reordered": args = [.. owned, "--allow-catalog-cache", "--catalog-root", catalog, "--data-root", data]; break;
            case "missing data": args = ["--catalog-root", catalog, "--allow-catalog-cache", .. owned]; break;
            case "relative data": args[1] = "relative"; break;
            case "alta data": args[1] = Path.Combine(parent, ".ALTA", "browser"); break;
            case "alta before traversal": args[1] = Path.Combine(parent, ".alta", "..", "browser"); break;
            case "missing consent": args = ["--data-root", data, "--catalog-root", catalog, .. owned]; break;
            case "consent only": args = ["--data-root", data, "--allow-catalog-cache", .. owned]; break;
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
            path == project || path == home || path == parent || path == builtin ||
            (path == catalog && row is not "missing catalog" and not "catalog file") ||
            (path == data && row == "existing data directory") || (path == Path.GetPathRoot(parent) && row == "catalog volume root");
        bool FileExists(string path) =>
            (path == data && row == "existing data file") || (path == catalog && row == "catalog file");

        Assert.AreEqual(expected, DesktopCommandLine.TryParse(args, DirectoryExists, FileExists, out var options, out var error));
        if (expected)
        {
            Assert.IsNotNull(options);
            Assert.AreEqual(data, options.DataRoot);
            Assert.AreEqual(catalog, options.CatalogRoot);
            Assert.AreEqual(new OwnedDesktopRoots(project, home, parent, builtin), options.Owned);
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
                projects = Enumerable.Range(0, WorkspaceService.MaximumProjects + 1).Select(i => Project($"p{i:D4}")).ToArray();
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
        Assert.IsTrue(result.Projects.Length <= WorkspaceService.MaximumProjects);
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
            // The projects stop at their share of the wire budget, and the sessions keep most of it.
            Assert.IsTrue(result.Projects.Length > 600, result.Projects.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsTrue(result.Sessions.Length > 400, result.Sessions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var few = WorkspaceService.ProjectSnapshot(projects[..201], sessions);
            Assert.AreEqual((201, 500), (few.Projects.Length, few.Sessions.Length), "Two hundred projects leave every session listed.");
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

    [TestMethod]
    public void Projection_NeverNamedProjectSessionShowsFirstLineOfSummary()
    {
        // Real persisted data: the title stays frozen at the text the session was created with while the summary evolves.
        var session = Session("unnamed") with
        {
            Summary = "Catalogue OK. Je regarde le format des agents.\nsecond line",
            Details = new RawApiSessionMetadataDetails(Title: "Project session for p."),
        };
        var snapshot = WorkspaceService.ProjectSnapshot([Project("p")], [session], new Dictionary<string, SessionViewJournalHeader> { ["unnamed"] = new() { SessionId = "unnamed", Kind = SessionViewKind.ProjectSession, ProjectRef = "p", CreatedAt = session.CreatedAt, WorkingDirectory = session.WorkspacePath! } });
        Assert.AreEqual("Catalogue OK. Je regarde le format des agents.", snapshot.Sessions[0].Title);
    }

    [TestMethod]
    public void Projection_NeverNamedSessionOfAProjectWithALongNameShowsFirstLineOfSummary()
    {
        // The runtime keeps the first 80 characters of the summary a session is created with as its title.
        var project = Project("p");
        project.DisplayName = new string('n', 60);
        var session = Session("unnamed") with
        {
            Summary = "The answer of the model.",
            Details = new RawApiSessionMetadataDetails(Title: $"Project session for {project.DisplayName}."[..80]),
        };
        var snapshot = WorkspaceService.ProjectSnapshot([project], [session], ProjectHeader(session));
        Assert.AreEqual("The answer of the model.", snapshot.Sessions[0].Title);
    }

    [TestMethod]
    [DataRow("😀tail")]
    [DataRow(" tail")]
    public void Projection_NeverNamedSessionShowsAtMost80CharactersOfItsSummary(string end)
    {
        // The title is complete as it is listed, since a deletion is confirmed with it: it is cut between two
        // characters and has no space at its end.
        var session = Session("unnamed") with
        {
            Summary = new string('x', 79) + end,
            Details = new RawApiSessionMetadataDetails(Title: "Project session for p."),
        };
        var snapshot = WorkspaceService.ProjectSnapshot([Project("p")], [session], ProjectHeader(session));
        Assert.AreEqual(new string('x', 79), snapshot.Sessions[0].Title);
        Assert.AreEqual(snapshot.Sessions[0].Title, snapshot.Sessions[0].FullTitle);
        Assert.IsFalse(snapshot.Sessions[0].FullTitleTruncated || snapshot.DisplayTextTruncated);
    }

    [TestMethod]
    public void Projection_NamedSessionKeepsItsTitleWhateverItsSummary()
    {
        var session = Session("named") with { Summary = "Latest answer of the assistant" };
        var headers = new Dictionary<string, SessionViewJournalHeader>
        {
            ["named"] = new() { SessionId = "named", Kind = SessionViewKind.ProjectSession, ProjectRef = "p", CreatedAt = session.CreatedAt, WorkingDirectory = session.WorkspacePath! },
        };
        var snapshot = WorkspaceService.ProjectSnapshot([Project("p")], [session], headers);
        Assert.AreEqual("Persisted title", snapshot.Sessions[0].Title);
    }

    [TestMethod]
    public void Projection_SessionWithATitleSourceIsListedByItsSavedTitle()
    {
        // The session itself writes the title CodeAlta chose, and a Rename the one somebody chose: a list shows what
        // is saved, also the title the session was created with, and never the first line of the summary.
        var project = Project("p");
        foreach (var (source, title) in new[]
                 {
                     (AgentSessionTitleSource.Auto, project.DisplayName),
                     (AgentSessionTitleSource.Auto, "Fix the login error"),
                     (AgentSessionTitleSource.User, project.DisplayName),
                 })
        {
            var session = Session("titled") with
            {
                Summary = "Latest answer of the assistant",
                Details = new RawApiSessionMetadataDetails(Title: title) { TitleSource = source },
            };
            var snapshot = WorkspaceService.ProjectSnapshot([project], [session], ProjectHeader(session));
            Assert.AreEqual(title, snapshot.Sessions[0].Title, source.ToString());
        }
    }

    [TestMethod]
    public void Projection_NeverNamedGlobalSessionShowsFirstLineOfSummary()
    {
        var session = Session("global") with
        {
            Summary = "Checking the catalog",
            Details = new RawApiSessionMetadataDetails(Title: "Global Session"),
        };
        var headers = new Dictionary<string, SessionViewJournalHeader>
        {
            ["global"] = new() { SessionId = "global", Kind = SessionViewKind.GlobalSession, CreatedAt = session.CreatedAt, WorkingDirectory = session.WorkspacePath! },
        };
        var snapshot = WorkspaceService.ProjectSnapshot([], [session], headers);
        Assert.AreEqual("Checking the catalog", snapshot.Sessions[0].Title);
    }

    [TestMethod]
    public void Projection_SessionOfAFolderAddedSinceIsListedUnderItsProject_NeverOneOfAnotherProject()
    {
        // altacli records the transient project of a folder outside the catalog; the project added later for that folder
        // has another id. A session that records another project of the catalog is never given to the project of its folder.
        var transient = Session("transient");
        var foreign = Session("foreign");
        var projects = new[] { Project("p"), Project("q") };
        var headers = new Dictionary<string, SessionViewJournalHeader>
        {
            ["transient"] = new() { SessionId = "transient", Kind = SessionViewKind.ProjectSession, ProjectRef = Guid.NewGuid().ToString("D"), CreatedAt = transient.CreatedAt, WorkingDirectory = transient.WorkspacePath! },
            ["foreign"] = new() { SessionId = "foreign", Kind = SessionViewKind.ProjectSession, ProjectRef = "q", CreatedAt = foreign.CreatedAt, WorkingDirectory = foreign.WorkspacePath! },
        };

        var snapshot = WorkspaceService.ProjectSnapshot(projects, [transient, foreign], headers);

        var adopted = snapshot.Sessions.Single(session => session.Id == "transient");
        Assert.AreEqual("project", adopted.ScopeKind);
        Assert.AreEqual("p", adopted.ProjectId);
        var other = snapshot.Sessions.Single(session => session.Id == "foreign");
        Assert.IsNull(other.ScopeKind);
        Assert.IsNull(other.ProjectId);
    }

    // A burst of sessions in one project once filled the snapshot, and every other project showed no session at all.
    [TestMethod]
    public void Projection_AProjectWithManyRecentSessions_TakesTheRoomOfItsOwnOlderOnes_NeverTheSessionsOfAnotherProject()
    {
        ProjectDescriptor[] projects = [Project("busy"), Project("quiet"), Project("unused")];
        var (sessions, headers) = (new List<AgentSessionMetadata>(), new Dictionary<string, SessionViewJournalHeader>());
        // 700 sessions of one project, each newer than the 8 sessions of another project and than the 3 chats.
        for (var index = 0; index < 700; index++) Add(sessions, headers, $"busy-{index:D3}", "busy", DateTimeOffset.UnixEpoch.AddDays(30).AddMinutes(-index));
        for (var index = 0; index < 8; index++) Add(sessions, headers, $"quiet-{index}", "quiet", DateTimeOffset.UnixEpoch.AddDays(2).AddMinutes(-index));
        for (var index = 0; index < 3; index++) Add(sessions, headers, $"chat-{index}", null, DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(-index));

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers);

        Assert.AreEqual(WorkspaceService.MaximumSessions, snapshot.Sessions.Length);
        Assert.IsTrue(snapshot.SessionsTruncated);
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(static index => $"quiet-{index}").ToArray(),
            snapshot.Sessions.Where(static session => session.ProjectId == "quiet").Select(static session => session.Id).ToArray(), "Every session of the other project is listed.");
        Assert.AreEqual(3, snapshot.Sessions.Count(static session => session.ScopeKind == "global"), "Every chat is listed.");
        // The busy project has the rest of the room, for its newest sessions, and says how many older ones are left out.
        CollectionAssert.AreEqual(Enumerable.Range(0, 489).Select(static index => $"busy-{index:D3}").ToArray(),
            snapshot.Sessions.Where(static session => session.ProjectId == "busy").Select(static session => session.Id).ToArray());
        Assert.AreEqual(211, snapshot.Projects.Single(static project => project.Id == "busy").OmittedSessions);
        Assert.IsNull(snapshot.Projects.Single(static project => project.Id == "quiet").OmittedSessions);
        Assert.IsNull(snapshot.Projects.Single(static project => project.Id == "unused").OmittedSessions);
        Assert.IsNull(snapshot.OmittedChatSessions);
        // The rows keep the order the lists show: the newest first.
        CollectionAssert.AreEqual(snapshot.Sessions.OrderByDescending(static session => session.UpdatedAt).Select(static session => session.Id).ToArray(),
            snapshot.Sessions.Select(static session => session.Id).ToArray());
    }

    [TestMethod]
    public void Projection_WithMoreSessionsThanRoom_EveryProjectKeepsItsNewest_AndTheNewestOfTheWorkspaceAreAllListed()
    {
        var projects = Enumerable.Range(0, 100).Select(static index => Project($"p{index:D3}")).ToArray();
        var (sessions, headers) = (new List<AgentSessionMetadata>(), new Dictionary<string, SessionViewJournalHeader>());
        // The 60 newest sessions of the workspace are in one project; every project has 10 older ones; some chats are older still.
        for (var index = 0; index < 60; index++) Add(sessions, headers, $"new-{index:D2}", "p000", DateTimeOffset.UnixEpoch.AddDays(30).AddMinutes(-index));
        foreach (var project in projects)
        {
            for (var index = 0; index < 10; index++) Add(sessions, headers, $"{project.Id}-{index}", project.Id, DateTimeOffset.UnixEpoch.AddDays(10).AddMinutes(-index));
        }
        for (var index = 0; index < 20; index++) Add(sessions, headers, $"chat-{index:D2}", null, DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(-index));
        // An archived session is kept after the others: it takes no room from a list of the Explorer.
        Add(sessions, headers, "archived", "p001", DateTimeOffset.UnixEpoch.AddDays(40));
        sessions[^1] = sessions[^1] with { ViewState = new AgentSessionViewStateMetadata(Archived: true) };

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers);

        Assert.AreEqual(WorkspaceService.MaximumSessions, snapshot.Sessions.Length);
        var listed = snapshot.Sessions.Select(static session => session.Id).ToHashSet();
        for (var index = 0; index < 50; index++) Assert.IsTrue(listed.Contains($"new-{index:D2}"), $"new-{index:D2}");
        foreach (var project in snapshot.Projects)
        {
            var own = snapshot.Sessions.Where(session => session.ProjectId == project.Id).ToArray();
            Assert.IsTrue(own.Length >= 4, $"{project.Id}: {own.Length}");
            var all = sessions.Count(session => headers[session.SessionId].ProjectRef == project.Id && session.ViewState?.Archived != true);
            Assert.AreEqual(all - own.Length, project.OmittedSessions ?? 0, project.Id);
        }
        var chats = snapshot.Sessions.Count(static session => session.ScopeKind == "global");
        Assert.IsTrue(chats >= 4, chats.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(20 - chats, snapshot.OmittedChatSessions ?? 0);
        Assert.IsFalse(listed.Contains("archived"));
    }

    // The catalog of the tests below: 700 sessions of one project, 8 of another and 3 chats, from the newest.
    private static (ProjectDescriptor[] Projects, List<AgentSessionMetadata> Sessions, Dictionary<string, SessionViewJournalHeader> Headers) BusyCatalog()
    {
        var (sessions, headers) = (new List<AgentSessionMetadata>(), new Dictionary<string, SessionViewJournalHeader>());
        for (var index = 0; index < 700; index++) Add(sessions, headers, $"busy-{index:D3}", "busy", DateTimeOffset.UnixEpoch.AddDays(30).AddMinutes(-index));
        for (var index = 0; index < 8; index++) Add(sessions, headers, $"quiet-{index}", "quiet", DateTimeOffset.UnixEpoch.AddDays(2).AddMinutes(-index));
        for (var index = 0; index < 3; index++) Add(sessions, headers, $"chat-{index}", null, DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(-index));
        return ([Project("busy"), Project("quiet")], sessions, headers);
    }

    private static string[] Ids(WorkspaceSnapshot snapshot, string project)
        => [.. snapshot.Sessions.Where(session => session.ProjectId == project).Select(static session => session.Id)];

    // An old session was out of reach: no tab, no link and no search of the window could show it (#483).
    [TestMethod]
    public void Projection_ASessionTheWindowNames_IsListedWhateverItsAge_WithTheSessionItIsUnder()
    {
        var (projects, sessions, headers) = BusyCatalog();
        // The oldest session of the busy project is a sub-agent of another old one.
        sessions[699] = sessions[699] with { ParentSessionId = "busy-650" };
        var first = WorkspaceService.ProjectSnapshot(projects, sessions, headers);
        Assert.IsFalse(first.Sessions.Any(static session => session.Id is "busy-699" or "busy-650"));

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Sessions = ["busy-699", "gone", null!] });

        CollectionAssert.AreEquivalent(first.Sessions.Select(static session => session.Id).Append("busy-699").Append("busy-650").ToArray(),
            snapshot.Sessions.Select(static session => session.Id).ToArray(), "What every list shows at first is the same.");
        Assert.AreEqual("busy-650", snapshot.Sessions.Single(static session => session.Id == "busy-699").ParentSessionId);
        // The rows keep the order the lists show: the newest first.
        CollectionAssert.AreEqual(snapshot.Sessions.OrderByDescending(static session => session.UpdatedAt).Select(static session => session.Id).ToArray(),
            snapshot.Sessions.Select(static session => session.Id).ToArray());
        Assert.IsNull(snapshot.WantedTruncated);
    }

    [TestMethod]
    public void Projection_AListTheWindowExtends_HasItsRowsWithTheirSubAgents_AndCountsTheRowsLeftOut()
    {
        var (projects, sessions, headers) = BusyCatalog();
        // The row 495 has the oldest session as a sub-agent; the session before the oldest is a sub-agent of a row left out.
        sessions[699] = sessions[699] with { ParentSessionId = "busy-495" };
        sessions[698] = sessions[698] with { ParentSessionId = "busy-697" };
        var first = WorkspaceService.ProjectSnapshot(projects, sessions, headers);
        // 489 rows are listed at first: 211 sessions are left out, two of them sub-agents, which are not rows of the list.
        Assert.AreEqual(209, first.Projects.Single(static project => project.Id == "busy").OmittedSessions);

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Lists = [new("busy", 496), new("quiet", 4), new("unknown", 9)] });

        CollectionAssert.AreEqual(Enumerable.Range(0, 496).Select(static index => $"busy-{index:D3}").Append("busy-699").ToArray(), Ids(snapshot, "busy"));
        Assert.AreEqual(700 - 497 - 1, snapshot.Projects.Single(static project => project.Id == "busy").OmittedSessions);
        Assert.HasCount(8, Ids(snapshot, "quiet"), "The other lists are as they were.");
        Assert.AreEqual(3, snapshot.Sessions.Count(static session => session.ScopeKind == "global"));
    }

    [TestMethod]
    public void Projection_TheRowsOfAnExtendedList_AreInTheOrderOfWhatWasDoneLastUnderThem()
    {
        var (projects, sessions, headers) = BusyCatalog();
        // An old session whose sub-agent worked last is the first row of its list, as the window shows it.
        Add(sessions, headers, "old-row", "busy", DateTimeOffset.UnixEpoch);
        Add(sessions, headers, "new-child", "busy", DateTimeOffset.UnixEpoch.AddDays(31));
        sessions[^1] = sessions[^1] with { ParentSessionId = "old-row" };
        Assert.IsFalse(WorkspaceService.ProjectSnapshot(projects, sessions, headers).Sessions.Any(static session => session.Id == "old-row"));

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Lists = [new("busy", 1)] });

        Assert.IsTrue(snapshot.Sessions.Any(static session => session.Id == "old-row"));
        Assert.AreEqual("old-row", snapshot.Sessions.Single(static session => session.Id == "new-child").ParentSessionId);
    }

    [TestMethod]
    public void Projection_ASearchOfTheWindow_FindsTheSessionsLeftOut_ByTheirTitleOrTheirId()
    {
        var (projects, sessions, headers) = BusyCatalog();
        sessions[600] = sessions[600] with { Details = new RawApiSessionMetadataDetails(Title: "Fix the Zebra parser") };
        sessions[650] = sessions[650] with { Details = new RawApiSessionMetadataDetails(Title: "Zebra crossing") };
        WorkspaceSnapshot Search(string text, bool scoped = false, string? project = null)
            => WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Searches = [new(text, scoped, project)] });
        var first = WorkspaceService.ProjectSnapshot(projects, sessions, headers);

        // Every word, whatever its case and its order.
        CollectionAssert.AreEqual(new[] { "busy-600" }, Search("  PARSER zebra ").Sessions.Select(static session => session.Id).Except(first.Sessions.Select(static session => session.Id)).ToArray());
        Assert.HasCount(first.Sessions.Length + 2, Search("zebra").Sessions);
        Assert.HasCount(first.Sessions.Length + 1, Search("busy-699").Sessions, "A session is found by its identifier.");
        Assert.HasCount(first.Sessions.Length, Search("nothing has this").Sessions);
        // A word that names a label finds the sessions that have one whose id starts so, with the other words in their title.
        sessions[650] = sessions[650] with { ViewState = new AgentSessionViewStateMetadata(Archived: false) { Labels = ["audit-weekly"] } };
        CollectionAssert.AreEqual(new[] { "busy-650" }, Search("label:audit").Sessions.Select(static session => session.Id).Except(first.Sessions.Select(static session => session.Id)).ToArray());
        Assert.HasCount(first.Sessions.Length + 1, Search("zebra label:AUDIT").Sessions);
        Assert.HasCount(first.Sessions.Length, Search("parser label:audit").Sessions);
        Assert.HasCount(first.Sessions.Length, Search("label:weekly").Sessions);
        // A search of one list leaves the others alone.
        Assert.HasCount(first.Sessions.Length, Search("zebra", scoped: true, project: "quiet").Sessions);
        Assert.HasCount(first.Sessions.Length, Search("zebra", scoped: true).Sessions);
        Assert.HasCount(first.Sessions.Length + 2, Search("zebra", scoped: true, project: "busy").Sessions);
        // A text that many sessions have adds the newest of those left out, and no more than a search adds.
        var many = Search("busy");
        Assert.HasCount(first.Sessions.Length + WorkspaceService.MaximumFoundSessions, many.Sessions);
        Assert.IsTrue(many.Sessions.Any(static session => session.Id == "busy-489") && !many.Sessions.Any(static session => session.Id == "busy-699"));
        // A search of one list finds all of them: the saved sessions of a project show every session found.
        Assert.HasCount(700, Ids(Search("busy", scoped: true, project: "busy"), "busy"));
        // The name of the project counts with a word of the title, as in the search of the window; alone, it finds no session.
        Add(sessions, headers, "odd-one", "quiet", DateTimeOffset.UnixEpoch.AddDays(-1));
        sessions[^1] = sessions[^1] with { Details = new RawApiSessionMetadataDetails(Title: "Yak shaving") };
        for (var index = 0; index < 600; index++) Add(sessions, headers, $"filler-{index:D3}", "quiet", DateTimeOffset.UnixEpoch.AddDays(20).AddMinutes(-index));
        Assert.IsFalse(Search("zebra").Sessions.Any(static session => session.Id == "odd-one"));
        Assert.IsTrue(Search("QUIET yak").Sessions.Any(static session => session.Id == "odd-one"));
        Assert.IsFalse(Search("quiet").Sessions.Any(static session => session.Id == "odd-one"));
    }

    [TestMethod]
    public void Projection_TheArchivedSessions_AreListedWhenTheWindowShowsThem_OrSearchesThem()
    {
        var (projects, sessions, headers) = BusyCatalog();
        Add(sessions, headers, "kept-away", "quiet", DateTimeOffset.UnixEpoch.AddDays(40));
        sessions[^1] = sessions[^1] with { ViewState = new AgentSessionViewStateMetadata(Archived: true), Details = new RawApiSessionMetadataDetails(Title: "Put away") };

        Assert.IsFalse(WorkspaceService.ProjectSnapshot(projects, sessions, headers).Sessions.Any(static session => session.Archived));
        Assert.AreEqual("kept-away", WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Archived = true }).Sessions.Single(static session => session.Archived).Id);
        Assert.AreEqual("kept-away", WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Searches = [new("put away", false, null)] }).Sessions.Single(static session => session.Archived).Id);
    }

    [TestMethod]
    public void Projection_WhenWhatIsAskedForHasNoRoom_TheSnapshotSaysSo_AndStaysUnderTheWireLimit()
    {
        var (projects, sessions, headers) = BusyCatalog();
        // Titles that take all the room a title can take on the wire.
        for (var index = 0; index < 700; index++) sessions[index] = sessions[index] with { Details = new RawApiSessionMetadataDetails(Title: new string('é', 5000)) };

        var snapshot = WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: new() { Lists = [new("busy", 600)] });

        Assert.IsTrue(snapshot.WantedTruncated);
        Assert.IsTrue(snapshot.Sessions.Length is > 10 and < 100, snapshot.Sessions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot).Length < 768 * 1024);
        Assert.IsNull(WorkspaceService.ProjectSnapshot(projects, sessions, headers).WantedTruncated, "Nothing was asked for.");
    }

    // With several spaces, the chats of another space counted in "Show more" and took the results of a search.
    [TestMethod]
    public void Projection_ForAWindowThatShowsOneSpace_TheRowsAskedFor_AndThoseLeftOut_AreTheOnesOfThatSpace()
    {
        SpaceDescriptor[] spaces = [SpaceDescriptor.CreateDefault(), new() { Id = "work", Name = "Work" }];
        ProjectDescriptor[] projects = [Project("shared"), Project("mine")];
        projects[1].Spaces = ["work"];
        var (sessions, headers) = (new List<AgentSessionMetadata>(), new Dictionary<string, SessionViewJournalHeader>());
        void AddIn(string id, string? project, string? space, DateTimeOffset updated)
        {
            Add(sessions, headers, id, project, updated);
            sessions[^1] = sessions[^1] with { SpaceId = space };
        }
        // 750 chats of the default space, newer than everything of the space "work": its 30 chats, an old session of a
        // project of every space that records it, and the sessions of its own project, which record no space.
        for (var index = 0; index < 750; index++) AddIn($"other-{index:D3}", null, null, DateTimeOffset.UnixEpoch.AddDays(30).AddMinutes(-index));
        for (var index = 0; index < 30; index++) AddIn($"work-chat-{index:D2}", null, "work", DateTimeOffset.UnixEpoch.AddDays(2).AddMinutes(-index));
        AddIn("work-in-shared", "shared", "work", DateTimeOffset.UnixEpoch.AddDays(1));
        AddIn("default-in-shared", "shared", null, DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(-1));
        AddIn("work-in-mine", "mine", null, DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(-2));
        WorkspaceSnapshot Read(WorkspaceRequest wanted) => WorkspaceService.ProjectSnapshot(projects, sessions, headers, wanted: wanted, spaces: spaces);
        int Chats(WorkspaceSnapshot snapshot, string prefix) => snapshot.Sessions.Count(session => session.ScopeKind == "global" && session.Id.StartsWith(prefix, StringComparison.Ordinal));

        // Every space: the chats left out are those of both spaces.
        var all = Read(new());
        Assert.AreEqual(750 + 30 - Chats(all, "other-") - Chats(all, "work-chat-"), all.OmittedChatSessions);

        // The space "work": only its own chats are counted, and "Show more" brings them, not the newer ones of the default space.
        var work = Read(new() { Space = "work" });
        Assert.AreEqual(30 - Chats(work, "work-chat-"), work.OmittedChatSessions ?? 0);
        var more = Read(new() { Space = "work", Lists = [new(null, 30)] });
        Assert.AreEqual(30, Chats(more, "work-chat-"));
        Assert.AreEqual(Chats(work, "other-"), Chats(more, "other-"));
        Assert.IsNull(more.OmittedChatSessions);
        // The default space: its chats, never those of "work".
        var main = Read(new() { Space = "default", Lists = [new(null, 600)] });
        Assert.AreEqual(600, Chats(main, "other-"));
        Assert.AreEqual(150, main.OmittedChatSessions);

        // A search finds the sessions of the space shown: 200 more of them, not 200 of another space.
        Assert.AreEqual(30, Chats(Read(new() { Space = "work", Searches = [new("chat", false, null)] }), "work-chat-"));
        var found = Read(new() { Space = "work", Searches = [new("other", false, null)] });
        Assert.HasCount(work.Sessions.Length, found.Sessions, "The chats of the default space are not looked for.");

        // A session is in the space it records, else in the spaces of its project, else in the default space.
        var scope = new WorkspaceService.SessionScope("project", "shared", null, "work");
        Assert.IsTrue(WorkspaceService.InSpace(scope, projects[0], "work", spaces));
        Assert.IsFalse(WorkspaceService.InSpace(scope, projects[0], "default", spaces));
        Assert.IsTrue(WorkspaceService.InSpace(scope with { RecordedSpace = null }, projects[0], "default", spaces));
        Assert.IsTrue(WorkspaceService.InSpace(scope with { ProjectId = "mine", RecordedSpace = null }, projects[1], "work", spaces));
        Assert.IsFalse(WorkspaceService.InSpace(scope with { ProjectId = "mine", RecordedSpace = null }, projects[1], "default", spaces));
        Assert.IsTrue(WorkspaceService.InSpace(scope with { RecordedSpace = "gone" }, projects[0], "default", spaces), "A space that is gone is the default one.");
        // A session that records no scope is listed by its folder, which the window looks at: it is never kept out.
        Assert.IsTrue(WorkspaceService.InSpace(new(null, null, null, null), null, "work", spaces));
        // A space the catalog does not have shows what every space does.
        Assert.AreEqual(all.OmittedChatSessions, Read(new() { Space = "unknown" }).OmittedChatSessions);
    }

    // The room of a row is what its texts take on the wire: plain titles and paths take a sixth of what was reserved for them.
    [TestMethod]
    public void Projection_RowsOfPlainText_FitTheSessionLimit_WithLongTitlesAndPaths()
    {
        var sessions = Enumerable.Range(0, 600).Select(index => new AgentSessionMetadata($"{Guid.Empty:D}-{index:D4}",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(index), WorkspacePath: "/home/someone/code/" + new string('p', 80),
            Details: new RawApiSessionMetadataDetails(Title: new string('t', 200)))).ToArray();
        var snapshot = WorkspaceService.ProjectSnapshot([], sessions);
        Assert.HasCount(WorkspaceService.MaximumSessions, snapshot.Sessions);
        Assert.IsTrue(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot).Length < 768 * 1024);
    }

    private static void Add(List<AgentSessionMetadata> sessions, Dictionary<string, SessionViewJournalHeader> headers, string id, string? project, DateTimeOffset updated)
    {
        // A session of a project works in the folder of that project.
        var session = Session(id) with { UpdatedAt = updated, WorkspacePath = project is null ? "/literal/chats" : "/literal/" + project };
        sessions.Add(session);
        headers[id] = new()
        {
            SessionId = id, Kind = project is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession, ProjectRef = project,
            CreatedAt = session.CreatedAt, WorkingDirectory = session.WorkspacePath!,
        };
    }

    private static Dictionary<string, SessionViewJournalHeader> ProjectHeader(AgentSessionMetadata session) => new()
    {
        [session.SessionId] = new() { SessionId = session.SessionId, Kind = SessionViewKind.ProjectSession, ProjectRef = "p", CreatedAt = session.CreatedAt, WorkingDirectory = session.WorkspacePath! },
    };

    [TestMethod]
    public void Snapshot_MarksTheProjectsOutsideGit_AndNoOther()
    {
        var root = Directory.CreateTempSubdirectory("codealta-repositories-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root.FullName, "repository")).FullName;
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var inside = Directory.CreateDirectory(Path.Combine(repository, "services", "billing")).FullName;
            var notes = Directory.CreateDirectory(Path.Combine(root.FullName, "notes")).FullName;
            var snapshot = new WorkspaceSnapshot(true,
            [
                new WorkspaceProject("repository", "repository", repository, false),
                new WorkspaceProject("billing", "billing", inside, false),
                new WorkspaceProject("notes", "notes", notes, false),
                new WorkspaceProject("old", "old", notes, true),
                new WorkspaceProject("machine", "pc", "machine:pc", false) { MachineId = "pc" },
            ], [], false, false, false);

            var marked = WorkspaceService.WithRepositories(snapshot, static path => CodeAlta.Catalog.Worktrees.GitWorktreeService.FindCheckoutRoot(path) is not null);

            CollectionAssert.AreEqual(new[] { "notes" }, marked.Projects.Where(static project => project.NoRepository == true).Select(static project => project.Id).ToArray(),
                "a folder under a repository is in it; an archived project and a machine are not asked");
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
