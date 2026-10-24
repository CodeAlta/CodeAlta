using System.Buffers;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Finds the conversations of Claude Code started in VS Code or by the CLI in a terminal, in every folder of the profile,
/// which no session of CodeAlta continues, reads one whole to import it, and describes one to open it in VS Code or a
/// terminal. The transcripts are only read.
/// </summary>
/// <remarks>
/// <para>
/// A listing looks at every project folder of the profile: the CLI files a conversation under the folder it started in,
/// and moves it to the folder of a worktree it enters, so the folder of a project does not hold all of its conversations.
/// Which project lists a conversation is decided by the caller, from the folders the conversation started and ended in.
/// One transcript is kept per session, the most recently written: the same file seen through two names of one folder
/// (a link) is one conversation.
/// </para>
/// <para>
/// A listing reads the first and the last 64 KB of each transcript only, as the session list of VS Code does, and lists
/// what that list shows: not the sessions programs started (CodeAlta's own included), daemons, sidechains, sessions
/// continued in another transcript, nor those without a prompt. What was read of a file is kept while its length and its
/// last write time stay the same, so that a listing reads again only what changed.
/// </para>
/// <para>
/// With a store of the headers (<see cref="IAgentExternalConversationHeaderStore"/>, the application database), what was
/// read is kept across the starts of CodeAlta too: memory is the first level, the store the second, which a listing
/// (<see cref="ListAsync"/>) reads once and writes in the background, in one write, after it lists. A transcript is
/// read again when its length or its last write time changed, when the title VS Code keeps beside it changed, when it
/// continues in another transcript, or when <see cref="HeaderFormatVersion"/> changed; the rows of the transcripts a
/// listing no longer finds are removed.
/// </para>
/// </remarks>
internal sealed class ClaudeCodeExternalConversations
{
    // The most transcripts of the profile looked at, the most recently written first.
    internal const int MaxTranscriptsScanned = 5000;

    // More than the transcripts looked at: a listing never lets go of what it read itself.
    private const int MaxCachedHeaders = 8192;
    // The conversations read whole to be shown, kept while their transcript does not change: the one shown, and the one before.
    internal const int MaxCachedPreviews = 2;
    private const string TranscriptExtension = ".jsonl";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Logger StoreLogger = LogManager.GetLogger("CodeAlta.ClaudeCode");

    /// <summary>The name the rows of the provider are kept under in the store of the headers.</summary>
    internal const string HeaderStoreProvider = "claude-code";

    /// <summary>
    /// The version of the headers kept in the store: raised when <see cref="ClaudeCodeTranscriptHeader"/> or the rules
    /// of <see cref="ClaudeCodeTranscriptReader.ReadHeader(string)"/> change, so that the transcripts are read again.
    /// </summary>
    internal const int HeaderFormatVersion = 1;

    private readonly string _configRoot;
    private readonly ClaudeCodeLiveSessions? _liveSessions;
    private readonly IAgentExternalConversationHeaderStore? _headerStore;
    private readonly int _headerFormatVersion;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedHeader> _headers = new(PathComparer);
    private readonly List<CachedPreview> _previews = [];
    // The paths of the transcripts of this profile the store has a row of, as written; null until the store was read.
    private HashSet<string>? _stored;
    private readonly SemaphoreSlim _storeLoadGate = new(initialCount: 1, maxCount: 1);
    // Whether a write to the store runs, and the last one started.
    private bool _storeWriting;
    private Task? _pendingStoreWrite;

    /// <summary>Initializes a new instance of the <see cref="ClaudeCodeExternalConversations"/> class.</summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="liveSessions">The registry of live sessions, which tells whether a conversation is open elsewhere; null to tell none is.</param>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> is empty.</exception>
    public ClaudeCodeExternalConversations(string configRoot, ClaudeCodeLiveSessions? liveSessions)
        : this(configRoot, liveSessions, headerStore: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ClaudeCodeExternalConversations"/> class.</summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="liveSessions">The registry of live sessions, which tells whether a conversation is open elsewhere; null to tell none is.</param>
    /// <param name="headerStore">Where the headers read are kept across the starts of CodeAlta; null to keep them in memory only.</param>
    /// <param name="headerFormatVersion">The version of the format of the headers kept; tests change it.</param>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> is empty.</exception>
    internal ClaudeCodeExternalConversations(string configRoot, ClaudeCodeLiveSessions? liveSessions,
        IAgentExternalConversationHeaderStore? headerStore, int headerFormatVersion = HeaderFormatVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        _configRoot = configRoot;
        _liveSessions = liveSessions;
        _headerStore = headerStore;
        _headerFormatVersion = headerFormatVersion;
    }

    /// <summary>
    /// Lists the conversations started outside CodeAlta in every folder of the profile, as <see cref="List"/> does, with
    /// the headers kept by the store: only the transcripts whose length or last write time changed since they were read
    /// are read, and what was read is written to the store once the listing is given.
    /// </summary>
    /// <param name="isKnown">Whether a session of CodeAlta continues a conversation already: such a one is not listed.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations, as <see cref="List"/> gives them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="isKnown"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public async Task<IReadOnlyList<AgentExternalConversation>> ListAsync(Func<string, bool> isKnown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isKnown);
        await LoadStoredHeadersAsync(cancellationToken).ConfigureAwait(false);
        var files = FindTranscripts().Take(MaxTranscriptsScanned).ToList();
        var listed = List(files, isKnown, cancellationToken);
        StartStoreWrite(files);
        return listed;
    }

    /// <summary>The write to the store a listing started last, for tests: it ends when the store has what the listing read.</summary>
    internal Task PendingStoreWrite => Volatile.Read(ref _pendingStoreWrite) ?? Task.CompletedTask;

    /// <summary>Lists the conversations started outside CodeAlta in every folder of the profile, the most recently written first.</summary>
    /// <param name="isKnown">Whether a session of CodeAlta continues a conversation already: such a one is not listed.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>
    /// The conversations, each with the folders it started and ended in, among the <see cref="MaxTranscriptsScanned"/> most
    /// recently written transcripts. Whether another program holds them is not looked up: see <see cref="WithHolders"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="isKnown"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public IReadOnlyList<AgentExternalConversation> List(Func<string, bool> isKnown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isKnown);
        return List(FindTranscripts().Take(MaxTranscriptsScanned), isKnown, cancellationToken);
    }

    private List<AgentExternalConversation> List(IEnumerable<TranscriptFile> files, Func<string, bool> isKnown, CancellationToken cancellationToken)
    {
        var listed = new List<AgentExternalConversation>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadHeader(file) is { } header && IsImportable(header) && !isKnown(header.SessionId))
            {
                listed.Add(ToConversation(header, null));
            }
        }

        return listed;
    }

    /// <summary>Tells which of the conversations a live program holds now, and what it says it does.</summary>
    /// <param name="conversations">Conversations of a listing: a page of them, since the registry is read for each.</param>
    /// <returns>The conversations, in the same order, with what holds them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversations"/> is null.</exception>
    public IReadOnlyList<AgentExternalConversation> WithHolders(IReadOnlyList<AgentExternalConversation> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return [.. conversations.Select(conversation => FindHolder(conversation.ConversationId) is { } holder
            ? conversation with { IsHeld = true, HolderStatus = holder.Status, HolderOrigin = ClaudeCodeSession.ToAgentHolder(holder).Origin }
            : conversation with { IsHeld = false, HolderStatus = null, HolderOrigin = null })];
    }

    /// <summary>
    /// Describes a conversation started outside CodeAlta, as the listing gives it: the command and the address that open
    /// it, and the program that holds it now.
    /// </summary>
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <param name="workingDirectory">
    /// The folder the command is run in. The CLI resumes the conversation from any folder while one transcript of it
    /// exists in the profile.
    /// </param>
    /// <returns>The conversation, or null when the listing does not give it.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public AgentSharedConversation? Describe(string conversationId, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!ClaudeCodeConversationLinks.IsConversationId(conversationId) ||
            FindTranscript(conversationId) is not { } file || ReadHeader(file) is not { } header || !IsImportable(header))
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

    /// <summary>Reads a whole conversation started outside CodeAlta, as the listing gives it.</summary>
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation, or null when the listing does not give it.</returns>
    /// <remarks>
    /// The provider state after the turns resumes the conversation of the CLI at its current leaf, counts exactly the
    /// messages written, follows the transcript from where this read stopped, takes the cost of the conversation from
    /// its last <c>cost-state</c> (none when it has none: the first turn of CodeAlta is then not given a cost rather than
    /// the cost of the whole conversation), and says the conversation was never given the instructions of CodeAlta.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public AgentExternalConversationImport? Read(string conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!ClaudeCodeConversationLinks.IsConversationId(conversationId) ||
            FindTranscript(conversationId) is not { } file ||
            ClaudeCodeTranscriptReader.ReadHeader(file.Path) is not { } header || !IsImportable(header))
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
    /// <param name="conversationId">The identifier of the conversation (the session of the CLI).</param>
    /// <param name="maxBytes">The largest transcript read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation and the revision of its transcript, or why there is none to show.</returns>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public AgentExternalConversationPreview Preview(string conversationId, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!ClaudeCodeConversationLinks.IsConversationId(conversationId) ||
            FindTranscript(conversationId) is not { } file || ReadHeader(file) is not { } header || !IsImportable(header))
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
        return new AgentExternalConversationImport(ToConversation(header, FindHolder(header.SessionId)), read);
    }

    // The transcripts of every project folder of the profile, the most recently written first, one per session: the same
    // session in two folders (a copy left by a move, or one folder under two names) is its most recent file.
    private List<TranscriptFile> FindTranscripts()
    {
        var found = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in EnumerateProjectFolders())
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + TranscriptExtension, SearchOption.TopDirectoryOnly))
                {
                    Keep(found, file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that cannot be read lists nothing; the others are listed.
            }
        }

        return [.. found.Values.OrderByDescending(static file => file.LastWriteTime).ThenBy(static file => file.SessionId, StringComparer.Ordinal)];
    }

    // The transcript of one session: its most recent file in the project folders of the profile.
    private TranscriptFile? FindTranscript(string conversationId)
    {
        var found = new Dictionary<string, TranscriptFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in EnumerateProjectFolders())
        {
            try
            {
                // Enumerated rather than tested: the file is given with its name as written, whatever the case asked, on a
                // file system that tells cases apart too (Linux), where the default match does.
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles(conversationId + TranscriptExtension, new EnumerationOptions
                {
                    MatchCasing = MatchCasing.CaseInsensitive,
                    MatchType = MatchType.Win32,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                }))
                {
                    Keep(found, file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return found.Values.FirstOrDefault();
    }

    private string[] EnumerateProjectFolders()
    {
        var projects = ClaudeCodeProjectFolders.GetProjectsFolder(_configRoot);
        try
        {
            return Directory.Exists(projects) ? Directory.GetDirectories(projects) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static void Keep(Dictionary<string, TranscriptFile> found, FileInfo file)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        if (sessionId.Length != 36 || !Guid.TryParseExact(sessionId, "D", out _) ||
            !string.Equals(file.Extension, TranscriptExtension, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var candidate = new TranscriptFile(file.FullName, sessionId, file.Length, file.LastWriteTimeUtc);
        if (!found.TryGetValue(sessionId, out var other) || other.LastWriteTime < candidate.LastWriteTime)
        {
            found[sessionId] = candidate;
        }
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

        // Looked at before the transcript is read: a title given meanwhile is read again by a later listing.
        var titleFileTicks = ReadTitleFileTicks(file.Path, file.SessionId);
        var header = ClaudeCodeTranscriptReader.ReadHeader(file.Path, out var unreadable);
        if (unreadable)
        {
            // Another program holds it, or it went: it is read again next time.
            return null;
        }

        lock (_gate)
        {
            if (_headers.Count >= MaxCachedHeaders)
            {
                _headers.Clear();
            }

            _headers[file.Path] = new CachedHeader(file.Length, file.LastWriteTime, header, titleFileTicks, Stored: false);
        }

        return header;
    }

    // Reads the rows the store keeps of the transcripts of this profile, once: the headers of the current format are
    // taken as read while their transcript keeps the length and the last write time they were read at. A store that
    // cannot be read now is read again by the next listing; meanwhile the transcripts are read.
    private async Task LoadStoredHeadersAsync(CancellationToken cancellationToken)
    {
        if (_headerStore is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_stored is not null)
            {
                return;
            }
        }

        await _storeLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_stored is not null)
                {
                    return;
                }
            }

            if (await _headerStore.ReadAsync(HeaderStoreProvider, cancellationToken).ConfigureAwait(false) is not { } rows)
            {
                return;
            }

            // Several configuration folders (several accounts) share the rows of the provider: each takes its own only.
            var projects = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClaudeCodeProjectFolders.GetProjectsFolder(_configRoot))) + Path.DirectorySeparatorChar;
            var stored = new HashSet<string>(StringComparer.Ordinal);
            var headers = new List<(string Path, CachedHeader Header)>();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!row.Path.StartsWith(projects, PathComparison))
                {
                    continue;
                }

                stored.Add(row.Path);
                if (row.FormatVersion == _headerFormatVersion && row.HeaderJson is { } json &&
                    ParseStoredHeader(json) is { } header &&
                    // What a transcript continued in is a transcript of its own, which may have come since.
                    header.Header.ContinuedIn is null &&
                    header.TitleFileTicks == ReadTitleFileTicks(row.Path, header.Header.SessionId))
                {
                    headers.Add((row.Path, new CachedHeader(row.Length, new DateTime(row.LastWriteUtcTicks, DateTimeKind.Utc), header.Header, header.TitleFileTicks, Stored: true)));
                }
            }

            lock (_gate)
            {
                foreach (var (path, header) in headers)
                {
                    // What this instance read itself is newer.
                    _headers.TryAdd(path, header);
                }

                _stored = stored;
            }
        }
        finally
        {
            _storeLoadGate.Release();
        }
    }

    // Writes to the store, in the background and in one write, the headers of the listed transcripts it does not have,
    // and removes the rows of the transcripts the listing no longer found. A write that is not made (the database is
    // locked, or closed with the application) is made by a later listing: the headers stay in memory meanwhile.
    private void StartStoreWrite(IReadOnlyList<TranscriptFile> files)
    {
        if (_headerStore is null)
        {
            return;
        }

        var upserts = new List<(string Path, CachedHeader Header)>();
        List<string> removed;
        lock (_gate)
        {
            if (_stored is null || _storeWriting)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                seen.Add(file.Path);
                if (_headers.TryGetValue(file.Path, out var cached) && !cached.Stored && cached.Header is not null &&
                    cached.Length == file.Length && cached.LastWriteTime == file.LastWriteTime)
                {
                    upserts.Add((file.Path, cached));
                }
            }

            removed = [.. _stored.Where(path => !seen.Contains(path))];
            if (upserts.Count == 0 && removed.Count == 0)
            {
                return;
            }

            _storeWriting = true;
        }

        Volatile.Write(ref _pendingStoreWrite, Task.Run(() => WriteStoreAsync(upserts, removed)));
    }

    private async Task WriteStoreAsync(List<(string Path, CachedHeader Header)> upserts, List<string> removed)
    {
        try
        {
            var records = upserts.ConvertAll(upsert => new AgentExternalConversationHeaderRecord(
                upsert.Path,
                upsert.Header.Length,
                upsert.Header.LastWriteTime.Ticks,
                _headerFormatVersion,
                IsImportable(upsert.Header.Header!),
                FormatStoredHeader(upsert.Header.Header!, upsert.Header.TitleFileTicks)));
            if (!await _headerStore!.WriteAsync(HeaderStoreProvider, records, removed, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }

            lock (_gate)
            {
                foreach (var (path, header) in upserts)
                {
                    _stored!.Add(path);
                    if (_headers.TryGetValue(path, out var cached) && ReferenceEquals(cached, header))
                    {
                        _headers[path] = cached with { Stored = true };
                    }
                }

                _stored!.ExceptWith(removed);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The rows are derived from the transcripts: what is not written is read again from them.
            StoreLogger.Warn(exception, "Could not keep the headers of the Claude Code transcripts in the application database.");
        }
        finally
        {
            lock (_gate)
            {
                _storeWriting = false;
            }
        }
    }

    // When the title VS Code keeps beside a transcript was last written, in UTC ticks; 0 when it has none.
    private static long ReadTitleFileTicks(string transcriptPath, string sessionId)
    {
        try
        {
            var title = new FileInfo(Path.Combine(Path.GetDirectoryName(transcriptPath) ?? string.Empty, sessionId, "custom-title.json"));
            return title.Exists ? title.LastWriteTimeUtc.Ticks : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    // The header as the store keeps it (format HeaderFormatVersion), with when the title beside the transcript was written.
    private static string FormatStoredHeader(ClaudeCodeTranscriptHeader header, long titleFileTicks)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", header.SessionId);
            writer.WriteString("cwd", header.Cwd);
            writer.WriteString("entrypoint", header.Entrypoint);
            writer.WriteString("origin", header.Origin.ToString());
            writer.WriteString("sessionKind", header.SessionKind);
            writer.WriteBoolean("isSidechain", header.IsSidechain);
            writer.WriteString("continuedIn", header.ContinuedIn);
            writer.WriteBoolean("continuedInExists", header.ContinuedInExists);
            writer.WriteString("title", header.Title);
            writer.WriteNumber("lastWriteUtcTicks", header.LastWriteTime.Ticks);
            writer.WriteNumber("length", header.Length);
            writer.WriteString("lastCwd", header.LastCwd);
            writer.WriteNumber("titleFileTicks", titleFileTicks);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The header a row keeps; null when it does not read as one, and the transcript is read again.
    private static (ClaudeCodeTranscriptHeader Header, long TitleFileTicks)? ParseStoredHeader(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "sessionId") is not { } sessionId ||
                !Enum.TryParse<ClaudeCodeTranscriptOrigin>(Text(root, "origin"), out var origin) ||
                !root.TryGetProperty("lastWriteUtcTicks", out var ticks) || !ticks.TryGetInt64(out var lastWriteTicks) ||
                !root.TryGetProperty("length", out var lengthValue) || !lengthValue.TryGetInt64(out var length) ||
                !root.TryGetProperty("titleFileTicks", out var titleValue) || !titleValue.TryGetInt64(out var titleFileTicks))
            {
                return null;
            }

            return (new ClaudeCodeTranscriptHeader(
                sessionId,
                Text(root, "cwd"),
                Text(root, "entrypoint"),
                origin,
                Text(root, "sessionKind"),
                Flag(root, "isSidechain"),
                Text(root, "continuedIn"),
                Flag(root, "continuedInExists"),
                Text(root, "title"),
                new DateTime(lastWriteTicks, DateTimeKind.Utc),
                length,
                Text(root, "lastCwd")), titleFileTicks);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static bool Flag(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    // What VS Code lists, with the folder it started in: the CLI writes it with the first entry.
    private static bool IsImportable(ClaudeCodeTranscriptHeader header) => header.IsListedByVsCode && header.Cwd is not null;

    private static AgentExternalConversation ToConversation(ClaudeCodeTranscriptHeader header, ClaudeCodeSessionHolder? holder)
    {
        var started = ToLocalDirectory(header.Cwd);
        return new AgentExternalConversation(
            header.SessionId,
            header.Title ?? header.SessionId,
            new DateTimeOffset(DateTime.SpecifyKind(header.LastWriteTime, DateTimeKind.Utc)),
            ClaudeCodeExternalTurns.ToOrigin(header.Origin),
            header.Entrypoint,
            holder is not null,
            holder?.Status,
            started,
            ToLocalDirectory(header.LastCwd) ?? started,
            holder is null ? null : ClaudeCodeSession.ToAgentHolder(holder).Origin);
    }

    /// <summary>
    /// Gives a <c>cwd</c> of a transcript as a path of this system: on Windows, the CLI run from Git Bash writes some of
    /// them as <c>/c/dev/project</c>, which is <c>C:\dev\project</c>. A path of another system (<c>/home/me</c> on
    /// Windows, a WSL folder) is given as written. The separator it ends with is removed.
    /// </summary>
    /// <param name="cwd">The <c>cwd</c> as written.</param>
    /// <returns>The folder, or null when none is written.</returns>
    internal static string? ToLocalDirectory(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && cwd.Length >= 2 && cwd[0] == '/' && char.IsAsciiLetter(cwd[1]) && (cwd.Length == 2 || cwd[2] == '/'))
        {
            cwd = $"{char.ToUpperInvariant(cwd[1])}:\\{cwd[Math.Min(3, cwd.Length)..].Replace('/', '\\')}";
        }

        return Path.TrimEndingDirectorySeparator(cwd);
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

    // Stored: the store has the header at this length and last write time.
    private sealed record CachedHeader(long Length, DateTime LastWriteTime, ClaudeCodeTranscriptHeader? Header, long TitleFileTicks, bool Stored);

    private sealed record CachedPreview(string Path, long Length, DateTime LastWriteTime, AgentExternalConversationPreview Preview);
}
