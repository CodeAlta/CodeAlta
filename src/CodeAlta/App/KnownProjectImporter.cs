using CodeAlta.Agent;
using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class KnownProjectImporter : IKnownProjectImporter
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.App");
    private readonly IAgentSessionCatalog _sessionCatalog;
    private readonly ProjectCatalog _projectCatalog;
    private readonly SemaphoreSlim _importGate = new(initialCount: 1, maxCount: 1);

    public KnownProjectImporter(
        IAgentSessionCatalog sessionCatalog,
        ProjectCatalog projectCatalog)
    {
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(projectCatalog);

        _sessionCatalog = sessionCatalog;
        _projectCatalog = projectCatalog;
    }

    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workingDirectories = new List<string?>();
            await foreach (var session in _sessionCatalog.ListSessionsAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                workingDirectories.Add(session.Context?.Cwd ?? session.WorkspacePath);
            }

            await _projectCatalog.ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import project history from the session catalog.");
        }
        finally
        {
            _importGate.Release();
        }
    }
}
