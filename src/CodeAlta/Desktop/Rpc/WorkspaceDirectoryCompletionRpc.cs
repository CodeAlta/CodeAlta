using System.Buffers;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // An admitted read owns one worker through settlement, even if the bridge cancels its waiter.
    private readonly Func<string, string, Task<DirectoryCompletionResult>>? _directoryCompletionReader;
    private Task<WorkspaceDirectoryCompletionResponse>? _directoryCompletionWork;
    internal const int MaximumDirectoryCompletionEnvelopeBytes = 64 * 1024;
    // NeoRpcOptions validates MaximumIdLength <= 256; '<' is escaped to six UTF-8 bytes per ID unit.
    private const int MaximumCompletionIdLengthForBudget = 256;

    internal WorkspaceService(OwnedSessionWorkspace reads, ProjectCatalog catalog, string epoch,
        Func<string, string, Task<DirectoryCompletionResult>> completionReader) : this(reads, catalog, epoch)
    {
        ArgumentNullException.ThrowIfNull(completionReader);
        _directoryCompletionReader = completionReader;
    }

    /// <summary>Reads bounded direct-child folder suggestions only when an owned host explicitly requests them.</summary>
    /// <remarks>Suggestions are not proof of ownership or importability. A canceled waiter does not release read
    /// admission; owned close drains the underlying read, which can block without a wall-clock bound.</remarks>
    /// <param name="request">Exact owned-host epoch, canonical directory and literal child-name prefix.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, not a blocking admitted read.</param>
    /// <returns>A bounded, sanitized status and observed suggestion subset.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels before admission or while waiting.</exception>
    [NeoRpcMethod("completeDirectory")]
    public async Task<WorkspaceDirectoryCompletionResponse> CompleteDirectoryAsync(
        WorkspaceDirectoryCompletionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceDirectoryCompletionResponse Reply(string status, bool echo = false)
            => new(status, _importEpoch, echo ? request?.DirectoryPath : null, echo ? request?.Prefix : null, [], 0, false);
        if (_directoryCompletionReader is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidDirectoryCompletionInput(request.DirectoryPath, request.Prefix))
            return Reply("invalid_request");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch", true);
        cancellationToken.ThrowIfCancellationRequested();

        Task<WorkspaceDirectoryCompletionResponse> work;
        lock (_importGate)
        {
            if (_importsClosed) return Reply("closed", true);
            if (_directoryCompletionWork is not null) return Reply("busy", true);
            var completion = new TaskCompletionSource<WorkspaceDirectoryCompletionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _directoryCompletionWork = work = completion.Task;
            _ = ReadDirectoryCompletionAsync(request.DirectoryPath, request.Prefix, completion);
        }
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadDirectoryCompletionAsync(string directory, string prefix,
        TaskCompletionSource<WorkspaceDirectoryCompletionResponse> completion)
    {
        try
        {
            var result = await _directoryCompletionReader!(directory, prefix).ConfigureAwait(false);
            completion.TrySetResult(ProjectDirectoryCompletion(result, directory, prefix));
        }
        catch (Exception)
        {
            // An injected reader or unexpected filesystem failure must not surface paths or exception details.
            completion.TrySetResult(new("read_error", _importEpoch, directory, prefix, [], 0, false));
        }
        finally { lock (_importGate) { if (ReferenceEquals(_directoryCompletionWork, completion.Task)) _directoryCompletionWork = null; } }
    }

    private WorkspaceDirectoryCompletionResponse ProjectDirectoryCompletion(DirectoryCompletionResult result, string directory, string prefix)
    {
        WorkspaceDirectoryCompletionResponse Error() => new("read_error", _importEpoch, directory, prefix, [], 0, false);
        if (result is null || result.EntriesVisited is < 0 or > DirectoryCompletionReader.MaximumEntries + 1 || result.Directories is null
            || result.Directories.Count > DirectoryCompletionReader.MaximumResults) return Error();
        var status = result.Status switch
        {
            DirectoryCompletionStatus.Complete => "complete",
            DirectoryCompletionStatus.Incomplete => "incomplete",
            DirectoryCompletionStatus.Invalid => "invalid_request",
            DirectoryCompletionStatus.Missing => "missing",
            DirectoryCompletionStatus.NotDirectory => "not_directory",
            DirectoryCompletionStatus.Denied => "denied",
            DirectoryCompletionStatus.ReadError => "read_error",
            _ => "read_error",
        };
        if (status is not ("complete" or "incomplete"))
            return new(status, _importEpoch, directory, prefix, [], result.EntriesVisited, result.OmittedUnsafeEntries);
        var paths = new List<string>(result.Directories.Count);
        var total = 0;
        foreach (var path in result.Directories)
        {
            if (path is null || path.Length > DirectoryCompletionReader.MaximumDirectoryLength || !ValidWireText(path)
                || Path.GetDirectoryName(path) != directory || path != Path.GetFullPath(path)) return Error();
            total += path.Length;
            if (total > DirectoryCompletionReader.MaximumTotalResultCharacters) return Error();
            paths.Add(path);
        }
        var response = new WorkspaceDirectoryCompletionResponse(status, _importEpoch, directory, prefix, paths.ToArray(),
            result.EntriesVisited, result.OmittedUnsafeEntries);
        while (DirectoryCompletionEnvelopeBytes(response) > MaximumDirectoryCompletionEnvelopeBytes && paths.Count > 0)
        {
            paths.RemoveAt(paths.Count - 1);
            response = response with { Status = "incomplete", Directories = paths.ToArray() };
        }
        return DirectoryCompletionEnvelopeBytes(response) <= MaximumDirectoryCompletionEnvelopeBytes ? response : Error();
    }

    // Serialize the actual escaped JSON result frame, including its fields and an intentionally oversized,
    // maximally escaped request ID (the frame's only variable field outside the result).
    internal static int DirectoryCompletionEnvelopeBytes(WorkspaceDirectoryCompletionResponse response)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("neoastra", 1);
            writer.WriteString("kind", "result");
            writer.WriteString("id", new string('<', MaximumCompletionIdLengthForBudget));
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, response, DesktopJsonContext.Default.WorkspaceDirectoryCompletionResponse);
            writer.WriteEndObject();
        }
        return buffer.WrittenCount;
    }

    private static bool ValidDirectoryCompletionInput(string? directory, string? prefix)
    {
        if (directory is not { Length: > 0 and <= DirectoryCompletionReader.MaximumDirectoryLength }
            || prefix is not { Length: <= DirectoryCompletionReader.MaximumPrefixLength }
            || !ValidWireText(directory) || !ValidWireText(prefix)) return false;
        try
        {
            if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || prefix.Contains(Path.DirectorySeparatorChar)
                || prefix.Contains(Path.AltDirectorySeparatorChar) || !Path.IsPathFullyQualified(directory)
                || directory.StartsWith("//", StringComparison.Ordinal) || directory.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(directory);
            return root is not null && directory != root && directory == Path.GetFullPath(directory)
                && (!OperatingSystem.IsWindows() || root.Length == 3 && char.IsLetter(root[0]) && root[1] == ':' && root[2] == '\\');
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool ValidWireText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

internal sealed record WorkspaceDirectoryCompletionRequest(string ExpectedHostEpoch, string DirectoryPath, string Prefix);
internal sealed record WorkspaceDirectoryCompletionResponse(string Status, string? HostEpoch, string? DirectoryPath, string? Prefix,
    string[] Directories, int EntriesVisited, bool OmittedUnsafeEntries);
