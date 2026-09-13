namespace CodeAlta.Tests;

/// <summary>Literal-only notes-delta restoration against the ten frozen 7c3a1279 originals; performs no reads.</summary>
internal static class OwnedSessionNotesSourceInverse
{
    private const string Store = "CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs";
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    private const string Reads = "CodeAlta.Orchestration/Runtime/OwnedSessionWorkspace.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string App = "CodeAlta/Desktop/DesktopApplication.cs";
    private const string Boot = "CodeAlta/Desktop/Rpc/BootRpc.cs";
    private const string Main = "CodeAlta/frontend/src/main.tsx";
    private const string AskInverse = "CodeAlta.Tests/OwnedSessionAskSourceInverse.cs";
    private const string Project = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    private const string Wiring = "CodeAlta.Desktop.Tests/DesktopOwnedSessionSourceTests.cs";
    internal static IReadOnlyList<string> Paths => [Store, Runtime, Reads, Host, App, Boot, Main, AskInverse, Project, Wiring];

    internal static string RestoreInput(string path, string source)
        => path is Store or Runtime or Reads or Host or App or Boot or Main or AskInverse or Project or Wiring ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.Canonicalize(source);
        foreach (var (before, after, count) in Edits(path))
        {
            Assert.IsTrue(after.Length > 0, path);
            Assert.AreEqual(count, source.Split(after, StringSplitOptions.None).Length - 1, path + ": " + after);
            source = source.Replace(after, before, StringComparison.Ordinal);
        }
        Assert.AreEqual(Original(path), OwnedSessionAskSourceInverse.GitObjectId(source), path);
        return source;
    }

    internal static string Original(string path) => path switch
    {
        Store => "d1acef1cdd4b607457a2e7ed67eb50f08ff34286",
        Runtime => "892411808f6bb841526bc0be8e3ae5d1a78784d5",
        Reads => "d8a7d6c28273f25ad294a77bc56beb70cb4dd985",
        Host => "59e9bd11f8265200205e9ca9d995a3d94a70966c",
        App => "c3515cf94c296c1993202794e1a2a6a3d2673f4d",
        Boot => "ecb59fe15c895f2fc93920afa23c2756e17a0517",
        Main => "0dbf2a2c420009e126ea65160141c8c73ca3efd6",
        AskInverse => "a7dfac2040930d771b79ca1f31d7569e8b40dffc",
        Project => "e39fadc605f73daf875d0d1dd57961a8884f0d27",
        Wiring => "0bdeb307494c5644f53acafa99ae3ddbd5157312",
        _ => throw new ArgumentException("Unlisted source.", nameof(path)),
    };

    private static IEnumerable<(string Before, string After, int Count)> Edits(string path)
    {
        switch (path)
        {
            case Store:
                yield return ("        return await _journalFile.WithPathLockAsync(path, async () =>\n", StoreRead + "\n", 1);
                yield return ("            return latest;\n        }, cancellationToken).ConfigureAwait(false);\n    }\n", "            return latest;\n        }, cancellationToken);\n", 1);
                break;
            case Runtime:
                yield return ("", RuntimeRead + "\n\n", 1);
                break;
            case Reads:
                yield return ("", "    private readonly Func<string, CancellationToken, Task<string>> _notes = static (_, _) => Task.FromException<string>(new InvalidOperationException(\"Notes reader not configured.\"));\n", 1);
                yield return ("    // Literal callback seam, internal to qualified tests; production uses only the constructor above.\n", ReadConstructor + "\n\n    // Literal callback seam, internal to qualified tests; production uses only the constructors above.\n", 1);
                yield return ("", WorkspaceRead + "\n\n", 1);
                break;
            case Host:
                yield return ("        WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore);\n", "        WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore, runtimeService);\n", 1);
                break;
            case App:
                yield return ("", "                    builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, epoch));\n", 1);
                break;
            case Boot:
                yield return ("", "[JsonSerializable(typeof(SessionNotesRequest))]\n[JsonSerializable(typeof(SessionNotesResponse))]\n", 1);
                break;
            case Main:
                yield return ("sessionAsks, type BootStatus", "sessionAsks, sessionNotes, type BootStatus", 1);
                yield return ("", "import { NotesPanel } from \"./NotesPanel\";\nimport { createNotesReader } from \"./sessionNotes\";\n", 1);
                yield return ("", "  const [notesReader] = useState(() => createNotesReader(sessionNotes.current));\n", 1);
                yield return ("", NotesPanel + "\n", 1);
                break;
            case AskInverse:
                yield return ("", "        source = OwnedSessionNotesSourceInverse.RestoreInput(path, source);\n", 1);
                break;
            case Project:
                yield return ("", "    <Compile Include=\"../CodeAlta.Tests/OwnedSessionNotesSourceInverse.cs\" Link=\"OwnedSessionNotesSourceInverse.cs\" />\n", 1);
                break;
            case Wiring:
                yield return ("        RequireOnce(host, \"WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore);\");\n", "        RequireOnce(host, \"WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore, runtimeService);\");\n", 1);
                yield return ("", "        StringAssert.Contains(reads, \"internal OwnedSessionWorkspace(ProjectCatalog projects, SessionViewJournalStore journals, SessionRuntimeService runtime)\");\n        StringAssert.Contains(reads, \"_notes = runtime.GetOwnedNotesMarkdownAsync;\");\n", 1);
                break;
        }
    }

    // Zero-indented raw-string terminators deliberately preserve every leading source space.
    private const string StoreRead = """
        return await ReadLatestNotesAtPathAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the latest notes with lexical containment of the notes-content open.</summary>
    /// <param name="sessionId">An existing session identifier, never a path grant.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The last canonical notes event in journal order, or null. Existing trailing-record tolerance applies.</returns>
    /// <remarks>Shares the legacy parser and journal lock. The complete scan is not bounded by a renderer limit.
    /// Containment does not cover prior cache metadata/existence probes, reparse points or external races.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="InvalidOperationException">No session exists.</exception>
    /// <exception cref="AgentSessionHistoryException">The resolved notes path is outside the sessions root.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="InvalidDataException">A notes event has no Markdown.</exception>
    /// <exception cref="JsonException">A canonical record is malformed.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<AgentNotesEvent?> ReadLatestNotesContainedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await GetExistingSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, ReadLatestNotesAtPathAsync, cancellationToken).ConfigureAwait(false);
    }

    private Task<AgentNotesEvent?> ReadLatestNotesAtPathAsync(string path, CancellationToken cancellationToken)
        => _journalFile.WithPathLockAsync(path, async () =>
""";

    private const string RuntimeRead = """
    /// <summary>Reads known-session notes with lexical containment of the notes-content open.</summary>
    /// <param name="sessionId">A backend-resolved active or recoverable session, not a renderer path or descriptor.</param>
    /// <param name="cancellationToken">Cancels lookup/read or the caller wait; runtime retains admitted work.</param>
    /// <returns>Exact latest Markdown, or empty for no notes, an empty Set or Clear.</returns>
    /// <remarks>No provider activation, writes or events. Scan costs, prior cache probes and reparse/external races
    /// are not bounded or isolated by the content-open containment check.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches the configured scope.</exception>
    /// <exception cref="InvalidOperationException">The resolved journal no longer exists.</exception>
    /// <exception cref="AgentSessionHistoryException">The notes-content path is outside the sessions root.</exception>
    /// <exception cref="IOException">The journal cannot be read or its notes are invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="System.Text.Json.JsonException">A canonical journal record is malformed.</exception>
    /// <exception cref="ObjectDisposedException">Runtime admission has closed.</exception>
    /// <exception cref="OperationCanceledException">The operation or caller wait is canceled.</exception>
    public async Task<string> GetOwnedNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetOwnedNotesMarkdownBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<string> GetOwnedNotesMarkdownBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .ReadLatestNotesContainedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return notes is null ? string.Empty : notes.Markdown;
    }
""";

    private const string ReadConstructor = """
    internal OwnedSessionWorkspace(ProjectCatalog projects, SessionViewJournalStore journals, SessionRuntimeService runtime)
        : this(projects, journals)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _notes = runtime.GetOwnedNotesMarkdownAsync;
    }
""";

    private const string WorkspaceRead = """
    internal OwnedSessionWorkspace(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> sessions,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> history,
        Func<string, CancellationToken, Task<string>> notes)
        : this(projects, sessions, history)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _notes = notes;
    }

    /// <summary>Reads complete notes through the same eight-actual-read admission and drain.</summary>
    /// <param name="sessionId">An explicit backend session identity.</param>
    /// <param name="cancellationToken">Cancels only this wait, never the retained actual read.</param>
    /// <returns>Exact latest Markdown, including empty notes.</returns>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Eight actual reads are admitted (synchronous admission refusal).</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">An admitted downstream read fails asynchronously.</exception>
    public Task<string> ReadNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _notes(sessionId, CancellationToken.None), cancellationToken);
    }
""";

    private const string NotesPanel = """
          {status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch && <NotesPanel
            key={JSON.stringify([selectedSession.id, status.hostEpoch, "notes"])} epoch={status.hostEpoch} sessionId={selectedSession.id}
            reader={notesReader} capability={mutation.capability} />}
""";
}
