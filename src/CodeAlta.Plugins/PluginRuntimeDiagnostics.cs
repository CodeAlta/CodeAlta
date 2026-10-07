using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Stores plugin runtime diagnostics separately from conversation history.
/// </summary>
public sealed class PluginRuntimeDiagnosticStore
{
    /// <summary>The most diagnostics kept. Beyond it the oldest failures of callbacks go first, then the oldest of all.</summary>
    public const int MaximumCount = 2048;

    private readonly object _gate = new();
    private readonly List<PluginRuntimeDiagnostic> _diagnostics = [];

    /// <summary>
    /// Adds a diagnostic.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    public void Add(PluginRuntimeDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        lock (_gate)
        {
            _diagnostics.Add(diagnostic);
            Trim();
        }
    }

    // A callback that fails each time the host asks (a status item, every few seconds) must not grow the store
    // for as long as the application runs. What the start and the builds said is kept the longest.
    private void Trim()
    {
        var excess = _diagnostics.Count - MaximumCount;
        if (excess <= 0) return;
        // A quarter at once, so that a full store is not trimmed at each addition.
        var remove = excess + MaximumCount / 4;
        _diagnostics.RemoveAll(diagnostic => diagnostic.Source == PluginRuntimeDiagnosticSource.Callback && remove-- > 0);
        if (_diagnostics.Count > MaximumCount) _diagnostics.RemoveRange(0, _diagnostics.Count - MaximumCount);
    }

    /// <summary>
    /// Adds diagnostics.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    public void AddRange(IEnumerable<PluginRuntimeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        lock (_gate)
        {
            _diagnostics.AddRange(diagnostics.Where(static diagnostic => diagnostic is not null));
            Trim();
        }
    }

    /// <summary>
    /// Gets a snapshot of stored diagnostics.
    /// </summary>
    /// <returns>Diagnostics ordered by timestamp and insertion order.</returns>
    public IReadOnlyList<PluginRuntimeDiagnostic> GetSnapshot()
    {
        lock (_gate)
        {
            return _diagnostics.ToArray();
        }
    }

    /// <summary>
    /// Gets diagnostics for a package id.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>Matching diagnostics.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="packageId"/> is empty.</exception>
    public IReadOnlyList<PluginRuntimeDiagnostic> GetByPackage(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_gate)
        {
            return _diagnostics
                .Where(diagnostic => string.Equals(diagnostic.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Gets diagnostics by runtime diagnostic source.
    /// </summary>
    /// <param name="source">The diagnostic source.</param>
    /// <returns>Matching diagnostics.</returns>
    public IReadOnlyList<PluginRuntimeDiagnostic> GetBySource(PluginRuntimeDiagnosticSource source)
    {
        lock (_gate)
        {
            return _diagnostics.Where(diagnostic => diagnostic.Source == source).ToArray();
        }
    }

    /// <summary>
    /// Gets diagnostics at or above a severity.
    /// </summary>
    /// <param name="minimumSeverity">The minimum severity.</param>
    /// <returns>Matching diagnostics.</returns>
    public IReadOnlyList<PluginRuntimeDiagnostic> GetByMinimumSeverity(PluginDiagnosticSeverity minimumSeverity)
    {
        lock (_gate)
        {
            return _diagnostics.Where(diagnostic => diagnostic.Severity >= minimumSeverity).ToArray();
        }
    }

    /// <summary>
    /// Removes the diagnostics a condition selects: those of a package whose plugins are replaced.
    /// </summary>
    /// <param name="match">Says whether a diagnostic is removed.</param>
    /// <returns>The number of diagnostics removed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="match"/> is <see langword="null"/>.</exception>
    public int RemoveWhere(Predicate<PluginRuntimeDiagnostic> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            return _diagnostics.RemoveAll(match);
        }
    }

    /// <summary>
    /// Clears all stored diagnostics.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _diagnostics.Clear();
        }
    }
}
