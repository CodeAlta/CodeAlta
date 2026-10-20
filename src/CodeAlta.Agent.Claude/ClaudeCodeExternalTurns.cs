using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Reads the turns added to the transcript of a Claude Code conversation outside CodeAlta (in VS Code, or by the CLI in
/// a terminal) since the session recorded it: the turns after the entry its provider state names, from where the last
/// read stopped.
/// </summary>
internal static class ClaudeCodeExternalTurns
{
    private const string TranscriptExtension = ".jsonl";

    /// <summary>Reads the turns made outside CodeAlta.</summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="state">The state the session recorded last.</param>
    /// <param name="workingDirectory">The folder the session works in: the CLI keeps the transcript in the folder named after it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>What was found.</returns>
    /// <remarks>
    /// The transcript is looked for only where the CLI started in <paramref name="workingDirectory"/> looks for it with
    /// <c>--resume</c>: one it does not find there is not the conversation the next turn resumes.
    /// <para>
    /// The CLI may not write the result of a tool under the identifier it streamed, which the session records as its
    /// leaf when a run ended on a tool result. When nothing in the transcript names that leaf, the turns are followed
    /// from the last assistant entry the session recorded (<see cref="ClaudeCodeProviderState.Anchor"/>), which the CLI
    /// writes; the results of the calls of that entry, which the session holds already, are left out.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> or <paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public static AgentExternalTurns Read(string configRoot, AgentSessionState state, string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Read(configRoot, ClaudeCodeProviderState.Read(state), workingDirectory, cancellationToken);
    }

    /// <summary>Reads the turns made outside CodeAlta after the point a provider state names.</summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="state">The provider state, or null when the session has none of this provider.</param>
    /// <param name="workingDirectory">The folder the session works in.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> or <paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public static AgentExternalTurns Read(string configRoot, ClaudeCodeProviderState? state, string workingDirectory, CancellationToken cancellationToken)
        => Read(configRoot, state, workingDirectory, null, cancellationToken);

    /// <summary>
    /// Reads the turns made outside CodeAlta after the point a provider state names, from the latest position of the
    /// transcript <paramref name="positions"/> knows, and records there where this read stopped.
    /// </summary>
    /// <param name="configRoot">The configuration folder of the CLI (<c>~/.claude</c>).</param>
    /// <param name="state">The provider state, or null when the session has none of this provider.</param>
    /// <param name="workingDirectory">The folder the session works in.</param>
    /// <param name="positions">Where the reads of this process stopped, or null.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> or <paramref name="workingDirectory"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public static AgentExternalTurns Read(
        string configRoot,
        ClaudeCodeProviderState? state,
        string workingDirectory,
        ClaudeCodeTranscriptPositions? positions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // A state recorded before entries were followed has no point to follow from: what comes after it is not known.
        if (state is not { Leaf: { } leaf } persisted || !IsFileName(persisted.SessionId))
        {
            return AgentExternalTurns.None;
        }

        var path = Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(configRoot, workingDirectory), persisted.SessionId + TranscriptExtension);
        var position = positions?.Choose(persisted.SessionId, persisted.Position, leaf) ?? persisted.Position;
        var result = ClaudeCodeTranscriptReader.ReadAfter(path, leaf, position, cancellationToken);
        var fromAnchor = false;
        if (result.Status == ClaudeCodeTranscriptReadStatus.LeafNotFound &&
            persisted.Anchor is { } anchor && !string.Equals(anchor, leaf, StringComparison.Ordinal))
        {
            var anchored = ClaudeCodeTranscriptReader.ReadAfter(path, anchor, position, cancellationToken);
            if (anchored.Status is ClaudeCodeTranscriptReadStatus.Read or ClaudeCodeTranscriptReadStatus.Unchanged or ClaudeCodeTranscriptReadStatus.CannotFollow)
            {
                result = anchored;
                fromAnchor = true;
            }
        }

        positions?.Note(persisted.SessionId, result.Position);
        switch (result.Status)
        {
            case ClaudeCodeTranscriptReadStatus.Missing:
                return AgentExternalTurns.Missing;
            case ClaudeCodeTranscriptReadStatus.Unchanged when !fromAnchor:
                return AgentExternalTurns.Unchanged;
            case ClaudeCodeTranscriptReadStatus.CannotFollow or ClaudeCodeTranscriptReadStatus.LeafNotFound:
                // Nothing is appended: the conversation goes on from where the transcript is now, and what is written
                // after this point is followed from there.
                return new AgentExternalTurns(
                    AgentExternalTurnsStatus.CannotFollow,
                    [],
                    (_, _, _) => Follow(persisted, result, 0, 0).ToJson());
        }

        var turns = new List<AgentExternalTurn>(result.Turns.Count);
        var entries = new List<string>(result.Turns.Count);
        foreach (var turn in result.Turns)
        {
            IReadOnlyList<AgentConversationMessage> messages = turn.Messages;
            if (fromAnchor && turns.Count == 0 && turn.Compaction is null && messages.FirstOrDefault()?.Role is AgentConversationRole.Tool)
            {
                // The results of the calls of the anchor: the session recorded a result for each of them already.
                messages = [.. messages.SkipWhile(static message => message.Role is AgentConversationRole.Tool)];
            }

            if (turn.Compaction is null && messages.Count == 0 && turn.TriggerText is null)
            {
                continue;
            }

            turns.Add(new AgentExternalTurn(
                messages,
                ToOrigin(turn.Origin),
                turn.Entrypoint,
                turn.Timestamp,
                IsCompaction: turn.Compaction is not null,
                turn.TriggerText));
            entries.Add(turn.LastUuid);
        }

        return new AgentExternalTurns(
            AgentExternalTurnsStatus.Read,
            turns,
            (written, users, assistants) =>
            {
                ArgumentOutOfRangeException.ThrowIfNegative(written);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(written, turns.Count);
                if (written < turns.Count)
                {
                    // Between two turns: the point a fork or a rewind of the session at the next one resumes at. What
                    // the conversation cost is known only for the whole read.
                    return (persisted with
                    {
                        Users = persisted.Users + users,
                        Assistants = persisted.Assistants + assistants,
                        Leaf = written == 0 ? persisted.Leaf : entries[written - 1],
                        Anchor = written == 0 ? persisted.Anchor : entries[written - 1],
                    }).ToJson();
                }

                return Follow(persisted, result, users, assistants).ToJson();
            });
    }

    // The state after the whole read: the counts of what was written added, the leaf and the position of the read, and
    // the cost of the conversation as the transcript last wrote it. A read without a cost (the CLI often writes none)
    // keeps the cost recorded: a CLI that resumes the conversation counts from the last cost the transcript has.
    private static ClaudeCodeProviderState Follow(ClaudeCodeProviderState persisted, ClaudeCodeTranscriptReadResult result, int users, int assistants)
        => persisted with
        {
            Users = persisted.Users + users,
            Assistants = persisted.Assistants + assistants,
            Leaf = result.Leaf ?? persisted.Leaf,
            Anchor = result.Leaf ?? persisted.Anchor,
            Position = result.Position ?? persisted.Position,
            Cost = result.TotalCostUsd ?? persisted.Cost,
        };

    internal static AgentExternalTurnOrigin ToOrigin(ClaudeCodeTranscriptOrigin origin)
        => origin switch
        {
            ClaudeCodeTranscriptOrigin.VsCode => AgentExternalTurnOrigin.VsCode,
            ClaudeCodeTranscriptOrigin.Terminal => AgentExternalTurnOrigin.Terminal,
            ClaudeCodeTranscriptOrigin.Unknown => AgentExternalTurnOrigin.Unknown,
            _ => AgentExternalTurnOrigin.OtherApplication,
        };

    private static bool IsFileName(string cliSessionId)
        => !cliSessionId.Contains("..", StringComparison.Ordinal) &&
           cliSessionId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           cliSessionId.AsSpan().IndexOfAny('/', '\\') < 0;
}
