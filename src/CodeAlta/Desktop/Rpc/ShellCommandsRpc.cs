using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Jobs;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The shell commands of the composer (a prompt that starts with <c>!</c>): a command the user types runs in the folder
/// the session works in, or in the folder of the project of a session to start, in the shell of the <c>shell_command</c>
/// tool and with the variables of the session's commands (for a project, of the space the window shows), and what it
/// writes streams back to the page. It is the user's own command, as in a terminal of the window: no permission is asked
/// and the sandbox of the session does not apply. Nothing is written to a session; the page keeps the commands, and gives
/// an output to the model only as an attachment of a prompt the user sends.
/// </summary>
[NeoRpcService("shellCommands", Version = 1)]
internal sealed class ShellCommandsService
{
    internal const int MaximumCommandLength = 16_384;

    /// <summary>Most characters of an output streamed to the page; the rest is counted, and only its end is sent.</summary>
    internal const int MaximumOutputCharacters = 512 * 1024;

    /// <summary>Most characters of the end of an output past <see cref="MaximumOutputCharacters"/> sent with its exit code: a summary or an error is often there.</summary>
    internal const int MaximumTailCharacters = 20_000;

    private readonly Func<ShellCommandTarget, CancellationToken, Task<ShellCommandPlace?>> _place;
    private readonly ShellCommandStarter _start;
    private readonly string _epoch;

    /// <summary>Creates the service on what finds where a command runs, the shell of the <c>shell_command</c> tool starting it.</summary>
    /// <param name="place">Gives the folder and the variables of a session or a project; null for one that is not known.</param>
    /// <param name="epoch">The epoch of the host.</param>
    internal ShellCommandsService(Func<ShellCommandTarget, CancellationToken, Task<ShellCommandPlace?>> place, string epoch)
        : this(place, ShellCommandProcess.Start, epoch) { }

    /// <summary>Creates the service on what finds where a command runs and what starts it.</summary>
    internal ShellCommandsService(Func<ShellCommandTarget, CancellationToken, Task<ShellCommandPlace?>> place, ShellCommandStarter start, string epoch)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _place = place;
        _start = start;
        _epoch = epoch;
    }

    /// <summary>
    /// Runs a command and streams it: <c>started</c> with the folder it runs in, <c>output</c> events as it writes, then
    /// <c>exit</c> with its exit code; or one <c>error</c>. Closing the stream ends the command and what it started.
    /// </summary>
    [NeoRpcMethod("run")]
    public NeoRpcChannel<ShellCommandEvent> Run(ShellCommandRequest request, CancellationToken cancellationToken)
        => new(RunAsync(request, cancellationToken), DesktopJsonContext.Default.ShellCommandEvent);

    internal async IAsyncEnumerable<ShellCommandEvent> RunAsync(ShellCommandRequest? request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A command runs for a session, or for the project of a session to start: one of them, never both.
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId) == string.IsNullOrWhiteSpace(request.ProjectId)
            || request.SessionId?.Length > 256 || request.ProjectId?.Length > 256
            || string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > MaximumCommandLength)
        {
            yield return ShellCommandEvent.Error("invalid_request", null);
            yield break;
        }

        if (request.ExpectedHostEpoch != _epoch)
        {
            yield return ShellCommandEvent.Error("stale_epoch", null);
            yield break;
        }

        var target = string.IsNullOrWhiteSpace(request.SessionId) ? new ShellCommandTarget(null, request.ProjectId) : new ShellCommandTarget(request.SessionId, null);
        var (folder, environment, failure) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        var events = Channel.CreateUnbounded<ShellCommandEvent>(new UnboundedChannelOptions { SingleReader = true });
        var output = new OutputLimit(events.Writer);
        IShellCommandProcess? process = null;
        string? startFailure = null;
        try { process = _start(request.Command.Trim(), folder!, environment, output.Write); }
        catch (InvalidOperationException exception) { startFailure = exception.Message; }
        if (process is null)
        {
            yield return ShellCommandEvent.Error("failed", startFailure);
            yield break;
        }

        try
        {
            yield return ShellCommandEvent.Started(folder!);
            // The output is all read before the command completes: its exit code comes last.
            _ = process.Completion.ContinueWith(completed =>
            {
                var (omitted, tail) = output.End();
                events.Writer.TryWrite(ShellCommandEvent.Exit(completed.Result, omitted, tail));
                events.Writer.TryComplete();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await foreach (var @event in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return @event;
        }
        finally
        {
            // The page closed the stream (Stop) or the window went away: the command ends with what it started.
            process.Kill();
            process.Dispose();
        }
    }

    private async Task<(string? Folder, AgentProcessEnvironment? Environment, ShellCommandEvent? Failure)> ResolveAsync(ShellCommandTarget target, CancellationToken cancellationToken)
    {
        try
        {
            var place = await _place(target, cancellationToken).ConfigureAwait(false);
            if (place is null) return (null, null, ShellCommandEvent.Error(target.SessionId is null ? "missing_project" : "missing_session", null));
            if (!Directory.Exists(place.Folder)) return (null, null, ShellCommandEvent.Error("missing_folder", place.Folder));
            return (place.Folder, place.Environment, null);
        }
        catch (ObjectDisposedException) { return (null, null, ShellCommandEvent.Error("closed", null)); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, null, ShellCommandEvent.Error("failed", exception.Message));
        }
    }

    // What the command writes, up to the limit the page is sent, and the end of what goes past it; it is written from the
    // threads that read its two outputs.
    private sealed class OutputLimit(ChannelWriter<ShellCommandEvent> writer)
    {
        private readonly Lock _lock = new();
        private readonly System.Text.StringBuilder _tail = new();
        private long _written;
        private long _omitted;

        // What was left out, counted up to int.MaxValue (past it, only said to be large), and the last characters of it.
        internal (int Omitted, string? Tail) End()
        {
            lock (_lock)
            {
                if (_tail.Length > MaximumTailCharacters) _tail.Remove(0, _tail.Length - MaximumTailCharacters);
                return ((int)Math.Min(_omitted, int.MaxValue), _tail.Length > 0 ? _tail.ToString() : null);
            }
        }

        internal void Write(string text)
        {
            lock (_lock)
            {
                var room = (int)Math.Max(0, MaximumOutputCharacters - _written);
                var sent = text.Length > room ? text[..room] : text;
                if (sent.Length > 0)
                {
                    _written += sent.Length;
                    writer.TryWrite(ShellCommandEvent.Output(sent));
                }

                if (sent.Length == text.Length) return;
                _omitted += text.Length - sent.Length;
                _tail.Append(text, sent.Length, text.Length - sent.Length);
                // The end is kept as it comes, trimmed once it is twice as long as what is sent of it.
                if (_tail.Length > 2 * MaximumTailCharacters) _tail.Remove(0, _tail.Length - MaximumTailCharacters);
            }
        }
    }
}

/// <summary>A shell command of the composer.</summary>
/// <param name="ExpectedHostEpoch">The epoch of the host the page knows.</param>
/// <param name="SessionId">The session whose folder the command runs in; null for a command of the project of a session to start.</param>
/// <param name="ProjectId">The project whose folder the command runs in, before its session starts; null for a command of a session.</param>
/// <param name="Command">The command line, as the user typed it after <c>!</c>.</param>
internal sealed record ShellCommandRequest(string ExpectedHostEpoch, string? SessionId, string? ProjectId, string Command);

/// <summary>What a command runs for: a session, or the project of a session to start.</summary>
internal sealed record ShellCommandTarget(string? SessionId, string? ProjectId);

/// <summary>Where a command runs: the folder, and the variables over the environment of the application.</summary>
internal sealed record ShellCommandPlace(string Folder, AgentProcessEnvironment Environment);

/// <summary>An event of a shell command of the composer.</summary>
/// <param name="Kind"><c>started</c>, <c>output</c>, <c>exit</c> or <c>error</c>.</param>
/// <param name="Value">
/// The folder it runs in (<c>started</c>), what it wrote (<c>output</c>), the end of what was not sent of its output
/// (<c>exit</c>, at most <see cref="ShellCommandsService.MaximumTailCharacters"/>; null when everything was sent), or what failed (<c>error</c>).
/// </param>
/// <param name="Status">For an error: <c>invalid_request</c>, <c>stale_epoch</c>, <c>missing_session</c>, <c>missing_project</c>, <c>missing_folder</c>, <c>closed</c> or <c>failed</c>.</param>
/// <param name="ExitCode">For <c>exit</c>: the exit code of the command, <c>-1</c> when it could not be read.</param>
/// <param name="Omitted">For <c>exit</c>: the characters of its output past the limit, which were not streamed.</param>
internal sealed record ShellCommandEvent(string Kind, string? Value, string? Status, int? ExitCode, int? Omitted)
{
    internal static ShellCommandEvent Started(string folder) => new("started", folder, null, null, null);
    internal static ShellCommandEvent Output(string text) => new("output", text, null, null, null);
    internal static ShellCommandEvent Exit(int code, int omitted, string? tail) => new("exit", tail, null, code, omitted);
    internal static ShellCommandEvent Error(string status, string? message) => new("error", message, status, null, null);
}
