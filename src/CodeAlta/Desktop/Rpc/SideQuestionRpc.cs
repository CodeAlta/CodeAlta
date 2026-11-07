using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The side questions of the composer (<c>/ask</c>): a question about a session, answered by its model from its
/// conversation, while it runs or not. The question and answer are not added to CodeAlta session history; the page
/// keeps them while it shows the session. Provider-side retention follows the provider's own policy.
/// </summary>
[NeoRpcService("sideQuestion", Version = 1)]
internal sealed class SideQuestionService
{
    internal const int MaximumQuestionLength = 16_384;
    private readonly Func<string, string, Func<string, CancellationToken, ValueTask>, CancellationToken, Task<string>> _ask;
    private readonly string _epoch;

    /// <summary>Creates the service on the runtime of the sessions.</summary>
    internal SideQuestionService(SessionRuntimeService runtime, string epoch)
        : this((runtime ?? throw new ArgumentNullException(nameof(runtime))).AskSideQuestionAsync, epoch) { }

    /// <summary>Creates the service on what answers: the session, the question, what receives the text as it streams.</summary>
    internal SideQuestionService(Func<string, string, Func<string, CancellationToken, ValueTask>, CancellationToken, Task<string>> ask, string epoch)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _ask = ask;
        _epoch = epoch;
    }

    /// <summary>
    /// Asks a question and streams its answer: <c>text</c> events as it is written, then <c>done</c> with the whole answer,
    /// or one <c>error</c>. Closing the stream stops the request.
    /// </summary>
    [NeoRpcMethod("ask")]
    public NeoRpcChannel<SideQuestionEvent> Ask(SideQuestionRequest request, CancellationToken cancellationToken)
        => new(AskAsync(request, cancellationToken), DesktopJsonContext.Default.SideQuestionEvent);

    internal async IAsyncEnumerable<SideQuestionEvent> AskAsync(SideQuestionRequest? request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256
            || string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaximumQuestionLength)
        {
            yield return SideQuestionEvent.Error("invalid_request", null);
            yield break;
        }

        if (request.ExpectedHostEpoch != _epoch)
        {
            yield return SideQuestionEvent.Error("stale_epoch", null);
            yield break;
        }

        var events = Channel.CreateBounded<SideQuestionEvent>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = RunAsync(request.SessionId, request.Question, events.Writer, stop.Token);
        try
        {
            await foreach (var @event in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return @event;
        }
        finally
        {
            // The page closed the stream (Esc) or the window went away: the request stops.
            await stop.CancelAsync().ConfigureAwait(false);
            await work.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(string sessionId, string question, ChannelWriter<SideQuestionEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            SideQuestionEvent? terminal;
            try
            {
                var answer = await _ask(sessionId, question.Trim(),
                    (text, _) => events.WriteAsync(SideQuestionEvent.Text(text), cancellationToken), cancellationToken).ConfigureAwait(false);
                terminal = SideQuestionEvent.Done(answer);
            }
            catch (KeyNotFoundException exception) { terminal = SideQuestionEvent.Error("missing_session", exception.Message); }
            catch (NotSupportedException exception) { terminal = SideQuestionEvent.Error("unsupported", exception.Message); }
            catch (ObjectDisposedException) { terminal = SideQuestionEvent.Error("closed", null); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { terminal = null; }
            catch (Exception exception) { terminal = SideQuestionEvent.Error("failed", exception.Message); }
            if (terminal is not null) await events.WriteAsync(terminal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The reader closed while the bounded stream was full.
        }
        finally { events.TryComplete(); }
    }
}

internal sealed record SideQuestionRequest(string ExpectedHostEpoch, string SessionId, string Question);

/// <summary>An event of the answer of a side question.</summary>
/// <param name="Kind"><c>text</c> (a part of the answer as it streams), <c>done</c> (the whole answer) or <c>error</c>.</param>
/// <param name="Value">The text of a part, the whole answer, or what failed.</param>
/// <param name="Status">For an error: <c>invalid_request</c>, <c>stale_epoch</c>, <c>missing_session</c>, <c>unsupported</c>, <c>closed</c> or <c>failed</c>.</param>
internal sealed record SideQuestionEvent(string Kind, string? Value, string? Status)
{
    internal static SideQuestionEvent Text(string value) => new("text", value, null);
    internal static SideQuestionEvent Done(string value) => new("done", value, null);
    internal static SideQuestionEvent Error(string status, string? message) => new("error", message, status);
}
