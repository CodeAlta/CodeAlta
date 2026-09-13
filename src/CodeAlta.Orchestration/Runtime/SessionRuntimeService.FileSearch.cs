using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>Gets the optional borrowed shared cache for live original-event invalidation.</summary>
    /// <remarks>The supplying owner retains this concrete, non-disposable cache through actual runtime
    /// closure. Null leaves standalone runtimes without a search cache. This is not a plugin callback seam.</remarks>
    internal ProjectFileSnapshotCache? FileSearchCache { get; init; }

    private async ValueTask InvalidateFileSearchCacheAsync(AgentEvent? publishedEvent, string workingDirectory)
    {
        if (FileSearchCache is not { } cache || string.IsNullOrWhiteSpace(workingDirectory)
            || publishedEvent is not (AgentActivityEvent { Kind: AgentActivityKind.FileChange }
                or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated })) return;

        try
        {
            // The sealed cache performs a local synchronous update. Do not substitute arbitrary services.
            await cache.MarkDirtyAsync(workingDirectory, ProjectFileInvalidationReason.FileSystemWrite).ConfigureAwait(false);
        }
        catch
        {
            // Cache dirty marking remains best effort; this catch does not own any other event work.
        }
    }
}
