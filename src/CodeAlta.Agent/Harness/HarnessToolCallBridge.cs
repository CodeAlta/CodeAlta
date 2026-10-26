using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Harness;

/// <summary>
/// A tool call an agent harness made in an answer it gave the session: one of its own tools, whose result it gives,
/// or a tool of CodeAlta, which it asks the session to run through the MCP server of the session.
/// </summary>
public sealed class HarnessToolCall
{
    internal HarnessToolCall(string id, string name, JsonElement arguments, bool isCodeAltaTool)
    {
        Id = id;
        Name = name;
        Arguments = arguments;
        IsCodeAltaTool = isCodeAltaTool;
    }

    /// <summary>Gets the identifier of the call, the one of its tool call part.</summary>
    public string Id { get; }

    /// <summary>Gets the name the session knows the tool by.</summary>
    public string Name { get; }

    /// <summary>Gets the arguments of the call.</summary>
    public JsonElement Arguments { get; }

    /// <summary>Gets whether the tool is one of CodeAlta, which the session runs.</summary>
    public bool IsCodeAltaTool { get; }

    internal TaskCompletionSource<AgentToolResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<HarnessPendingMcpCall> McpCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool McpMatched { get; set; }

    internal AgentToolProgressHandler? Progress { get; set; }

    internal List<string> BufferedProgress { get; } = [];

    /// <summary>Gets whether the call has its result.</summary>
    public bool IsCompleted => Result.Task.IsCompleted;
}

/// <summary>A call of a tool of CodeAlta by the harness, waiting for the session to run it.</summary>
internal sealed class HarnessPendingMcpCall(HarnessMcpToolCall call)
{
    public HarnessMcpToolCall Call { get; } = call;

    public TaskCompletionSource<AgentToolResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Presents the tool calls of an agent harness that runs its own loop to the session as calls it runs itself, so that
/// the session shows, records and replays them as it does for any provider (see <see cref="IAgentProviderToolHost" />).
/// </summary>
/// <remarks>
/// <para>
/// The executor registers each tool call it returns in an answer (<see cref="Register" />). The session then resolves
/// it (<see cref="Resolve" />): a call of the harness's own tool waits for the result the harness gives
/// (<see cref="Complete" />); a call of a tool of CodeAlta waits for the harness to call it through the MCP server of
/// the session (<see cref="CallFromMcpAsync" />), runs it, and gives the result back to the harness.
/// </para>
/// <para>
/// An MCP call names no tool call: it is matched to the oldest call of the same tool and arguments that waits for one,
/// else to the oldest of the same tool. A call that matches none (a sub-agent of the harness) is run by itself.
/// </para>
/// </remarks>
public sealed class HarnessToolCallBridge
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HarnessToolCall> _calls = new(StringComparer.Ordinal);
    private readonly List<HarnessToolCall> _order = [];

    /// <summary>
    /// Registers a tool call of an answer of the harness.
    /// </summary>
    /// <param name="callId">The identifier of the tool call part.</param>
    /// <param name="name">The name the session knows the tool by.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="isCodeAltaTool">Whether the tool is one of CodeAlta.</param>
    /// <returns>The call.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="callId" /> or <paramref name="name" /> is empty.</exception>
    public HarnessToolCall Register(string callId, string name, JsonElement arguments, bool isCodeAltaTool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_calls.TryGetValue(callId, out var existing))
            {
                return existing;
            }

            var call = new HarnessToolCall(callId, name, arguments.Clone(), isCodeAltaTool);
            _calls[callId] = call;
            _order.Add(call);
            return call;
        }
    }

    /// <summary>Returns a registered call, or <see langword="null" />.</summary>
    /// <param name="callId">The identifier of the call.</param>
    public HarnessToolCall? Find(string callId)
    {
        lock (_gate)
        {
            return _calls.GetValueOrDefault(callId);
        }
    }

    /// <summary>Notes that the harness started to run a call.</summary>
    /// <param name="callId">The identifier of the call.</param>
    public void MarkStarted(string callId) => Find(callId)?.Started.TrySetResult();

    /// <summary>Gives the result the harness gave for a call of its own tool, or for a call it answered itself.</summary>
    /// <param name="callId">The identifier of the call.</param>
    /// <param name="result">The result.</param>
    /// <returns>Whether the call was waiting for it.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public bool Complete(string callId, AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Find(callId) is not { } call)
        {
            return false;
        }

        call.Started.TrySetResult();
        return call.Result.TrySetResult(result);
    }

    /// <summary>Shows output of a call while it runs (what a command writes).</summary>
    /// <param name="callId">The identifier of the call.</param>
    /// <param name="delta">The output.</param>
    /// <param name="cancellationToken">A token to cancel the report.</param>
    public async ValueTask ReportProgressAsync(string callId, string delta, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(delta) || Find(callId) is not { } call)
        {
            return;
        }

        AgentToolProgressHandler? progress;
        lock (_gate)
        {
            progress = call.Progress;
            if (progress is null)
            {
                call.BufferedProgress.Add(delta);
                return;
            }
        }

        await progress(new AgentToolProgressUpdate(delta), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends every call that waits for its result with a failure: the harness stopped, or the turn was given up.</summary>
    /// <param name="message">What the failures say.</param>
    public void FailOutstanding(string message)
    {
        HarnessToolCall[] calls;
        lock (_gate)
        {
            calls = [.. _order];
        }

        foreach (var call in calls)
        {
            call.Started.TrySetResult();
            call.Result.TrySetResult(Failure(message));
            call.McpCall.TrySetCanceled();
        }
    }

    /// <summary>
    /// Gives a result to every call of the harness's own tools that waits for one: the harness went on without
    /// reporting their end. A result it reports later for one of them is ignored.
    /// </summary>
    /// <param name="result">The result of those calls.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public void CompleteOwnOutstanding(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        HarnessToolCall[] calls;
        lock (_gate)
        {
            calls = [.. _order.Where(static call => !call.IsCodeAltaTool && !call.Result.Task.IsCompleted)];
        }

        foreach (var call in calls)
        {
            call.Started.TrySetResult();
            call.Result.TrySetResult(result);
        }
    }

    /// <summary>Forgets the calls that have their result.</summary>
    public void ForgetCompleted()
    {
        lock (_gate)
        {
            _order.RemoveAll(call =>
            {
                if (!call.Result.Task.IsCompleted)
                {
                    return false;
                }

                _calls.Remove(call.Id);
                return true;
            });
        }
    }

    /// <summary>Gets whether a call waits for its result.</summary>
    public bool HasOutstandingCalls
    {
        get
        {
            lock (_gate)
            {
                return _order.Exists(static call => !call.Result.Task.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Returns the definition that runs a tool call of an answer of the harness, or <see langword="null" /> for a call
    /// that was not registered: see <see cref="IAgentProviderToolHost.ResolveTool" />.
    /// </summary>
    /// <param name="toolCall">The tool call part.</param>
    /// <param name="registered">The tool of the session with that name, when there is one.</param>
    /// <param name="externalDescription">The description of a tool of the harness, such as "A tool run by Copilot.".</param>
    /// <param name="interruptAsync">Stops the turn of the harness when the session gives up a call.</param>
    /// <returns>The definition, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolCall" /> or <paramref name="interruptAsync" /> is <see langword="null" />.</exception>
    public AgentToolDefinition? Resolve(
        AgentMessagePart.ToolCall toolCall,
        AgentToolDefinition? registered,
        string externalDescription,
        Func<Task> interruptAsync)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(interruptAsync);
        if (Find(toolCall.CallId) is not { } call)
        {
            return null;
        }

        if (call.IsCodeAltaTool && registered is not null)
        {
            return new AgentToolDefinition(registered.Spec, (invocation, cancellationToken) => RunCodeAltaToolAsync(call, registered, invocation, interruptAsync, cancellationToken));
        }

        var spec = new AgentToolSpec(SanitizeName(toolCall.Name), externalDescription, EmptyObject);
        return new AgentToolDefinition(spec, (invocation, cancellationToken) => AwaitResultAsync(call, invocation, interruptAsync, cancellationToken));
    }

    /// <summary>Returns the task that ends when the harness starts to run a call, or <see langword="null" />.</summary>
    /// <param name="callId">The identifier of the call.</param>
    public Task? WhenStarted(string callId) => Find(callId)?.Started.Task;

    /// <summary>
    /// Runs a call of a tool of CodeAlta the harness made through the MCP server of the session: the call the session
    /// runs, when it matches one, else the tool by itself.
    /// </summary>
    /// <param name="call">The MCP call.</param>
    /// <param name="findTool">Returns the tool of the session with a name, or <see langword="null" />.</param>
    /// <param name="providerId">The provider of the session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The result to give the harness.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="call" /> or <paramref name="findTool" /> is <see langword="null" />.</exception>
    public async Task<AgentToolResult> CallFromMcpAsync(
        HarnessMcpToolCall call,
        Func<string, AgentToolDefinition?> findTool,
        ModelProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(findTool);
        HarnessToolCall? owner = null;
        lock (_gate)
        {
            HarnessToolCall? byName = null;
            foreach (var candidate in _order)
            {
                if (!candidate.IsCodeAltaTool || candidate.McpMatched || candidate.Result.Task.IsCompleted ||
                    !string.Equals(candidate.Name, call.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (JsonElement.DeepEquals(candidate.Arguments, call.Arguments))
                {
                    owner = candidate;
                    break;
                }

                byName ??= candidate;
            }

            owner ??= byName;
            if (owner is not null)
            {
                owner.McpMatched = true;
            }
        }

        if (owner is not null)
        {
            var pending = new HarnessPendingMcpCall(call);
            owner.Started.TrySetResult();
            owner.McpCall.TrySetResult(pending);
            return await pending.Response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (findTool(call.Name) is not { } definition)
        {
            return Failure($"Tool '{call.Name}' is not available in this session.");
        }

        try
        {
            return await definition.Handler(
                    new AgentToolInvocation(providerId, sessionId, $"harness-mcp-{Guid.NewGuid():N}", definition.Spec.Name, call.Arguments),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure($"Tool '{call.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>Creates the result of a failed call.</summary>
    /// <param name="message">What the failure says.</param>
    /// <returns>The result.</returns>
    public static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    /// <summary>
    /// Makes a name the session can record a tool under: letters, digits, <c>_</c> and <c>-</c>, at most 64 characters.
    /// </summary>
    /// <param name="name">The name the harness gave.</param>
    /// <returns>The name, or <c>tool</c> when nothing is left of it.</returns>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "tool";
        }

        Span<char> buffer = stackalloc char[Math.Min(name.Length, 64)];
        var length = 0;
        foreach (var character in name)
        {
            if (length == buffer.Length)
            {
                break;
            }

            buffer[length++] = char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_';
        }

        var sanitized = new string(buffer[..length]).Trim('_');
        return sanitized.Length == 0 ? "tool" : sanitized;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private async Task<AgentToolResult> AwaitResultAsync(
        HarnessToolCall call,
        AgentToolInvocation invocation,
        Func<Task> interruptAsync,
        CancellationToken cancellationToken)
    {
        string[] buffered;
        lock (_gate)
        {
            call.Progress = invocation.Progress;
            buffered = [.. call.BufferedProgress];
            call.BufferedProgress.Clear();
        }

        try
        {
            if (invocation.Progress is { } progress)
            {
                foreach (var delta in buffered)
                {
                    await progress(new AgentToolProgressUpdate(delta), cancellationToken).ConfigureAwait(false);
                }
            }

            return await call.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await interruptAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                call.Progress = null;
            }
        }
    }

    private static async Task<AgentToolResult> RunCodeAltaToolAsync(
        HarnessToolCall call,
        AgentToolDefinition registered,
        AgentToolInvocation invocation,
        Func<Task> interruptAsync,
        CancellationToken cancellationToken)
    {
        HarnessPendingMcpCall? pending = null;
        try
        {
            var first = await Task.WhenAny(call.McpCall.Task, call.Result.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first == call.Result.Task && !call.McpCall.Task.IsCompletedSuccessfully)
            {
                // The harness answered the call itself: it refused it, or it stopped.
                return await call.Result.Task.ConfigureAwait(false);
            }

            pending = await call.McpCall.Task.ConfigureAwait(false);
            AgentToolResult result;
            try
            {
                result = await registered.Handler(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Failure($"Tool '{call.Name}' failed: {ex.Message}");
            }

            pending.Response.TrySetResult(result);
            call.Result.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pending?.Response.TrySetResult(Failure("The tool call was interrupted."));
            await interruptAsync().ConfigureAwait(false);
            throw;
        }
    }
}
