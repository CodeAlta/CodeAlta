using System.Security;

namespace CodeAlta.Agent.Runtime;

// One composition pass, shared by the raw-agent and orchestration composers. Only instruction-file I/O
// failures are recoverable here; invalid inputs and programming errors still escape.
internal sealed class AgentInstructionFileReader
{
    private readonly Func<string, long> _getLength;
    private readonly Func<string, string> _readAllText;
    private readonly List<AgentInstructionFileDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _reportedPaths = new(StringComparer.OrdinalIgnoreCase);

    internal AgentInstructionFileReader() : this(static path => new FileInfo(path).Length, File.ReadAllText)
    {
    }

    internal AgentInstructionFileReader(Func<string, long> getLength, Func<string, string> readAllText)
    {
        ArgumentNullException.ThrowIfNull(getLength);
        ArgumentNullException.ThrowIfNull(readAllText);
        _getLength = getLength;
        _readAllText = readAllText;
    }

    internal IReadOnlyList<AgentInstructionFileDiagnostic> Diagnostics => _diagnostics;

    internal string? SelectLargestFile(string directory)
    {
        string? selected = null;
        long selectedLength = -1;
        foreach (var relative in new[] { "AGENTS.md", "CLAUDE.md", Path.Combine(".github", "copilot-instructions.md") })
        {
            var path = Path.Combine(directory, relative);
            var length = GetLength(path);
            if (length is { } size && (size > selectedLength ||
                size == selectedLength && StringComparer.OrdinalIgnoreCase.Compare(path, selected) < 0))
            {
                selected = path;
                selectedLength = size;
            }
        }

        return selected;
    }

    internal long? GetLength(string path, bool required = false)
    {
        try
        {
            // File.Exists hides access errors as absence. Query metadata directly so those errors are observable.
            return _getLength(path);
        }
        catch (Exception ex) when (!required && ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The three optional candidate names need not exist. A selected file disappearing is different.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ReportFailure(path, "inspected", ex);
            return null;
        }
    }

    internal string? ReadAllText(string path)
    {
        try
        {
            return _readAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ReportFailure(path, "read", ex);
            return null;
        }
    }

    internal void ReportFailure(string path, string operation, Exception exception)
    {
        if (_reportedPaths.Add(path))
        {
            _diagnostics.Add(new(path,
                $"Project instruction/context file '{path}' could not be {operation}: {exception.Message} Continuing without this file; its guidance is unavailable."));
        }
    }
}

internal sealed record AgentInstructionFileDiagnostic(string Path, string Message);
