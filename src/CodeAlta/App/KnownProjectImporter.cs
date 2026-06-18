using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
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

    public async Task<bool> ImportAsync(CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var beforeImport = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var workingDirectories = new List<string?>();
            await foreach (var session in _sessionCatalog.ListSessionsAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                workingDirectories.Add(session.Context?.Cwd ?? session.WorkspacePath);
            }

            await _projectCatalog.ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
            var afterImport = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            return HasChanged(beforeImport, afterImport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentSessionCacheLockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to import project history from the session catalog.");
            return false;
        }
        finally
        {
            _importGate.Release();
        }
    }

    private static bool HasChanged(IReadOnlyList<ProjectDescriptor> beforeImport, IReadOnlyList<ProjectDescriptor> afterImport)
    {
        if (beforeImport.Count != afterImport.Count)
        {
            return true;
        }

        foreach (var before in beforeImport)
        {
            var after = afterImport.FirstOrDefault(project =>
                string.Equals(project.Id, before.Id, StringComparison.OrdinalIgnoreCase));
            if (after is null ||
                before.Archived != after.Archived ||
                !string.Equals(NormalizePath(before.ProjectPath), NormalizePath(after.ProjectPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
