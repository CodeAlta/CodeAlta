using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// What a relocation of a transcript of Claude Code did.
/// </summary>
public enum ClaudeCodeTranscriptRelocationStatus
{
    /// <summary>The transcript was moved to the folder of the new working directory.</summary>
    Moved,

    /// <summary>No folder of the profile holds the transcript: nothing was done.</summary>
    NothingToMove,

    /// <summary>Both working directories use the same folder: nothing was done.</summary>
    SameFolder,

    /// <summary>
    /// The folder of the new working directory already holds the transcript and keeps it; a copy left elsewhere was
    /// removed, or set aside when it differed.
    /// </summary>
    AlreadyThere,

    /// <summary>Several other folders hold the transcript: none was moved.</summary>
    Ambiguous,

    /// <summary>The files could not be moved: what was copied was removed and the source was kept.</summary>
    Failed,
}

/// <summary>
/// The outcome of a relocation of a transcript of Claude Code.
/// </summary>
/// <param name="Status">What was done.</param>
/// <param name="SourcePath">The transcript that was found, when one was.</param>
/// <param name="TargetPath">The transcript in the folder of the new working directory, when there is one.</param>
/// <param name="BackupPath">The copy that was set aside because it differed from the one kept, when there is one.</param>
/// <param name="Message">What went wrong or what was left behind, when something was.</param>
public sealed record ClaudeCodeTranscriptRelocationResult(
    ClaudeCodeTranscriptRelocationStatus Status,
    string? SourcePath = null,
    string? TargetPath = null,
    string? BackupPath = null,
    string? Message = null)
{
    /// <summary>
    /// Gets whether the CLI started in the new working directory finds the transcript in its own folder.
    /// </summary>
    public bool IsAtTarget => Status is ClaudeCodeTranscriptRelocationStatus.Moved
        or ClaudeCodeTranscriptRelocationStatus.AlreadyThere
        or ClaudeCodeTranscriptRelocationStatus.SameFolder;
}

/// <summary>
/// Moves the transcript of a conversation of Claude Code to the folder of another working directory, so that
/// <c>--resume</c> finds it when the CLI is started there.
/// </summary>
/// <remarks>
/// The CLI looks for a conversation in the folder of its working directory first, then accepts one copy in another
/// folder: two copies elsewhere make it fail. A relocation therefore removes the source once the copy is complete,
/// and never leaves two copies of the same name. The transcript <c>&lt;id&gt;.jsonl</c> moves with its companion
/// folder <c>&lt;id&gt;/</c> (tool results, subagents); the <c>memory</c> folder of a project and the other folders
/// of the profile stay where they are. The CLI must not run the conversation while it is moved. Problems of the file
/// system are reported in the result, never thrown: a transcript that is not found makes CodeAlta start a new
/// conversation of the CLI with the history as context.
/// </remarks>
public static class ClaudeCodeTranscriptRelocation
{
    private const string TranscriptExtension = ".jsonl";
    private const int BufferSize = 81920;

    /// <summary>
    /// Reads the identifier of the conversation of the CLI from the provider state a session of Claude Code records.
    /// </summary>
    /// <param name="providerState">The provider state of the session, as CodeAlta stores it.</param>
    /// <returns>The identifier, or null when the state is not one of Claude Code or names no conversation.</returns>
    public static string? ReadCliSessionId(JsonElement? providerState) => ClaudeCodeProviderState.Read(providerState)?.SessionId;

    /// <summary>
    /// Moves a transcript from the folder of a working directory to the folder of another one.
    /// </summary>
    /// <param name="configRoot">The configuration folder of Claude Code (see <see cref="ClaudeCodeProjectFolders.ResolveConfigRoot()"/>).</param>
    /// <param name="cliSessionId">The identifier of the conversation of the CLI.</param>
    /// <param name="fromWorkingDirectory">The working directory the CLI ran in.</param>
    /// <param name="toWorkingDirectory">The working directory the CLI will run in.</param>
    /// <param name="cancellationToken">Cancels the copy; what was copied is removed.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentException">An argument is blank, or the identifier is not a plain file name.</exception>
    /// <exception cref="OperationCanceledException">The copy was canceled.</exception>
    public static Task<ClaudeCodeTranscriptRelocationResult> RelocateAsync(
        string configRoot,
        string cliSessionId,
        string fromWorkingDirectory,
        string toWorkingDirectory,
        CancellationToken cancellationToken)
        => RelocateAsync(configRoot, cliSessionId, fromWorkingDirectory, toWorkingDirectory, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Moves a transcript from the folder of a working directory to the folder of another one.
    /// </summary>
    /// <param name="configRoot">The configuration folder of Claude Code (see <see cref="ClaudeCodeProjectFolders.ResolveConfigRoot()"/>).</param>
    /// <param name="cliSessionId">The identifier of the conversation of the CLI.</param>
    /// <param name="fromWorkingDirectory">The working directory the CLI ran in.</param>
    /// <param name="toWorkingDirectory">The working directory the CLI will run in.</param>
    /// <param name="timeProvider">Gives the time in the name of a copy set aside.</param>
    /// <param name="cancellationToken">Cancels the copy; what was copied is removed.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is null.</exception>
    /// <exception cref="ArgumentException">An argument is blank, or the identifier is not a plain file name.</exception>
    /// <exception cref="OperationCanceledException">The copy was canceled.</exception>
    public static async Task<ClaudeCodeTranscriptRelocationResult> RelocateAsync(
        string configRoot,
        string cliSessionId,
        string fromWorkingDirectory,
        string toWorkingDirectory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateSessionId(cliSessionId);
        var projects = ClaudeCodeProjectFolders.GetProjectsFolder(configRoot);
        var fromName = ClaudeCodeProjectFolders.GetFolderName(fromWorkingDirectory);
        var toName = ClaudeCodeProjectFolders.GetFolderName(toWorkingDirectory);
        if (ClaudeCodeProjectFolders.AreSameFolderName(fromName, toName))
        {
            return new ClaudeCodeTranscriptRelocationResult(ClaudeCodeTranscriptRelocationStatus.SameFolder);
        }

        var fileName = cliSessionId + TranscriptExtension;
        var targetFolder = Path.Combine(projects, toName);
        var target = Path.Combine(targetFolder, fileName);
        var targetCompanion = Path.Combine(targetFolder, cliSessionId);
        var undo = new Stack<Action>();
        string? source = null;
        try
        {
            var sources = FindSources(projects, fromName, toName, fileName);
            if (sources.Count == 0)
            {
                return File.Exists(target)
                    ? new ClaudeCodeTranscriptRelocationResult(ClaudeCodeTranscriptRelocationStatus.AlreadyThere, TargetPath: target)
                    : new ClaudeCodeTranscriptRelocationResult(ClaudeCodeTranscriptRelocationStatus.NothingToMove);
            }

            if (sources.Count > 1)
            {
                return new ClaudeCodeTranscriptRelocationResult(
                    ClaudeCodeTranscriptRelocationStatus.Ambiguous,
                    Message: $"The conversation {cliSessionId} is in several folders of Claude Code: {string.Join(", ", sources)}.");
            }

            source = sources[0];
            var sourceFolder = Path.GetDirectoryName(source)!;
            // Two names can be one folder: a link (users link the folders of the worktrees of one repository so that
            // they share their conversations), a junction, another case. Moved onto itself, the transcript would be
            // taken for a copy already there, and the source deleted: the only one.
            if (IsSameFolder(sourceFolder, targetFolder))
            {
                return new ClaudeCodeTranscriptRelocationResult(ClaudeCodeTranscriptRelocationStatus.SameFolder, source, source);
            }

            var sourceCompanion = Path.Combine(sourceFolder, cliSessionId);
            var stamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string? backup = null;
            if (File.Exists(target))
            {
                if (await HaveSameContentAsync(source, target, cancellationToken).ConfigureAwait(false))
                {
                    // The copy is already there: what the source has of the companion folder joins it.
                    await MergeFolderAsync(sourceCompanion, targetCompanion, undo, cancellationToken).ConfigureAwait(false);
                    File.Delete(source);
                    undo.Clear();
                    return new ClaudeCodeTranscriptRelocationResult(
                        ClaudeCodeTranscriptRelocationStatus.AlreadyThere, source, target, Message: DeleteLeftover(sourceCompanion));
                }

                if (new FileInfo(source).Length <= new FileInfo(target).Length)
                {
                    // The copy at the target holds more: the source is set aside beside itself.
                    backup = UniquePath(source + ".bak-" + stamp);
                    MoveFile(source, backup, undo);
                    if (Directory.Exists(sourceCompanion))
                    {
                        MoveDirectory(sourceCompanion, UniquePath(sourceCompanion + ".bak-" + stamp), undo);
                    }

                    undo.Clear();
                    return new ClaudeCodeTranscriptRelocationResult(
                        ClaudeCodeTranscriptRelocationStatus.AlreadyThere, source, target, backup,
                        $"The folder of the new working directory had a longer copy of the conversation: it was kept and the other one was set aside as {backup}.");
                }
            }

            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
                undo.Push(() => Directory.Delete(targetFolder));
            }

            var temporary = target + ".tmp";
            File.Delete(temporary);
            await CopyFileAsync(source, temporary, undo, cancellationToken).ConfigureAwait(false);
            if (File.Exists(target))
            {
                // The source holds more: the copy at the target is set aside beside itself.
                backup = UniquePath(target + ".bak-" + stamp);
                MoveFile(target, backup, undo);
                if (Directory.Exists(targetCompanion))
                {
                    MoveDirectory(targetCompanion, UniquePath(targetCompanion + ".bak-" + stamp), undo);
                }
            }

            await MergeFolderAsync(sourceCompanion, targetCompanion, undo, cancellationToken).ConfigureAwait(false);
            MoveFile(temporary, target, undo);

            // The copy is complete: removing the source is what makes it the only one.
            File.Delete(source);
            undo.Clear();
            var leftover = DeleteLeftover(sourceCompanion);
            var message = backup is null
                ? leftover
                : $"The folder of the new working directory had a shorter copy of the conversation: it was set aside as {backup}.{(leftover is null ? string.Empty : " " + leftover)}";
            return new ClaudeCodeTranscriptRelocationResult(ClaudeCodeTranscriptRelocationStatus.Moved, source, target, backup, message);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            Rollback(undo);
            return new ClaudeCodeTranscriptRelocationResult(
                ClaudeCodeTranscriptRelocationStatus.Failed, source, Message: $"The conversation {cliSessionId} of Claude Code could not be moved: {ex.Message}");
        }
        catch
        {
            Rollback(undo);
            throw;
        }
    }

    private static void ValidateSessionId(string cliSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliSessionId);
        if (cliSessionId.Contains("..", StringComparison.Ordinal) ||
            cliSessionId.AsSpan().IndexOfAny('/', '\\') >= 0 ||
            cliSessionId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(cliSessionId, cliSessionId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{cliSessionId}' is not an identifier of a conversation of Claude Code.", nameof(cliSessionId));
        }
    }

    // The folder of the old working directory comes first, as for the CLI; else the one other folder that has it.
    private static List<string> FindSources(string projects, string fromName, string toName, string fileName)
    {
        var inFromFolder = Path.Combine(projects, fromName, fileName);
        if (File.Exists(inFromFolder))
        {
            return [inFromFolder];
        }

        var found = new List<string>();
        if (!Directory.Exists(projects))
        {
            return found;
        }

        var target = Path.Combine(projects, toName);
        foreach (var folder in Directory.EnumerateDirectories(projects))
        {
            // A folder that is the target under another name, or one already found under another name, is the same copy.
            if (!ClaudeCodeProjectFolders.AreSameFolderName(Path.GetFileName(folder), toName) &&
                Path.Combine(folder, fileName) is var candidate && File.Exists(candidate) &&
                !IsSameFolder(folder, target) && !found.Any(other => IsSameFolder(Path.GetDirectoryName(other)!, folder)))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether two folders are one: a file created in the first is seen in the second. That holds through links,
    /// junctions and differences of case, which comparing the paths does not see.
    /// </summary>
    /// <exception cref="IOException">The probe file cannot be created: nothing can be known of the folders.</exception>
    internal static bool IsSameFolder(string first, string second)
    {
        if (!Directory.Exists(first) || !Directory.Exists(second))
        {
            return false;
        }

        var probe = ".codealta-probe-" + Guid.NewGuid().ToString("N");
        var created = Path.Combine(first, probe);
        using (new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose))
        {
            return File.Exists(Path.Combine(second, probe));
        }
    }

    private static async Task<bool> HaveSameContentAsync(string left, string right, CancellationToken cancellationToken)
    {
        await using var first = OpenRead(left);
        await using var second = OpenRead(right);
        if (first.Length != second.Length)
        {
            return false;
        }

        var firstBuffer = new byte[BufferSize];
        var secondBuffer = new byte[BufferSize];
        while (true)
        {
            var read = await first.ReadAtLeastAsync(firstBuffer, BufferSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return true;
            }

            await second.ReadExactlyAsync(secondBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (!firstBuffer.AsSpan(0, read).SequenceEqual(secondBuffer.AsSpan(0, read)))
            {
                return false;
            }
        }
    }

    // Copies a file to a path that does not exist, and checks that the copy has the length of the source.
    private static async Task CopyFileAsync(string source, string destination, Stack<Action> undo, CancellationToken cancellationToken)
    {
        await using (var input = OpenRead(source))
        {
            var length = input.Length;
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                undo.Push(() => File.Delete(destination));
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(destination).Length != length)
            {
                throw new IOException($"The copy of {source} is incomplete.");
            }
        }

        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    // Copies what a folder has that the destination has not; the files the destination has are kept.
    private static async Task MergeFolderAsync(string source, string destination, Stack<Action> undo, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
            undo.Push(() => Directory.Delete(destination));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var copy = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(copy))
            {
                await CopyFileAsync(file, copy, undo, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            await MergeFolderAsync(folder, Path.Combine(destination, Path.GetFileName(folder)), undo, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void MoveFile(string source, string destination, Stack<Action> undo)
    {
        File.Move(source, destination);
        undo.Push(() => File.Move(destination, source));
    }

    private static void MoveDirectory(string source, string destination, Stack<Action> undo)
    {
        Directory.Move(source, destination);
        undo.Push(() => Directory.Move(destination, source));
    }

    // The companion folder of a source that is gone; the transcript is safe at the target whatever happens here.
    private static string? DeleteLeftover(string companion)
    {
        try
        {
            if (Directory.Exists(companion))
            {
                Directory.Delete(companion, recursive: true);
            }

            return null;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"The folder {companion} could not be removed: {ex.Message}";
        }
    }

    private static string UniquePath(string path)
    {
        var candidate = path;
        for (var index = 1; File.Exists(candidate) || Directory.Exists(candidate); index++)
        {
            candidate = $"{path}-{index}";
        }

        return candidate;
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    // Undoes what was done, last first; what cannot be undone is left as it is.
    private static void Rollback(Stack<Action> undo)
    {
        while (undo.TryPop(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // The rest is still undone: a leftover copy is better than a lost source.
            }
        }
    }

    private static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
