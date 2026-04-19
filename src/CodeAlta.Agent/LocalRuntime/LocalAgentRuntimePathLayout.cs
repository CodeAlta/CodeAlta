namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Provides the filesystem layout for local raw-API providers and session journals.
/// </summary>
public sealed class LocalAgentRuntimePathLayout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LocalAgentRuntimePathLayout"/> class.
    /// </summary>
    /// <param name="rootPath">Runtime root path, typically <c>~/.alta</c>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootPath" /> is empty.</exception>
    public LocalAgentRuntimePathLayout(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    /// <summary>
    /// Gets the runtime root path.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the providers root path.
    /// </summary>
    public string ProvidersRootPath => Path.Combine(RootPath, "providers");

    /// <summary>
    /// Gets the sessions root path.
    /// </summary>
    public string SessionsRootPath => Path.Combine(RootPath, "sessions");

    /// <summary>
    /// Gets the provider root path.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <returns>The provider root path.</returns>
    public string GetProviderRootPath(string protocolFamily, string providerKey)
        => Path.Combine(ProvidersRootPath, NormalizeSegment(protocolFamily), NormalizeSegment(providerKey));

    /// <summary>
    /// Gets the provider descriptor path.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <returns>The provider descriptor path.</returns>
    public string GetProviderDescriptorPath(string protocolFamily, string providerKey)
        => Path.Combine(GetProviderRootPath(protocolFamily, providerKey), "provider.json");

    /// <summary>
    /// Gets the shared sessions root path.
    /// </summary>
    /// <param name="protocolFamily">Protocol family.</param>
    /// <param name="providerKey">Provider key.</param>
    /// <returns>The shared sessions root path.</returns>
    public string GetProviderSessionsRootPath(string protocolFamily, string providerKey)
        => SessionsRootPath;

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
