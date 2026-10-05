using System.Text.Json;
using CodeAlta.Plugins;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// What the start-up screen says below its progress bar while the host starts: nothing in the usual start,
/// and the plugin being built when a source plugin is new or has changed.
/// </summary>
/// <remarks>
/// The start-up screen is a document without a bridge to the host: it reads this status as a file of the
/// application (<see cref="DocumentPath"/>) a few times a second.
/// </remarks>
internal sealed class DesktopStartupStatus
{
    /// <summary>The path of the status document in the application's scheme.</summary>
    internal const string DocumentPath = "/startup-status.json";

    private readonly Lock _gate = new();
    private string _text = string.Empty;

    /// <summary>Gets or sets the status line; empty for none.</summary>
    internal string Text
    {
        get { lock (_gate) return _text; }
        set { lock (_gate) _text = value ?? string.Empty; }
    }

    /// <summary>The status as the JSON document the start-up screen reads.</summary>
    internal byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("text", Text);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

/// <summary>
/// The application's files, plus the start-up status document.
/// </summary>
internal sealed class DesktopStartupResources(INeoResourceProvider assets, DesktopStartupStatus status) : INeoResourceProvider
{
    private readonly INeoResourceProvider _assets = assets ?? throw new ArgumentNullException(nameof(assets));
    private readonly DesktopStartupStatus _status = status ?? throw new ArgumentNullException(nameof(status));

    /// <inheritdoc />
    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(request.Uri.AbsolutePath, DesktopStartupStatus.DocumentPath, StringComparison.Ordinal)
            ? NeoResourceResponse.FromBytes(_status.ToJson(), "application/json")
            : _assets.GetResponse(request);
    }
}

/// <summary>
/// Shows on the start-up screen which source plugins are being built. A start where every plugin is up to
/// date shows nothing.
/// </summary>
internal sealed class DesktopPluginStartupFeedback(DesktopStartupStatus status) : IPluginStartupFeedback
{
    private readonly DesktopStartupStatus _status = status ?? throw new ArgumentNullException(nameof(status));

    /// <inheritdoc />
    public async ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests, bool waitForAcknowledgement,
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,
        Func<T, TimeSpan, string> summaryFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(summaryFactory);
        try
        {
            return await operation(new Progress(_status), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _status.Text = string.Empty;
        }
    }

    /// <summary>The status line for the plugins being built; empty when none is.</summary>
    internal static string Describe(IReadOnlyCollection<string> building, int built, int total)
    {
        if (building.Count == 0) return string.Empty;
        // One plugin is named with its place in the run; several built at once are counted.
        if (building.Count == 1) return total > 1 ? $"Building plugin {building.First()} ({Math.Min(total, built + 1)} of {total})…" : $"Building plugin {building.First()}…";
        return built > 0 ? $"Building {total} plugins ({built} done)…" : $"Building {Math.Max(total, building.Count)} plugins…";
    }

    private sealed class Progress(DesktopStartupStatus status) : IPluginStartupProgress
    {
        private readonly Lock _gate = new();
        private readonly List<string> _building = [];
        private int _built;
        private bool _anyBuilt;

        public void MarkPreparing() { }

        public void MarkBuilding() { }

        public void Report(PluginBuildProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            lock (_gate)
            {
                var id = progress.Package.PackageId;
                switch (progress.State)
                {
                    case PluginBuildProgressState.Running:
                        if (!_building.Contains(id, StringComparer.OrdinalIgnoreCase)) _building.Add(id);
                        _anyBuilt = true;
                        break;
                    case PluginBuildProgressState.Succeeded or PluginBuildProgressState.Failed or PluginBuildProgressState.UpToDate:
                        _building.RemoveAll(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
                        _built++;
                        break;
                }

                status.Text = Describe(_building, _built, progress.Total);
            }
        }

        public void MarkBuildsCompleted() { }

        public void MarkActivating()
        {
            lock (_gate) status.Text = _anyBuilt ? "Starting plugins…" : string.Empty;
        }
    }
}
