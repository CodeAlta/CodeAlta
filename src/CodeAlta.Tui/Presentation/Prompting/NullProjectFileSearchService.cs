using CodeAlta.Catalog;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed class NullProjectFileSearchService : IProjectFileSearchService
{
    public static readonly NullProjectFileSearchService Instance = new();

    private NullProjectFileSearchService()
    {
    }

    public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
        ProjectFileSearchSessionOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Search sessions are not available from the null project-file search service.");

    public ValueTask<ProjectFileResolution> ResolveAsync(
        ProjectFileResolveQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ValueTask.FromResult(
            new ProjectFileResolution(
                IsResolved: false,
                NormalizedReferenceText: query.ReferenceText,
                Item: null,
                query.LineRange));
    }

    public ValueTask RecordUsageAsync(
        ProjectFileUsageEvent usageEvent,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask InvalidateAsync(
        string projectRoot,
        ProjectFileInvalidationReason reason,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
