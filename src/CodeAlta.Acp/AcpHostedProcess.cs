namespace CodeAlta.Acp;

/// <summary>
/// The process of an agent that another component started, such as the sandbox of a session: its streams, how it ends,
/// and what ends it. A client connected to it (<see cref="AcpClient.ConnectAsync(AcpHostedProcess, InitializeRequest, Action{AcpServerMessage}?, TimeSpan, CancellationToken)"/>)
/// reads the last lines of its standard error to explain an exit, and ends it when it is disposed.
/// </summary>
public sealed class AcpHostedProcess
{
    private const int ErrorTailLines = 40;

    private readonly Queue<string> _errorTail = new();
    private readonly Lock _errorTailLock = new();
    private readonly Task _errors;

    /// <summary>Initializes a new instance of the <see cref="AcpHostedProcess"/> class, and starts reading its standard error.</summary>
    /// <param name="name">The name of the agent, as its exit is told.</param>
    /// <param name="standardOutput">What the agent writes: the protocol.</param>
    /// <param name="standardInput">What the agent reads: the protocol.</param>
    /// <param name="standardError">What the agent writes as errors.</param>
    /// <param name="exited">A task that ends with the exit code of the process.</param>
    /// <param name="end">Ends the process and what it started, and releases what started it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AcpHostedProcess(string name, Stream standardOutput, Stream standardInput, Stream standardError, Task<int> exited, Func<ValueTask> end)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StandardOutput = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        StandardInput = standardInput ?? throw new ArgumentNullException(nameof(standardInput));
        ArgumentNullException.ThrowIfNull(standardError);
        Exited = exited ?? throw new ArgumentNullException(nameof(exited));
        End = end ?? throw new ArgumentNullException(nameof(end));
        _errors = Task.Run(() => ReadErrorsAsync(standardError));
    }

    /// <summary>Gets the name of the agent.</summary>
    public string Name { get; }

    /// <summary>Gets what the agent writes: the protocol.</summary>
    public Stream StandardOutput { get; }

    /// <summary>Gets what the agent reads: the protocol.</summary>
    public Stream StandardInput { get; }

    /// <summary>Gets a task that ends with the exit code of the process.</summary>
    public Task<int> Exited { get; }

    /// <summary>Gets what ends the process.</summary>
    public Func<ValueTask> End { get; }

    /// <summary>Gets the last lines the agent wrote to its standard error.</summary>
    public string ErrorTail
    {
        get
        {
            lock (_errorTailLock)
            {
                return string.Join('\n', _errorTail);
            }
        }
    }

    /// <summary>Waits a little for the process to exit and its errors to be read, then says how it ended.</summary>
    /// <returns>The exit code, when known, and the last lines of its errors.</returns>
    internal string DescribeExit()
    {
        var name = Name;
        try
        {
            Task.WhenAll(Exited, _errors).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Said by what is known below.
        }

        var message = Exited.IsCompletedSuccessfully
            ? $"The ACP agent '{name}' exited with code {Exited.Result}."
            : $"The ACP agent '{name}' closed its output.";
        var tail = ErrorTail.Trim();
        return tail.Length == 0 ? message : $"{message}{Environment.NewLine}{tail}";
    }

    private async Task ReadErrorsAsync(Stream standardError)
    {
        try
        {
            using var reader = new StreamReader(standardError);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_errorTailLock)
                {
                    _errorTail.Enqueue(line.Length > 1000 ? line[..1000] : line);
                    while (_errorTail.Count > ErrorTailLines)
                    {
                        _errorTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process ended.
        }
    }
}
