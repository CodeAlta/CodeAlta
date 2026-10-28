using System.Diagnostics;
using System.Formats.Tar;

namespace CodeAlta.CodexAppServer.Generator;

/// <summary>Where the schema of the protocol was read: a tag of the Codex repository, or a folder.</summary>
/// <param name="SchemaDirectory">The folder that holds the schema files.</param>
/// <param name="SourceRef">The tag or ref of the repository, or <see langword="null"/> for a folder given as it is.</param>
/// <param name="Commit">The commit of <paramref name="SourceRef"/>, or <see langword="null"/>.</param>
/// <param name="TemporaryDirectory">A folder to delete once the generation is done, or <see langword="null"/>.</param>
internal sealed record CodexSchemaSource(string SchemaDirectory, string? SourceRef, string? Commit, string? TemporaryDirectory)
{
    /// <summary>The folder of the schema in the Codex repository, with the separators git uses.</summary>
    public const string RepositorySchemaPath = "codex-rs/app-server-protocol/schema/json";

    /// <summary>Reads the schema the Codex repository checks in at a ref, without a checkout: git writes its files into a temporary folder.</summary>
    /// <param name="repositoryDirectory">The folder of a clone of <c>openai/codex</c>.</param>
    /// <param name="sourceRef">The tag of the pinned release, such as <c>rust-v0.165.0</c>.</param>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    /// <exception cref="DirectoryNotFoundException">The repository folder does not exist.</exception>
    /// <exception cref="InvalidOperationException">git failed, or the ref holds no schema.</exception>
    public static async Task<CodexSchemaSource> FromRepositoryAsync(string repositoryDirectory, string sourceRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        var repository = Path.GetFullPath(repositoryDirectory);
        if (!Directory.Exists(repository))
        {
            throw new DirectoryNotFoundException($"The Codex repository folder '{repository}' does not exist.");
        }

        var commit = (await RunGitAsync(repository, ["rev-parse", "--verify", $"{sourceRef}^{{commit}}"], stdout: null, cancellationToken).ConfigureAwait(false)).Trim();
        var temporary = Path.Combine(Path.GetTempPath(), "codealta-codex-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using (var archive = new MemoryStream())
            {
                await RunGitAsync(repository, ["archive", "--format=tar", commit, "--", RepositorySchemaPath], archive, cancellationToken).ConfigureAwait(false);
                archive.Position = 0;
                await ExtractAsync(archive, temporary, cancellationToken).ConfigureAwait(false);
            }

            // git writes the paths of the archive with '/': each segment is combined for the separators of this system.
            var schemaDirectory = Path.Combine([temporary, .. RepositorySchemaPath.Split('/')]);
            if (!Directory.Exists(schemaDirectory))
            {
                throw new InvalidOperationException($"{sourceRef} has no '{RepositorySchemaPath}' folder.");
            }

            return new CodexSchemaSource(schemaDirectory, sourceRef, commit, temporary);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Reads the schema of a folder as it is.</summary>
    /// <param name="schemaDirectory">The folder that holds <c>codex_app_server_protocol.v2.schemas.json</c>.</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static CodexSchemaSource FromDirectory(string schemaDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaDirectory);
        var directory = Path.GetFullPath(schemaDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The schema folder '{directory}' does not exist.");
        }

        return new CodexSchemaSource(directory, null, null, null);
    }

    /// <summary>Deletes the temporary folder, when there is one.</summary>
    public void Cleanup()
    {
        if (TemporaryDirectory is not null)
        {
            TryDelete(TemporaryDirectory);
        }
    }

    private static async Task ExtractAsync(Stream archive, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination);
        await using var reader = new TarReader(archive);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine([root, .. entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries)]));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The archive of the schema holds a path outside its folder: {entry.Name}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await entry.ExtractToFileAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> RunGitAsync(string repository, IReadOnlyList<string> arguments, Stream? stdout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repository);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        string output = string.Empty;
        if (stdout is null)
        {
            output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        }

        return output;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is harmless.
        }
    }
}
