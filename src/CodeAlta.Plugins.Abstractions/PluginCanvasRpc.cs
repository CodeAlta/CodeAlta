using System.Text.Json;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Answers a call that the script of a canvas made to its plugin, and gives a result.</summary>
/// <typeparam name="TRequest">The type of the request, read from the JSON that the script passed.</typeparam>
/// <typeparam name="TResult">The type of the result, written as JSON for the script.</typeparam>
/// <param name="request">The request. A call without input gives an object read from <c>{}</c>.</param>
/// <param name="cancellationToken">A token cancelled when the script cancels the call, when the call times out, when the connection closes, and when the instance closes or the plugin stops.</param>
/// <returns>The result.</returns>
public delegate ValueTask<TResult> PluginRpcHandler<in TRequest, TResult>(TRequest request, CancellationToken cancellationToken);

/// <summary>Answers a call that the script of a canvas made to its plugin, and gives no result.</summary>
/// <typeparam name="TRequest">The type of the request, read from the JSON that the script passed.</typeparam>
/// <param name="request">The request. A call without input gives an object read from <c>{}</c>.</param>
/// <param name="cancellationToken">A token cancelled when the script cancels the call, when the call times out, when the connection closes, and when the instance closes or the plugin stops.</param>
/// <returns>A task that completes when the call is done.</returns>
public delegate ValueTask PluginRpcVoidHandler<in TRequest>(TRequest request, CancellationToken cancellationToken);

/// <summary>Answers a call that the script of a canvas made to its plugin with a stream of items.</summary>
/// <typeparam name="TRequest">The type of the request, read from the JSON that the script passed.</typeparam>
/// <typeparam name="TItem">The type of an item, written as JSON for the script.</typeparam>
/// <param name="request">The request. A call without input gives an object read from <c>{}</c>.</param>
/// <param name="cancellationToken">A token cancelled when the script stops reading the stream, when the connection closes, and when the instance closes or the plugin stops. Honor it in the iterator.</param>
/// <returns>The items. The window takes the next one only when the script has taken the ones sent before, so a slow reader slows the iterator down and never fills a queue.</returns>
public delegate IAsyncEnumerable<TItem> PluginRpcStreamHandler<in TRequest, out TItem>(TRequest request, CancellationToken cancellationToken);

/// <summary>
/// The calls that the script of a canvas makes to its plugin, and the events the plugin sends to the script.
/// </summary>
/// <remarks>
/// <para>
/// A plugin registers its handlers in the <see cref="PluginCanvasContribution.Open"/> handler of the canvas, before it returns
/// the view: the script cannot call before that, and a handler registered later throws. Each open instance has its own
/// registry, so a handler can keep what it needs of the instance (<see cref="PluginCanvasContext.ProjectId"/>, the state of the
/// plugin) in the closure it was written with.
/// </para>
/// <para>
/// The script calls by name: <c>alta.rpc.invoke("board.get", input)</c>, <c>alta.rpc.stream("board.watch", input)</c>,
/// <c>alta.rpc.subscribe("board.changed", handler)</c>. Requests and results are ordinary types, written as JSON with
/// <see cref="JsonOptions"/>: the web defaults (camelCase names, numbers read as numbers), unless the plugin gives its own.
/// A call fails with a stable error code that the script can test. What a handler throws does not cross to the page: it
/// is logged by the plugin's logger and the script gets <c>internal_error</c>, unless the handler throws a
/// <see cref="PluginRpcException"/> with a code and a message made to be shown.
/// </para>
/// <para>
/// The window bounds what it carries: a request or a frame of at most 1 MiB, 8 calls running at once, 8 streams and 8 subscriptions
/// open, about 200 calls a second, and 30 seconds for a call. Past a limit the script gets <c>too_many_requests</c>,
/// <c>payload_too_large</c> or <c>timeout</c>.
/// </para>
/// </remarks>
public interface IPluginCanvasRpc
{
    /// <summary>Gets a value indicating whether a window can carry the calls of a script. It is false for a context that has no tab, which registers nothing.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets or sets the options that read the requests and write the results and the items. The default is <see cref="JsonSerializerDefaults.Web"/>.
    /// A plugin that has a source-generated context sets <c>new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = MyContext.Default }</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The registry is sealed: the handler that opens the instance returned.</exception>
    JsonSerializerOptions JsonOptions { get; set; }

    /// <summary>Registers a handler of a call that gives a result.</summary>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="name">The name the script calls: 1 to 64 of the characters <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>-</c> and <c>_</c>, starting with a letter or a digit (<see cref="PluginRpc.IsValidName"/>).</param>
    /// <param name="handler">The handler.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is not valid, or a handler, a stream or an event already has it.</exception>
    /// <exception cref="InvalidOperationException">The registry is sealed: the handler that opens the instance returned.</exception>
    void Handle<TRequest, TResult>(string name, PluginRpcHandler<TRequest, TResult> handler);

    /// <summary>Registers a handler of a call that gives no result.</summary>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    /// <param name="name">The name the script calls (see <see cref="PluginRpc.IsValidName"/>).</param>
    /// <param name="handler">The handler.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is not valid, or a handler or a stream already has it.</exception>
    /// <exception cref="InvalidOperationException">The registry is sealed: the handler that opens the instance returned.</exception>
    void Handle<TRequest>(string name, PluginRpcVoidHandler<TRequest> handler);

    /// <summary>Registers a handler of a call that answers with a stream: the script reads the items one at a time, with backpressure.</summary>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    /// <typeparam name="TItem">The type of an item.</typeparam>
    /// <param name="name">The name the script calls (see <see cref="PluginRpc.IsValidName"/>).</param>
    /// <param name="handler">The handler. An exception it throws ends the stream with <c>internal_error</c>; a stream has no way to carry the code of a <see cref="PluginRpcException"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is not valid, or a handler or a stream already has it.</exception>
    /// <exception cref="InvalidOperationException">The registry is sealed: the handler that opens the instance returned.</exception>
    void Stream<TRequest, TItem>(string name, PluginRpcStreamHandler<TRequest, TItem> handler);

    /// <summary>
    /// Sends an event to the script, which listens with <c>alta.rpc.subscribe(name, handler)</c>. An event is not kept: a script that
    /// is not subscribed yet, or whose tab is closed, does not get it, so a script reads the state with a call after it subscribed.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="name">The name of the event (see <see cref="PluginRpc.IsValidName"/>).</param>
    /// <param name="value">The value, written as JSON with <see cref="JsonOptions"/>. An event above 1 MiB is dropped.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the event is queued for the script. It does nothing when no script listens, and once the instance is closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is not valid.</exception>
    ValueTask PublishAsync<T>(string name, T value, CancellationToken cancellationToken = default);
}

/// <summary>
/// An error of a call from the script of a canvas that the plugin made to be shown: the script gets its code, its message and its
/// <see cref="Retryable"/> mark as they are written. Any other exception a handler throws reaches the script as <c>internal_error</c>.
/// </summary>
public sealed class PluginRpcException : Exception
{
    /// <summary>Creates an error that a retry cannot fix.</summary>
    /// <param name="code">The stable code the script tests: 1 to 64 of <c>a-z</c>, <c>0-9</c> and <c>_</c>, starting with a letter (<c>not_found</c>, <c>conflict</c>).</param>
    /// <param name="message">What a person can read: one line, without a stack trace, a path or a secret. It is cut at 400 characters.</param>
    /// <exception cref="ArgumentException">The code is not valid, or the message is empty.</exception>
    public PluginRpcException(string code, string message)
        : this(code, message, retryable: false)
    {
    }

    /// <summary>Creates an error.</summary>
    /// <param name="code">The stable code the script tests: 1 to 64 of <c>a-z</c>, <c>0-9</c> and <c>_</c>, starting with a letter (<c>not_found</c>, <c>conflict</c>).</param>
    /// <param name="message">What a person can read: one line, without a stack trace, a path or a secret. It is cut at 400 characters.</param>
    /// <param name="retryable">Whether the same call can succeed later (a lock that is held, a source that is busy).</param>
    /// <exception cref="ArgumentException">The code is not valid, or the message is empty.</exception>
    public PluginRpcException(string code, string message, bool retryable)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!IsValidCode(code)) throw new ArgumentException("The code is 1 to 64 of a-z, 0-9 and _, starting with a letter.", nameof(code));
        Code = code;
        Retryable = retryable;
    }

    /// <summary>Whether a text can be the code of an error: 1 to 64 of <c>a-z</c>, <c>0-9</c> and <c>_</c>, starting with a letter.</summary>
    /// <param name="code">The text.</param>
    /// <returns><see langword="true"/> when the text can be the code of a <see cref="PluginRpcException"/>.</returns>
    public static bool IsValidCode(string? code)
    {
        if (code is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterLower(code[0])) return false;
        foreach (var value in code)
        {
            if (!(char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value) || value == '_')) return false;
        }

        return true;
    }

    /// <summary>Gets the stable code of the error.</summary>
    public string Code { get; }

    /// <summary>Gets a value indicating whether the same call can succeed later.</summary>
    public bool Retryable { get; }
}

/// <summary>The rules that the names of the calls and the events of a canvas follow.</summary>
public static class PluginRpc
{
    /// <summary>The longest name of a call or an event.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>Whether a text is a name of a call or an event: 1 to 64 of <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>-</c> and <c>_</c>, starting with a letter or a digit.</summary>
    /// <param name="name">The text.</param>
    /// <returns><see langword="true"/> when the text can name a call or an event.</returns>
    public static bool IsValidName(string? name)
    {
        if (name is not { Length: > 0 and <= MaximumNameLength } || !(char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))) return false;
        foreach (var value in name)
        {
            if (!(char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value) || value is '.' or '-' or '_')) return false;
        }

        return true;
    }
}

/// <summary>
/// The registry of a context that has no tab (a description, the action of an agent): it accepts nothing, and everything it is asked does nothing. Each context has its own.
/// </summary>
public sealed class NoopPluginCanvasRpc : IPluginCanvasRpc
{
    private JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public JsonSerializerOptions JsonOptions
    {
        get => _options;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _options = value;
        }
    }

    /// <inheritdoc />
    public void Handle<TRequest, TResult>(string name, PluginRpcHandler<TRequest, TResult> handler) => Check(name, handler);

    /// <inheritdoc />
    public void Handle<TRequest>(string name, PluginRpcVoidHandler<TRequest> handler) => Check(name, handler);

    /// <inheritdoc />
    public void Stream<TRequest, TItem>(string name, PluginRpcStreamHandler<TRequest, TItem> handler) => Check(name, handler);

    /// <inheritdoc />
    public ValueTask PublishAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        Check(name, name);
        return ValueTask.CompletedTask;
    }

    private static void Check(string name, object? handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!PluginRpc.IsValidName(name)) throw new ArgumentException("The name is 1 to 64 of a-z, 0-9, '.', '-' and '_', starting with a letter or a digit.", nameof(name));
    }
}
