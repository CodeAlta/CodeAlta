using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Finds the conversations of Claude Code started in VS Code or by the CLI in a terminal in the folder of a project,
/// which no session of CodeAlta continues, reads one whole to import it, and describes one to open it in VS Code or a
/// terminal. The transcripts are only read.
/// </summary>
/// <remarks>
/// A listing reads the first and the last 64 KB of each transcript only, as the session list of VS Code does, and lists
/// what that list shows: not the sessions programs started (CodeAlta's own included), daemons, sidechains, sessions
/// continued in another transcript, nor those without a prompt. What was read of a file is kept while its length and its
/// last write time stay the same, so that a listing reads again only what changed.
/// </remarks>
internal sealed class ClaudeCodeExternalConversations
{
    // The most transcripts of a folder looked at, the most recently written first.
    internal const int MaxTranscriptsScanned = 1000;

    private const int MaxCachedHeaders = 4096;
    // The conversations read whole to be shown, kept while their transcript does not change: the one shown, and the one before.
    internal const int MaxCachedPreviews = 2;
    private const string TranscriptExtension = ".jsonl";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _configRoot;
    private readonly ClaudeCodeLiveSessions? _liveSessions;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedHeader> _headers = new(PathComparer);
    private readonly List<CachedPreview> _previews = [];

    /// <summary>Initializes a new instance of the <see cref="ClaudeCodeExternalConversations"/> class.</summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="liveSessions">The registry of live sessions, which tells whether a conversation is open elsewhere; null to tell none is.</param>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> is empty.</exception>
    public ClaudeCodeExternalConversations(string configRoot, ClaudeCodeLiveSessions? liveSessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        _configRoot = configRoot;
        _liveSessions = liveSessions;
    }

    /// <summary>Lists the conversations started outside CodeAlta in a folder, the most recently written first.</summary>
    /// <param name="workingDirectory">The folder of the project.</param>
    /// <param name="isKnown">Whether a session of CodeAlta continues a conversation already.</param>
    /// <param name="offset">How many conversations are skipped before the page.</param>
    /// <param name="count">The most conversations returned.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations of the page and how many there are in all.</returns>
    /// <remarks>
    /// The total counts every listed conversation among the <see cref="MaxTranscriptsScanned"/> most recently written
    /// transcripts of the folder; only the conversations of the page are looked up in the registry of live sessions.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="isKnown"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, or <paramref name="count"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public AgentExternalConversationList List(string workingDirectory, Func<string, bool> isKnown, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(isKnown);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var listed = new List<ClaudeCodeTranscriptHeader>();
        foreach (var file in FindTranscripts(workingDirectory).Take(MaxTranscriptsScanned))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadHeader(file) is { } header && IsImportable(header, workingDirectory) && !isKnown(header.SessionId))
            {
                listed.Add(header);
            }
        }

        var conversations = listed.Skip(offset).Take(count).Select(ToConversation).ToArray();
        return new AgentExternalConversationList(conversations, listed.Count);
    }

    /// <summary>
    /// Describes a conversation started outside CodeAlta, as the listing gives it for the folder: the command and the
    /// address that open it, and the program that holds it now.
    /// </summary>
    /// <param name="workingDirectory">The folder of the project.</param>
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <returns>The conversation, or null when the listing does not give it for that folder.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public AgentSharedConversation? Describe(string workingDirectory, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!ClaudeCodeConversationLinks.IsConversationId(conversationId))
        {
            return null;
        }

        var file = FindTranscripts(workingDirectory)
            .FirstOrDefault(candidate => string.Equals(candidate.SessionId, conversationId, StringComparison.OrdinalIgnoreCase));
        if (file is null || ReadHeader(file) is not { } header || !IsImportable(header, workingDirectory))
        {
            return null;
        }

        // The identifier as the transcript is named: the CLI resumes it by that name.
        var id = file.SessionId;
        return new AgentSharedConversation(
            id,
            ClaudeCodeConversationLinks.ResumeCommand(id),
            workingDirectory,
            ClaudeCodeConversationLinks.VsCodeUri(id),
            FindHolder(id) is { } holder ? ClaudeCodeSession.ToAgentHolder(holder) : null);
    }

    /// <summary>Reads a whole conversation started outside CodeAlta, as the listing gives it for the folder.</summary>
    /// <param name="workingDirectory">The folder of the project.</param>
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation, or null when the listing does not give it for that folder.</returns>
    /// <remarks>
    /// The provider state after the turns resumes the conversation of the CLI at its current leaf, counts exactly the
    /// messages written, follows the transcript from where this read stopped, takes the cost of the conversation from
    /// its last <c>cost-state</c> (none when it has none: the first turn of CodeAlta is then not given a cost rather than
    /// the cost of the whole conversation), and says the conversation was never given the instructions of CodeAlta.
    /// </remarks>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public AgentExternalConversationImport? Read(string workingDirectory, string conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!Guid.TryParseExact(conversationId, "D", out _) || conversationId.Length != 36)
        {
            return null;
        }

        var file = FindTranscripts(workingDirectory)
            .FirstOrDefault(candidate => string.Equals(candidate.SessionId, conversationId, StringComparison.OrdinalIgnoreCase));
        if (file is null || ClaudeCodeTranscriptReader.ReadHeader(file.Path) is not { } header || !IsImportable(header, workingDirectory))
        {
            return null;
        }

        return ReadWhole(file, header, cancellationToken);
    }

    /// <summary>
    /// Reads a whole conversation started outside CodeAlta to show it, as <see cref="Read"/> reads it, for a transcript of
    /// at most <paramref name="maxBytes"/>. The last <see cref="MaxCachedPreviews"/> reads are kept while their transcript
    /// keeps its length and its last write time.
    /// </summary>
    /// <param name="workingDirectory">The folder of the project.</param>
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <param name="maxBytes">The largest transcript read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation and the revision of its transcript, or why there is none to show.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public AgentExternalConversationPreview Preview(string workingDirectory, string conversationId, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!ClaudeCodeConversationLinks.IsConversationId(conversationId))
        {
            return AgentExternalConversationPreview.NotFound;
        }

        var file = FindTranscripts(workingDirectory)
            .FirstOrDefault(candidate => string.Equals(candidate.SessionId, conversationId, StringComparison.OrdinalIgnoreCase));
        if (file is null || ReadHeader(file) is not { } header || !IsImportable(header, workingDirectory))
        {
            return AgentExternalConversationPreview.NotFound;
        }

        if (file.Length > maxBytes)
        {
            return new(AgentExternalConversationPreviewStatus.TooLarge, null, file.Length, file.LastWriteTime);
        }

        lock (_gate)
        {
            var kept = _previews.FindIndex(preview => PathComparer.Equals(preview.Path, file.Path));
            if (kept >= 0 && _previews[kept].Length == file.Length && _previews[kept].LastWriteTime == file.LastWriteTime)
            {
                // The one read last is the one let go last.
                var hit = _previews[kept];
                _previews.RemoveAt(kept);
                _previews.Add(hit);
                return hit.Preview;
            }
        }

        var read = ReadWhole(file, header, cancellationToken);
        var preview = read is null
            ? AgentExternalConversationPreview.NotFound
            : new AgentExternalConversationPreview(AgentExternalConversationPreviewStatus.Read, read, file.Length, file.LastWriteTime);
        lock (_gate)
        {
            _previews.RemoveAll(cached => PathComparer.Equals(cached.Path, file.Path));
            _previews.Add(new CachedPreview(file.Path, file.Length, file.LastWriteTime, preview));
            if (_previews.Count > MaxCachedPreviews)
            {
                _previews.RemoveAt(0);
            }
        }

        return preview;
    }

    private AgentExternalConversationImport? ReadWhole(TranscriptFile file, ClaudeCodeTranscriptHeader header, CancellationToken cancellationToken)
    {
        var result = ClaudeCodeTranscriptReader.Read(file.Path, cancellationToken);
        if (result.Status != ClaudeCodeTranscriptReadStatus.Read || result.Leaf is null)
        {
            return null;
        }

        var turns = new List<AgentExternalTurn>(result.Turns.Count);
        var entries = new List<string>(result.Turns.Count);
        foreach (var turn in result.Turns)
        {
            if (turn.Compaction is null && turn.Messages.Count == 0 && turn.TriggerText is null)
            {
                continue;
            }

            turns.Add(new AgentExternalTurn(
                turn.Messages,
                ClaudeCodeExternalTurns.ToOrigin(turn.Origin),
                turn.Entrypoint,
                turn.Timestamp,
                IsCompaction: turn.Compaction is not null,
                turn.TriggerText));
            entries.Add(turn.LastUuid);
        }

        if (turns.All(static turn => turn.IsCompaction))
        {
            return null;
        }

        var cliSessionId = result.SessionId;
        var read = new AgentExternalTurns(
            AgentExternalTurnsStatus.Read,
            turns,
            (written, users, assistants) =>
            {
                ArgumentOutOfRangeException.ThrowIfNegative(written);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(written, turns.Count);
                // Between two turns: the point a fork or a rewind of the session at the next one resumes at.
                var state = written < turns.Count
                    ? new ClaudeCodeProviderState(cliSessionId, users, assistants, Leaf: written == 0 ? null : entries[written - 1],
                        Anchor: written == 0 ? null : entries[written - 1], Imported: true)
                    : new ClaudeCodeProviderState(cliSessionId, users, assistants, Instructions: null, Cost: result.TotalCostUsd,
                        Leaf: result.Leaf, Position: result.Position, Anchor: result.Leaf, Imported: true);
                return state.ToJson();
            });
        return new AgentExternalConversationImport(ToConversation(header), read);
    }

    // The transcripts of the conversations started in the folder, the most recently written first, one per session: the
    // project folders of the CLI whose name is the one of the folder (whatever its case on Windows, where the CLI may
    // name it from a path of another case), and which can be the same folder under two names.
    private List<TranscriptFile> FindTranscripts(string workingDirectory)
    {
        var projects = ClaudeCodeProjectFolders.GetProjectsFolder(_configRoot);
        var name = ClaudeCodeProjectFolders.GetFolderName(workingDirectory);
        var found = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(projects))
            {
                return [];
            }

            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                if (!ClaudeCodeProjectFolders.AreSameFolderName(Path.GetFileName(folder), name))
                {
                    continue;
                }

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + TranscriptExtension, SearchOption.TopDirectoryOnly))
                {
                    var sessionId = Path.GetFileNameWithoutExtension(file.Name);
                    if (sessionId.Length != 36 || !Guid.TryParseExact(sessionId, "D", out _) ||
                        !string.Equals(file.Extension, TranscriptExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidate = new TranscriptFile(file.FullName, sessionId, file.Length, file.LastWriteTimeUtc);
                    if (!found.TryGetValue(sessionId, out var other) || other.LastWriteTime < candidate.LastWriteTime)
                    {
                        found[sessionId] = candidate;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A folder that cannot be read lists what was found before it.
        }

        return [.. found.Values.OrderByDescending(static file => file.LastWriteTime).ThenBy(static file => file.SessionId, StringComparer.Ordinal)];
    }

    private ClaudeCodeTranscriptHeader? ReadHeader(TranscriptFile file)
    {
        if (file.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_headers.TryGetValue(file.Path, out var cached) && cached.Length == file.Length && cached.LastWriteTime == file.LastWriteTime)
            {
                return cached.Header;
            }
        }

        var header = ClaudeCodeTranscriptReader.ReadHeader(file.Path);
        lock (_gate)
        {
            if (_headers.Count >= MaxCachedHeaders)
            {
                _headers.Clear();
            }

            _headers[file.Path] = new CachedHeader(file.Length, file.LastWriteTime, header);
        }

        return header;
    }

    // What VS Code lists, started in that very folder: a transcript of a folder whose name is only alike (the name is
    // made of the letters and digits of the path) is not the one of the project.
    private static bool IsImportable(ClaudeCodeTranscriptHeader header, string workingDirectory)
        => header.IsListedByVsCode && header.Cwd is { } cwd && IsSameFolder(cwd, workingDirectory);

    private static bool IsSameFolder(string left, string right)
        => PathComparer.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right));

    private AgentExternalConversation ToConversation(ClaudeCodeTranscriptHeader header)
    {
        var holder = FindHolder(header.SessionId);
        return new AgentExternalConversation(
            header.SessionId,
            header.Title ?? header.SessionId,
            new DateTimeOffset(DateTime.SpecifyKind(header.LastWriteTime, DateTimeKind.Utc)),
            ClaudeCodeExternalTurns.ToOrigin(header.Origin),
            header.Entrypoint,
            holder is not null,
            holder?.Status);
    }

    // The live process that holds a conversation now; null when none does, or when the registry cannot be read.
    private ClaudeCodeSessionHolder? FindHolder(string conversationId)
    {
        if (_liveSessions is null)
        {
            return null;
        }

        try
        {
            return _liveSessions.FindHolder(conversationId) is { Status: ClaudeCodeSessionHoldStatus.Held, Holder: { } holder } ? holder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record TranscriptFile(string Path, string SessionId, long Length, DateTime LastWriteTime);

    private sealed record CachedHeader(long Length, DateTime LastWriteTime, ClaudeCodeTranscriptHeader? Header);

    private sealed record CachedPreview(string Path, long Length, DateTime LastWriteTime, AgentExternalConversationPreview Preview);
}
