using XenoAtom.Glob.IO;

namespace CodeAlta.Catalog;

internal sealed class XenoAtomGlobProjectFileSearchTraversal : IProjectFileSearchTraversal
{
    private readonly FileTreeWalker _walker = new();

    public ProjectFileTraversalSnapshot Traverse(
        string projectRoot,
        IReadOnlyDictionary<string, ProjectFileUsageEntry> usageByRelativePath,
        int batchSize,
        Action<IReadOnlyList<ProjectFileSearchItem>> onBatch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(usageByRelativePath);
        ArgumentNullException.ThrowIfNull(onBatch);
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        var normalizedRoot = ProjectFilePathUtilities.NormalizeProjectRoot(projectRoot);
        var items = new List<ProjectFileSearchItem>();
        var batch = new List<ProjectFileSearchItem>(batchSize);
        var walkOptions = ProjectFileTree.CreateWalkOptions(_walker, normalizedRoot, includeDirectories: true, cancellationToken, out var isGitAware);

        foreach (var entry in _walker.Enumerate(normalizedRoot, walkOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = CreateItem(normalizedRoot, entry, usageByRelativePath);
            items.Add(item);
            batch.Add(item);
            if (batch.Count < batchSize)
            {
                continue;
            }

            onBatch(batch.ToArray());
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            onBatch(batch.ToArray());
        }

        return new ProjectFileTraversalSnapshot(isGitAware, items);
    }

    private static ProjectFileSearchItem CreateItem(
        string projectRoot,
        FileTreeEntry entry,
        IReadOnlyDictionary<string, ProjectFileUsageEntry> usageByRelativePath)
    {
        var relativePath = ProjectFilePathUtilities.NormalizeStoredRelativePath(entry.RelativePath);
        var parentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        var extension = entry.IsDirectory ? string.Empty : Path.GetExtension(entry.Name);
        usageByRelativePath.TryGetValue(relativePath, out var usage);

        return new ProjectFileSearchItem
        {
            Kind = entry.IsDirectory ? ProjectFileSearchItemKind.Directory : ProjectFileSearchItemKind.File,
            ProjectRoot = projectRoot,
            RelativePath = relativePath,
            FullPath = entry.FullPath,
            Basename = entry.Name,
            ParentPath = parentPath,
            Extension = extension,
            LastWriteTimeUtc = entry.LastWriteTimeUtc,
            SearchFields = ProjectFilePathUtilities.CreateSearchFields(relativePath, entry.Name, extension),
            Usage = usage,
        };
    }
}
