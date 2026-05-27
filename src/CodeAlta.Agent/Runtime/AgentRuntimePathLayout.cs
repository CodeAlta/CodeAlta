namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Provides the filesystem layout for local raw-API session journals.
/// </summary>
public sealed class AgentRuntimePathLayout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntimePathLayout"/> class.
    /// </summary>
    /// <param name="rootPath">Runtime root path, typically <c>~/.alta</c>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootPath" /> is empty.</exception>
    public AgentRuntimePathLayout(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    /// <summary>
    /// Gets the runtime root path.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the sessions root path.
    /// </summary>
    public string SessionsRootPath => Path.Combine(RootPath, "sessions");

    /// <summary>
    /// Gets the root directory for optional provider protocol traces.
    /// </summary>
    public string SessionTracesRootPath => Path.Combine(SessionsRootPath, "traces");

    /// <summary>
    /// Gets the session journal path.
    /// </summary>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="createdAt">Creation timestamp used for date sharding.</param>
    /// <returns>The session journal path.</returns>
    public string GetSessionFilePath(
        string sessionId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Path.Combine(
            SessionsRootPath,
            createdAt.UtcDateTime.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("dd", System.Globalization.CultureInfo.InvariantCulture),
            $"{NormalizeSegment(sessionId)}.jsonl");
    }

    /// <summary>
    /// Gets the optional provider protocol trace path for a local session.
    /// </summary>
    /// <param name="sessionId">Local session identifier.</param>
    /// <returns>The session protocol trace path.</returns>
    public string GetSessionTraceFilePath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Path.Combine(SessionTracesRootPath, $"{NormalizeSegment(sessionId)}.trace");
    }

    private static string NormalizeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        return string.Concat(trimmed.Select(static c =>
            Path.GetInvalidFileNameChars().Contains(c) || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar
                ? '_'
                : c));
    }
}
