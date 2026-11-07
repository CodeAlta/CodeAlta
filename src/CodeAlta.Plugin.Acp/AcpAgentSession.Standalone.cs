using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// A turn that belongs to no session (a side question): an agent process and a session of the agent of its own, ended
// with the turn.
internal sealed partial class AcpAgentSession
{
    /// <summary>
    /// Gets whether the session runs one turn that belongs to no session (<see cref="ExecuteStandaloneTurnAsync"/>):
    /// every permission request of the agent is refused.
    /// </summary>
    public bool Standalone { get; init; }

    // The empty folder the agent of a standalone turn works in, deleted with the turn; null when it works in the folder of
    // the request (in a sandbox).
    private string? _standaloneFolder;

    /// <summary>
    /// Runs a turn that belongs to no session: the agent is started for it, without the tools of CodeAlta and without a
    /// file system or a terminal of the client, in a read-only mode when it offers one, and every permission it asks is
    /// refused. Its session is closed and deleted when the agent can, and its process ended, also when the turn fails or
    /// is cancelled.
    /// </summary>
    /// <remarks>
    /// The agent works in an empty folder of its own, deleted with the turn: the question needs no file (its conversation
    /// is the whole context), the tools the agent runs without asking (reading a file, searching) find nothing of the
    /// project, and an agent that cannot delete a session keeps it in its history under that folder, not the project's.
    /// In a sandbox, whose profile names the folders it allows, it works in the folder of the request.
    /// </remarks>
    public async Task<AgentTurnResponse> ExecuteStandaloneTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Standalone)
        {
            throw new InvalidOperationException("The session runs the turns of a CodeAlta session.");
        }

        _providerId = request.ProviderId;
        try
        {
            var connection = await StartStandaloneAsync(request, cancellationToken).ConfigureAwait(false);
            await ApplyModelAsync(request, cancellationToken).ConfigureAwait(false);
            // The system message is appended to the system prompt of an agent that takes one, else it opens the prompt.
            IReadOnlyList<string> preambles = connection.Capabilities.SystemPrompt || string.IsNullOrWhiteSpace(request.SystemMessage) ? [] : [request.SystemMessage];
            SendPrompt(connection, AcpPrompts.CreatePrompt(preambles, [.. request.Conversation.Where(static message => message.Role is AgentConversationRole.User)], connection.Capabilities.Images));

            // The answer is the text of the whole prompt: the tool calls the agent made on its own are left out.
            var parts = new List<AgentMessagePart>();
            var contentIds = new List<string?>();
            AgentTurnResponse segment;
            do
            {
                segment = await ReadSegmentAsync(onUpdate, static (_, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < segment.AssistantMessage.Parts.Count; index++)
                {
                    if (segment.AssistantMessage.Parts[index] is not AgentMessagePart.ToolCall)
                    {
                        parts.Add(segment.AssistantMessage.Parts[index]);
                        contentIds.Add(segment.AssistantPartContentIds?[index]);
                    }
                }
            }
            while (connection.PromptActive);

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. parts]),
                AssistantPartContentIds = contentIds.Exists(static id => id is not null) ? [.. contentIds] : null,
                Usage = segment.Usage,
            };
        }
        finally
        {
            await EndStandaloneAsync().ConfigureAwait(false);
        }
    }

    // Starts the agent and a session of it with no MCP server, then puts it in a read-only mode.
    private async Task<Connection> StartStandaloneAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        var workingDirectory = WorkingDirectoryOf(request);
        if (request.ProcessHost is null)
        {
            _standaloneFolder = Path.Combine(Path.GetTempPath(), "codealta-side-questions", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(_standaloneFolder);
            workingDirectory = _standaloneFolder;
        }

        var connection = await StartAgentAsync(
                new AcpConnectionRequest(executable, _options.Agent.Arguments, CreateEnvironment(request), workingDirectory, _options.InitializeTimeout)
                {
                    Host = request.ProcessHost,
                    Needs = request.ProcessHost is null ? AcpAgentNeeds.None : _options.Quirks.SandboxNeeds(_options.Agent, executable, _options.ProviderKey),
                },
                launchKey: string.Empty,
                cancellationToken)
            .ConfigureAwait(false);

        NewSessionResponse created;
        try
        {
            created = await connection.Client.SessionNewAsync(
                    new NewSessionRequest
                    {
                        Cwd = workingDirectory,
                        McpServers = [],
                        SystemPrompt = connection.Capabilities.SystemPrompt && !string.IsNullOrWhiteSpace(request.SystemMessage) ? CreateSystemPrompt(request.SystemMessage) : null,
                    },
                    Timeout(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (AcpJsonRpcException ex)
        {
            throw MapFailure(ex, connection);
        }

        connection.AgentSessionId = created.SessionId.Value;
        SetConfigOptions(connection, Serialize(created.ConfigOptions));
        await ChooseReadOnlyModeAsync(connection, created.Modes, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    // The mode of the session, as a config option of the category `mode` or as the modes of the session.
    private async Task ChooseReadOnlyModeAsync(Connection connection, SessionModeState? modes, CancellationToken cancellationToken)
    {
        if (AcpConfigOptions.Find(connection.ConfigOptions, "mode") is { } option)
        {
            if (ChooseReadOnlyMode(AcpConfigOptions.Values(option).Select(static value => (value.Value, value.Name))) is { } value &&
                !string.Equals(AcpJson.GetString(option, "currentValue"), value, StringComparison.Ordinal))
            {
                await SetOptionAsync(connection, AcpJson.GetString(option, "id") ?? "mode", value, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (modes is null ||
            ChooseReadOnlyMode(modes.AvailableModes.Select(static mode => (mode.Id.Value, (string?)mode.Name))) is not { } modeId ||
            string.Equals(modes.CurrentModeId.Value, modeId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await connection.Client.SessionSetModeAsync(
                    new SetSessionModeRequest { SessionId = new SessionId { Value = connection.AgentSessionId }, ModeId = modeId },
                    Timeout(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (AcpJsonRpcException ex)
        {
            // The turn goes on in the mode the agent has: its permission requests are refused all the same.
            _logger?.Info($"{_options.AgentName} refused the mode {modeId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Chooses the mode of an agent that changes the least: a read-only one, else one that only answers (<c>ask</c>),
    /// else one that only plans (<c>plan</c>), by its id or its name; <see langword="null"/> when none is.
    /// </summary>
    /// <param name="modes">The modes the agent offers: their id and their name.</param>
    public static string? ChooseReadOnlyMode(IEnumerable<(string Id, string? Name)> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        string? best = null;
        var bestRank = int.MaxValue;
        foreach (var (id, name) in modes)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rank = Math.Min(Rank(id), name is null ? int.MaxValue : Rank(name));
            if (rank < bestRank)
            {
                best = id;
                bestRank = rank;
            }
        }

        return best;

        static int Rank(string text)
        {
            // "read-only", "Read Only", "readonly": their letters alone.
            var letters = string.Concat(text.Where(char.IsLetter)).ToLowerInvariant();
            if (letters.Contains("readonly", StringComparison.Ordinal))
            {
                return 0;
            }

            return letters switch
            {
                "ask" => 1,
                "plan" => 2,
                _ => int.MaxValue,
            };
        }
    }

    // Stops the prompt when it still runs, closes and deletes the session of the agent when it can, then ends its
    // process: nothing of the turn goes on or is kept by CodeAlta. Each request has a short time of its own, as the
    // turn may have been cancelled.
    private async Task EndStandaloneAsync()
    {
        await InterruptAsync().ConfigureAwait(false);
        if (_connection is { AgentSessionId.Length: > 0 } connection && !connection.Client.HasExited)
        {
            var sessionId = new SessionId { Value = connection.AgentSessionId };
            if (connection.Capabilities.CloseSession)
            {
                await EndRequestAsync("session/close", token => connection.Client.SessionCloseAsync(new CloseSessionRequest { SessionId = sessionId }, token)).ConfigureAwait(false);
            }

            if (connection.Capabilities.DeleteSession)
            {
                await EndRequestAsync("session/delete", token => connection.Client.SessionDeleteAsync(new DeleteSessionRequest { SessionId = sessionId }, token)).ConfigureAwait(false);
            }
            else
            {
                _logger?.Debug($"{_options.AgentName} cannot delete a session: it keeps session {sessionId.Value} of a turn without a session in its own history.");
            }
        }

        await CloseConnectionAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _standaloneFolder, null) is { } folder)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What the agent left there goes with the temporary files of the system.
                _logger?.Debug($"The folder {folder} of a turn without a session could not be deleted: {ex.Message}");
            }
        }
    }

    private async Task EndRequestAsync(string method, Func<CancellationToken, Task> send)
    {
        using var timeout = new CancellationTokenSource(_options.CancelTimeout);
        try
        {
            await send(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AcpJsonRpcException or AcpConnectionClosedException or OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            _logger?.Info($"{_options.AgentName} did not answer {method}: {ex.Message}");
        }
    }
}
