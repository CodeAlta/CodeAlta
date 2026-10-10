using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// Remote Control of Claude Code: the CLI connects the session to claude.ai, where it can be followed and driven
// from the browser or the Claude app. It is asked for with the `remote_control` control request, says how its
// bridge stands with `bridge_state` messages, and starts a turn by itself for a prompt sent from there. While it is
// on, the process is kept running: the bridge ends with it. A process started again (another model, effort or
// folder, a mode it could not switch to) is connected again with the same link.
internal sealed partial class ClaudeCodeSession
{
    // Taken before `_gate` by whatever changes the remote control, and held while those who listen are told.
    private readonly Lock _remoteControlNotice = new();
    private bool _remoteControlWanted;
    private string? _remoteControlName;
    private string? _bridgeSessionId;
    private bool _remoteControlAvailable = true;
    private AgentRemoteControl _remoteControl = AgentRemoteControl.Off;
    private Action<AgentRemoteControl>? _onRemoteControl;

    /// <summary>Gets the remote control of the session now.</summary>
    public AgentRemoteControl RemoteControl
    {
        get
        {
            lock (_gate)
            {
                return _remoteControl;
            }
        }
    }

    /// <summary>
    /// Registers what is called when the remote control of the session changes. The handler is called while the
    /// CLI is being read: it must not wait.
    /// </summary>
    public IDisposable OnRemoteControlChanged(Action<AgentRemoteControl> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _onRemoteControl = handler;
        }

        return new RemoteControlRegistration(this, handler);
    }

    private sealed class RemoteControlRegistration(ClaudeCodeSession session, Action<AgentRemoteControl> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (session._gate)
            {
                if (ReferenceEquals(session._onRemoteControl, handler))
                {
                    session._onRemoteControl = null;
                }
            }
        }
    }

    /// <summary>
    /// Turns Remote Control on or off. With no process running, one is started from the request, as for a
    /// compaction; while a turn starts one, the turn connects it.
    /// </summary>
    public async Task<AgentRemoteControl> SetRemoteControlAsync(AgentTurnRequest request, bool enabled, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            _remoteControlWanted = enabled;
            _remoteControlName = name ?? _remoteControlName;
            connection = _connection is { IsClosed: false } running ? running : null;
        }

        if (!enabled)
        {
            if (connection is not null)
            {
                await TurnOffBridgeAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _bridgeSessionId = null;
            }

            ChangeRemoteControl(AgentRemoteControl.Off);
            // Nothing keeps an idle process for Remote Control any longer.
            ArmIdleTimer();
            return RemoteControl;
        }

        if (connection is not null)
        {
            return await ConnectRemoteControlAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        // No process runs. A turn that holds the gate starts one, and connects it: the wish is all it needs.
        if (!await _turnGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Connecting));
            return RemoteControl;
        }

        try
        {
            CancelIdleTimer();
            _providerId = request.ProviderId;
            _ = BindConversation(request, hasNewPrompt: false);
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Connecting));
            // The process that starts is connected as it starts (StartConnectionCoreAsync).
            await EnsureConnectionAsync(request, cancellationToken).ConfigureAwait(false);
            return RemoteControl;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Failed, Error: ex.Message));
            return RemoteControl;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Given up before a process was up: nothing connects it before the next prompt sent here.
            UpdateRemoteControl(current => _remoteControlWanted && current.Status == AgentRemoteControlStatus.Connecting && _connection is not { IsClosed: false }
                ? new AgentRemoteControl(AgentRemoteControlStatus.Failed, current.SessionUrl,
                    "Claude Code was not started. Remote Control connects with the next prompt sent here, or with Try again.")
                : null);
            throw;
        }
        finally
        {
            ArmIdleTimer();
            _turnGate.Release();
        }
    }

    // Called once a process is up: one started while Remote Control is wanted is connected, with the link it had.
    private async Task ResumeRemoteControlAsync(ClaudeCodeConnection connection, CancellationToken cancellationToken)
    {
        bool wanted;
        lock (_gate)
        {
            wanted = _remoteControlWanted;
        }

        if (wanted)
        {
            try
            {
                await ConnectRemoteControlAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The process is ready: the request goes on without this wait, and its answer says how it stands.
            }
        }
    }

    // The caller can stop waiting, not the request: the CLI connects all the same, and its answer, with the link, is
    // what the state then says. A request given up would leave it connecting, with a link nobody read.
    private Task<AgentRemoteControl> ConnectRemoteControlAsync(ClaudeCodeConnection connection, CancellationToken cancellationToken)
        => ConnectRemoteControlCoreAsync(connection).WaitAsync(cancellationToken);

    private async Task<AgentRemoteControl> ConnectRemoteControlCoreAsync(ClaudeCodeConnection connection)
    {
        string? name;
        string? reattach;
        bool available;
        lock (_gate)
        {
            name = _remoteControlName;
            reattach = _bridgeSessionId;
            available = _remoteControlAvailable;
        }

        if (!available)
        {
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Failed,
                Error: "Claude Code says Remote Control is not available here. It needs a claude.ai login, not an API key."));
            return RemoteControl;
        }

        // Turned off meanwhile: nothing is asked of the CLI.
        UpdateRemoteControl(current => _remoteControlWanted ? new AgentRemoteControl(AgentRemoteControlStatus.Connecting, current.SessionUrl) : null);
        lock (_gate)
        {
            if (!_remoteControlWanted)
            {
                return _remoteControl;
            }
        }

        try
        {
            var response = await connection.RequestAsync(
                    "remote_control",
                    writer =>
                    {
                        writer.WriteBoolean("enabled", true);
                        if (name is not null)
                        {
                            writer.WriteString("name", name);
                        }

                        // A process started again keeps the link of the session.
                        if (reattach is not null)
                        {
                            writer.WriteString("reattach_session_id", reattach);
                        }
                    },
                    _options.ControlTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var url = ClaudeCodeJson.GetString(response, "session_url");
            bool wanted;
            lock (_gate)
            {
                wanted = _remoteControlWanted;
                if (wanted)
                {
                    _bridgeSessionId = ClaudeCodeJson.GetString(response, "bridge_session_id") ?? _bridgeSessionId;
                }
            }

            if (!wanted)
            {
                // Turned off while it was being connected: the off request went before this answer, and the bridge
                // it connected is turned off in its turn.
                await TurnOffBridgeAsync(connection, CancellationToken.None).ConfigureAwait(false);
                return RemoteControl;
            }

            UpdateRemoteControl(current => _remoteControlWanted ? new AgentRemoteControl(AgentRemoteControlStatus.Connected, url ?? current.SessionUrl) : null);
        }
        catch (ClaudeCodeControlException ex)
        {
            // Claude Code refuses it with an API key, inside a remote session, or in the cloud.
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Failed,
                Error: $"Claude Code could not start Remote Control: {ex.Message} It needs a claude.ai login, not an API key."));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            ChangeRemoteControl(new AgentRemoteControl(AgentRemoteControlStatus.Failed, Error: $"Claude Code did not start Remote Control: {ex.Message}"));
        }

        return RemoteControl;
    }

    private async Task TurnOffBridgeAsync(ClaudeCodeConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.RequestAsync("remote_control", writer => writer.WriteBoolean("enabled", false), _options.ControlTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ClaudeCodeControlException or TimeoutException or IOException)
        {
            // The bridge ends with the process anyway: the process is no longer kept for it.
        }
    }

    // Reads what `initialize` says of Remote Control: an older CLI says nothing, and is then assumed to have it.
    private void ReadRemoteControlAvailability(JsonElement initialized)
    {
        if (initialized.ValueKind == JsonValueKind.Object &&
            initialized.TryGetProperty("remote_control_available", out var available) &&
            available.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            lock (_gate)
            {
                _remoteControlAvailable = available.GetBoolean();
            }
        }
    }

    // `bridge_state` says how the bridge to claude.ai stands, with each change: `ready`, `connected`, and the
    // states of a bridge that lost its connection and tries again.
    private void ReadBridgeState(JsonElement message)
    {
        AgentRemoteControlStatus? status = ClaudeCodeJson.GetString(message, "state") switch
        {
            "connected" => AgentRemoteControlStatus.Connected,
            "failed" or "error" => AgentRemoteControlStatus.Failed,
            // Any other state is the bridge on its way: ready, or connecting again after it lost its connection.
            { Length: > 0 } => AgentRemoteControlStatus.Connecting,
            _ => null,
        };
        if (status is not { } next)
        {
            return;
        }

        var error = ClaudeCodeJson.GetString(message, "error") ?? "The connection to claude.ai failed.";
        UpdateRemoteControl(current =>
        {
            if (!_remoteControlWanted)
            {
                return null;
            }

            // The bridge can say it is connected before the answer that gives its link is read: it is connected
            // once the link is known, which the answer then says.
            var shown = next == AgentRemoteControlStatus.Connected && current.SessionUrl is null ? AgentRemoteControlStatus.Connecting : next;
            return new AgentRemoteControl(shown, current.SessionUrl, shown == AgentRemoteControlStatus.Failed ? error : null);
        });
    }

    // A process that ends takes its bridge with it: one that is wanted shows as connecting until a process is
    // connected again, the next one that starts. A failure stays shown: it says why nothing connects.
    private void NoteRemoteControlProcessEnded()
    {
        UpdateRemoteControl(current => !_remoteControlWanted || Volatile.Read(ref _disposed) != 0 ? AgentRemoteControl.Off
            : current.Status == AgentRemoteControlStatus.Failed ? null
            : new AgentRemoteControl(AgentRemoteControlStatus.Connecting, current.SessionUrl));
    }

    // A process that stops by itself takes its bridge with it, and nothing starts another one before the next prompt
    // sent here: claude.ai cannot reach the session meanwhile.
    private void NoteRemoteControlProcessStopped()
    {
        UpdateRemoteControl(current => _remoteControlWanted && Volatile.Read(ref _disposed) == 0
            ? new AgentRemoteControl(AgentRemoteControlStatus.Failed, current.SessionUrl,
                "Claude Code stopped. Remote Control connects again with the next prompt sent here, or with Try again.")
            : null);
    }

    // A prompt sent from claude.ai: the CLI replays it as a prompt of a person, with an identity this side did not
    // give, then runs its turn by itself. Its text is kept for the run that shows that turn.
    private void NoteRemotePrompt(JsonElement message)
    {
        if (!ClaudeCodeJson.TryGetObject(message, "origin", out var origin) ||
            !string.Equals(ClaudeCodeJson.GetString(origin, "kind"), "human", StringComparison.Ordinal) ||
            ClaudeCodeJson.GetString(message, "uuid") is not { Length: > 0 } uuid ||
            !ClaudeCodeJson.TryGetObject(message, "message", out var content) ||
            PromptText(content) is not { } text)
        {
            return;
        }

        lock (_gate)
        {
            if (!_outstandingUserMessages.Contains(uuid) && !_unansweredUserMessages.Contains(uuid))
            {
                _remotePrompt = text;
            }
        }
    }

    // The text of a prompt: a string, or the text blocks of a list of blocks.
    private static string? PromptText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() is { Length: > 0 } single && !string.IsNullOrWhiteSpace(single) ? single : null;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Where(static block => block.ValueKind == JsonValueKind.Object && ClaudeCodeJson.GetString(block, "type") == "text")
            .Select(static block => ClaudeCodeJson.GetString(block, "text"))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    private void ChangeRemoteControl(AgentRemoteControl next) => UpdateRemoteControl(_ => next);

    // Computes a change from the state it replaces and applies it under the gate, then tells those who listen when
    // it changed what they are shown. The reader of the CLI and the answer to a request change it at the same time:
    // a change computed from a state that was replaced meanwhile would undo the other one. Null changes nothing.
    private void UpdateRemoteControl(Func<AgentRemoteControl, AgentRemoteControl?> update)
    {
        lock (_remoteControlNotice)
        {
            Action<AgentRemoteControl>? handler;
            AgentRemoteControl next;
            lock (_gate)
            {
                if (update(_remoteControl) is not { } computed || computed == _remoteControl)
                {
                    return;
                }

                _remoteControl = next = computed;
                handler = _onRemoteControl;
            }

            handler?.Invoke(next);
        }
    }
}
