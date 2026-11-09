using CodeAlta.Core.IO;
using CodeAlta.ModelServers.HuggingFace;

namespace CodeAlta.ModelServers.Store;

/// <summary>
/// The models of a machine, kept in the layout of the Hugging Face cache that llama.cpp (<c>-hf</c>), the <c>hf</c> tool and
/// <c>huggingface_hub</c> share: <c>models--org--name/refs/main</c> names a commit, <c>blobs/&lt;sha256&gt;</c> holds each file once,
/// and <c>snapshots/&lt;commit&gt;/&lt;path&gt;</c> links to it (or is a copy where links cannot be made, as llama.cpp does on Windows).
/// Writers take the lock of a blob under <c>.locks/</c>, as those tools do.
/// </summary>
public sealed class ModelStore
{
    /// <summary>Creates the store of a folder.</summary>
    /// <param name="root">The folder of the hub cache.</param>
    public ModelStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the folder.</summary>
    public string Root { get; }

    /// <summary>The Hugging Face cache of this user, as llama.cpp finds it: <c>HF_HUB_CACHE</c>, then <c>$HF_HOME/hub</c>, then <c>~/.cache/huggingface/hub</c>.</summary>
    /// <returns>Its folder.</returns>
    public static string DefaultRoot()
    {
        if (Environment.GetEnvironmentVariable("HF_HUB_CACHE") is { Length: > 0 } hub) return hub;
        if (Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_CACHE") is { Length: > 0 } legacy) return legacy;
        if (Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } home) return Path.Combine(home, "hub");
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg) return Path.Combine(xdg, "huggingface", "hub");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub");
    }

    /// <summary>The folder of a repository: <c>models--org--name</c>.</summary>
    /// <param name="repo">The repository.</param>
    /// <returns>Its folder.</returns>
    public string RepoFolder(string repo)
    {
        HuggingFaceClient.ValidateRepo(repo);
        return Path.Combine(Root, "models--" + repo.Replace("/", "--", StringComparison.Ordinal));
    }

    /// <summary>Lists the models on disk.</summary>
    /// <returns>The models, the largest first.</returns>
    public IReadOnlyList<StoredModel> Scan()
    {
        var result = new List<StoredModel>();
        if (!Directory.Exists(Root)) return result;
        foreach (var folder in Directory.EnumerateDirectories(Root, "models--*"))
        {
            var name = Path.GetFileName(folder)["models--".Length..];
            var separator = name.IndexOf("--", StringComparison.Ordinal);
            if (separator <= 0) continue;
            var repo = name[..separator] + "/" + name[(separator + 2)..];
            try
            {
                result.AddRange(ScanRepo(folder, repo));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A repository another tool writes now is read again at the next scan.
            }
        }

        return result.OrderByDescending(static model => model.SizeBytes).ToList();
    }

    /// <summary>Finds a model on disk.</summary>
    /// <param name="id">Its id, <c>org/repo:TAG</c>.</param>
    /// <returns>The model, or null.</returns>
    public StoredModel? Find(string id)
    {
        var (repo, tag) = ParseId(id);
        var folder = RepoFolder(repo);
        return Directory.Exists(folder) ? ScanRepo(folder, repo).FirstOrDefault(model => string.Equals(model.Tag, tag, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>Reads a model id: <c>org/repo:TAG</c>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Its repository and tag.</returns>
    /// <exception cref="ModelHostException">It is not one.</exception>
    public static (string Repo, string Tag) ParseId(string id)
    {
        var colon = id?.LastIndexOf(':') ?? -1;
        if (colon <= 0 || colon == id!.Length - 1) throw new ModelHostException("invalid", $"'{id}' is not a model (org/repo:TAG).");
        var repo = id[..colon];
        HuggingFaceClient.ValidateRepo(repo);
        var tag = id[(colon + 1)..];
        if (tag.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))) throw new ModelHostException("invalid", $"'{tag}' is not a quantization.");
        return (repo, tag.ToUpperInvariant());
    }

    private static bool IsInside(string folder, string path)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Every snapshot of a repository: a quantization downloaded at an older revision stays with it while another one moves
    // main. A quantization in several snapshots is the one of main, else the newest.
    private List<StoredModel> ScanRepo(string folder, string repo)
    {
        var refs = Path.Combine(folder, "refs", "main");
        var commit = File.Exists(refs) ? File.ReadAllText(refs).Trim() : null;
        var snapshots = Path.Combine(folder, "snapshots");
        if (!Directory.Exists(snapshots)) return [];
        var ordered = Directory.EnumerateDirectories(snapshots)
            .OrderByDescending(snapshot => string.Equals(Path.GetFileName(snapshot), commit, StringComparison.Ordinal))
            .ThenByDescending(Directory.GetLastWriteTimeUtc).ToList();
        var scanned = ordered.Select(snapshot => (Snapshot: snapshot, Entries: ReadSnapshot(snapshot))).ToList();
        // How many quantizations of every snapshot use each file: what removing one frees.
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, entries) in scanned)
        {
            foreach (var quant in GgufNames.Quantizations(entries.Select(static entry => entry.File).ToList()))
            {
                foreach (var identity in quant.Files.Select(file => entries.First(entry => entry.File.Path == file.Path).Identity).Distinct())
                    uses[identity] = uses.GetValueOrDefault(identity) + 1;
            }
        }

        var models = new List<StoredModel>();
        foreach (var (snapshot, entries) in scanned)
        {
            foreach (var model in ModelsOf(snapshot, entries, repo, uses))
            {
                if (!models.Any(known => string.Equals(known.Tag, model.Tag, StringComparison.OrdinalIgnoreCase))) models.Add(model);
            }
        }

        return models;
    }

    private static List<(HubFile File, string Identity, string Full, DateTime Modified)> ReadSnapshot(string snapshot)
    {
        var entries = new List<(HubFile File, string Identity, string Full, DateTime Modified)>();
        foreach (var path in Directory.EnumerateFiles(snapshot, "*.gguf", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(snapshot, path).Replace('\\', '/');
            string identity = path;
            long size;
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not FileInfo { Exists: true } blob) continue; // a link to a blob not there yet
                identity = blob.FullName;
                size = blob.Length;
            }
            else
            {
                size = info.Length;
            }

            entries.Add((new HubFile(relative, size, null), identity, path, info.LastWriteTimeUtc));
        }

        return entries;
    }

    private static List<StoredModel> ModelsOf(string snapshot, List<(HubFile File, string Identity, string Full, DateTime Modified)> entries, string repo,
        Dictionary<string, int> uses)
    {
        var revision = Path.GetFileName(snapshot);
        var quants = GgufNames.Quantizations(entries.Select(static entry => entry.File).ToList());
        var models = new List<StoredModel>();
        foreach (var quant in quants)
        {
            var mine = quant.Files.Select(file => entries.First(entry => entry.File.Path == file.Path)).ToList();
            var unique = mine.DistinctBy(static entry => entry.Identity).ToList();
            var size = unique.Sum(static entry => entry.File.Size);
            var freed = unique.Where(entry => uses.GetValueOrDefault(entry.Identity) <= 1).Sum(static entry => entry.File.Size);
            var stored = mine.Select(entry => new StoredFile(entry.File.Path, entry.File.Size, GgufNames.IsMmproj(entry.File.Path) ? "mmproj" : "model",
                uses.GetValueOrDefault(entry.Identity) > 1, entry.Identity == entry.Full ? null : Path.GetFileName(entry.Identity))).ToList();
            var main = mine.First(entry => !GgufNames.IsMmproj(entry.File.Path));
            var mmproj = mine.FirstOrDefault(entry => GgufNames.IsMmproj(entry.File.Path));
            models.Add(new StoredModel($"{repo}:{quant.Tag}", repo, quant.Tag, revision, stored, size, freed, quant.Vision, "ready",
                main.Full, mmproj.Full, mine.Max(static entry => entry.Modified)));
        }

        return models;
    }

    /// <summary>Removes a model: its files, and the blobs no other model uses. A repository left empty goes too.</summary>
    /// <param name="id">The model.</param>
    /// <returns>The bytes freed.</returns>
    /// <exception cref="ModelHostException">The model is not on disk, or a file is in use.</exception>
    public long Remove(string id)
    {
        var model = Find(id) ?? throw new ModelHostException("not_found", $"{id} is not on this machine.");
        var folder = RepoFolder(model.Repo);
        var snapshot = Path.Combine(folder, "snapshots", model.Revision);
        var others = ScanRepo(folder, model.Repo).Where(other => other.Id != model.Id).SelectMany(static other => other.Files).Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        long freed = 0;
        foreach (var file in model.Files.Where(file => !others.Contains(file.Path)))
        {
            var path = Path.Combine(snapshot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            var blob = info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo : null;
            using (AcquireLock(model.Repo, blob?.Name ?? file.Path.Replace('/', '_')))
            {
                try
                {
                    File.Delete(path);
                    // Only a blob of this repository's cache goes: a link to a file elsewhere (an original on another disk)
                    // loses its link, never its target.
                    if (blob is { Exists: true } && IsInside(Path.Combine(folder, "blobs"), blob.FullName) && !IsLinkedElsewhere(folder, blob.FullName, path)) blob.Delete();
                    freed += file.Size;
                }
                catch (IOException exception)
                {
                    throw new ModelHostException("in_use", $"{file.Path} is in use: stop what reads it, then remove it again ({exception.Message}).");
                }
            }
        }

        var snapshots = Path.Combine(folder, "snapshots");
        DeleteEmptyFolders(snapshots);
        if (!Directory.Exists(snapshots) || !Directory.EnumerateFiles(snapshots, "*", SearchOption.AllDirectories).Any())
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return freed;
    }

    /// <summary>Puts a downloaded and checked file in its place: its blob, its entry in the snapshot of its revision, and the ref of main.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="revision">The commit.</param>
    /// <param name="path">The path of the file in the repository.</param>
    /// <param name="partPath">The downloaded file; it is moved.</param>
    /// <param name="sha256">Its SHA-256, which names its blob.</param>
    public void Place(string repo, string revision, string path, string partPath, string sha256)
    {
        var folder = RepoFolder(repo);
        var blob = Path.Combine(folder, "blobs", sha256);
        var entry = Path.Combine(folder, "snapshots", revision, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        using (AcquireLock(repo, sha256))
        {
            if (File.Exists(blob)) File.Delete(partPath);
            else File.Move(partPath, blob);
            if (File.Exists(entry) || new FileInfo(entry).LinkTarget is not null) File.Delete(entry);
            try
            {
                File.CreateSymbolicLink(entry, Path.GetRelativePath(Path.GetDirectoryName(entry)!, blob));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // No right to make links (Windows without Developer Mode): the file itself, as llama.cpp does. A blob
                // that another entry already uses is copied, so that both stay.
                if (IsLinkedElsewhere(folder, blob, entry)) File.Copy(blob, entry);
                else File.Move(blob, entry);
            }
        }

        var refs = Path.Combine(folder, "refs", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(refs)!);
        SafeFile.WriteAllText(refs, revision);
    }

    /// <summary>Whether the blob of a file is in the cache of a repository already: placed by another download.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="sha256">The SHA-256 of the file.</param>
    /// <returns>Whether it is.</returns>
    public bool HasBlob(string repo, string sha256) => File.Exists(Path.Combine(RepoFolder(repo), "blobs", sha256));

    /// <summary>The file a download writes before it is checked: <c>blobs/&lt;sha256&gt;.incomplete</c>, the name huggingface_hub uses.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="sha256">The SHA-256 of the file.</param>
    /// <returns>Its path.</returns>
    public string PartPath(string repo, string sha256) => Path.Combine(RepoFolder(repo), "blobs", sha256 + ".incomplete");

    /// <summary>The bytes free on the disk of the store.</summary>
    /// <returns>The free bytes, or 0 when not known.</returns>
    public long FreeBytes()
    {
        try
        {
            var existing = Root;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private IDisposable AcquireLock(string repo, string name)
    {
        var path = Path.Combine(Root, ".locks", "models--" + repo.Replace("/", "--", StringComparison.Ordinal), name + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (LockFile.TryAcquire(path, out var held)) return held;
            Thread.Sleep(50);
        }

        throw new ModelHostException("in_use", $"Another program writes {repo} now: try again in a moment.");
    }

    private static bool IsLinkedElsewhere(string folder, string blob, string except)
    {
        var snapshots = Path.Combine(folder, "snapshots");
        if (!Directory.Exists(snapshots)) return false;
        foreach (var path in Directory.EnumerateFiles(snapshots, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(path, except, StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                && string.Equals(target.FullName, blob, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void DeleteEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var child in Directory.EnumerateDirectories(folder)) DeleteEmptyFolders(child);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
