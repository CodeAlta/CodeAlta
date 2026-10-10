using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>A project as the catalog of CodeAlta names it.</summary>
/// <param name="Id">The identifier of the project: what a session records as its project.</param>
/// <param name="Slug">The short name.</param>
/// <param name="Name">The name to show.</param>
/// <param name="SpaceIds">The spaces the project belongs to today, the default space apart, which holds every project.</param>
internal sealed record ProjectInfo(string Id, string Slug, string Name, IReadOnlyList<string> SpaceIds);

/// <summary>A space: a named group of projects.</summary>
/// <param name="Id">The identifier of the space.</param>
/// <param name="Name">The name.</param>
/// <param name="IsDefault">Whether it is the default space, which holds every project.</param>
internal sealed record SpaceInfo(string Id, string Name, bool IsDefault);

/// <summary>What the statistics need to know of the projects and the spaces: their names, and which projects a space has today.</summary>
internal interface IProjectDirectory
{
    /// <summary>Lists every project, the archived ones too.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The projects.</returns>
    ValueTask<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the spaces.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The spaces.</returns>
    ValueTask<IReadOnlyList<SpaceInfo>> ListSpacesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The directory read through the <c>alta project</c> and <c>alta space</c> commands, which every host has. A read that fails, or finds
/// no project or no space (the commands are not ready at the start of the host), is read again after a few seconds, not after the long time.
/// </summary>
/// <param name="alta">The <c>alta</c> commands of the host.</param>
/// <param name="timeProvider">The clock; the system clock when null.</param>
internal sealed class AltaProjectDirectory(IPluginAltaService alta, TimeProvider? timeProvider = null) : IProjectDirectory
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryTime = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _readAt;
    private bool _complete;
    private IReadOnlyList<ProjectInfo>? _projects;
    private IReadOnlyList<SpaceInfo>? _spaces;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _projects ?? [];
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SpaceInfo>> ListSpacesAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _spaces ?? [];
        }
    }

    private async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_readAt != default && _time.GetUtcNow() - _readAt < (_complete ? CacheTime : RetryTime))
            {
                return;
            }
        }

        var projects = await ReadAsync(["project", "list", "--all", "--include-archived", "--detailed"], "alta.project.item", ParseProject, cancellationToken).ConfigureAwait(false);
        var spaces = await ReadAsync(["space", "list"], "alta.space.item", ParseSpace, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            // What an earlier read found stays when this one failed.
            _projects = projects ?? _projects;
            _spaces = spaces ?? _spaces;
            _complete = projects is { Count: > 0 } && spaces is { Count: > 0 };
            _readAt = _time.GetUtcNow();
        }
    }

    // Null when the command failed, an empty list when it ran and found nothing.
    private async ValueTask<IReadOnlyList<T>?> ReadAsync<T>(string[] args, string type, Func<JsonElement, T?> parse, CancellationToken cancellationToken)
        where T : class
    {
        PluginAltaCommandResult result;
        try
        {
            result = await alta.InvokeAsync(args, null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        var items = new List<T>();
        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var kind) && kind.GetString() == type && parse(root) is { } item)
                {
                    items.Add(item);
                }
            }
            catch (JsonException)
            {
            }
        }

        return items;
    }

    private static ProjectInfo? ParseProject(JsonElement element)
    {
        var id = Text(element, "projectId");
        if (id is null)
        {
            return null;
        }

        var spaces = new List<string>();
        if (element.TryGetProperty("spaces", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            spaces.AddRange(list.EnumerateArray().Select(static item => item.GetString()).OfType<string>());
        }

        return new ProjectInfo(id, Text(element, "slug") ?? id, Text(element, "displayName") ?? Text(element, "name") ?? id, spaces);
    }

    private static SpaceInfo? ParseSpace(JsonElement element)
    {
        var id = Text(element, "id");
        return id is null
            ? null
            : new SpaceInfo(id, Text(element, "name") ?? id, element.TryGetProperty("default", out var isDefault) && isDefault.ValueKind == JsonValueKind.True);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
