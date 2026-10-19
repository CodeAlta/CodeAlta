using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Adds the folders a start of <c>alta</c> names (<c>alta .</c>, <c>alta &lt;folder&gt;...</c>, with <c>--space</c>) as
/// projects of the running application, then shows the first one in the window. A start that finds CodeAlta running
/// passes them on with <see cref="Arguments"/>; the running one reads them with <see cref="TryRead"/>.
/// </summary>
/// <remarks>
/// A folder is added with the command <c>alta project add</c>, which checks it, puts it in the spaces named and tells
/// the window. The requests that come before the commands can run (while the application starts) wait for
/// <see cref="Attach"/>, and the requests run one after the other.
/// </remarks>
internal sealed class DesktopProjectOpening
{
    /// <summary>The argument passed on before each folder.</summary>
    internal const string ProjectArgument = "--project";

    private readonly object _gate = new();
    private readonly Queue<(IReadOnlyList<string> Folders, IReadOnlyList<string> Spaces)> _pending = new();
    private readonly Action<string, string?> _show;
    private readonly Action<string> _warn;
    private Func<IReadOnlyList<string>, Task<AltaCommandResult>>? _invoke;
    private Task _work = Task.CompletedTask;

    /// <summary>Creates the service.</summary>
    /// <param name="show">Shows a project in the window: its id, and the id of a space it joined or null.</param>
    /// <param name="warn">Reports a folder that could not be added.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopProjectOpening(Action<string, string?> show, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(warn);
        _show = show;
        _warn = warn;
    }

    /// <summary>Gets the work of the requests made so far, which ends once each of them is done.</summary>
    internal Task Completion { get { lock (_gate) return _work; } }

    /// <summary>The arguments a start passes on to the running application for folders and their spaces.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static string[] Arguments(IReadOnlyList<string> folders, IReadOnlyList<string> spaces)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(spaces);
        return [.. folders.SelectMany(static folder => new[] { ProjectArgument, folder }), .. spaces.SelectMany(static space => new[] { DesktopCommandLine.SpaceOption, space })];
    }

    /// <summary>Reads the folders and spaces passed on by <see cref="Arguments"/>; false for any other arguments.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    internal static bool TryRead(IReadOnlyList<string> arguments, out IReadOnlyList<string> folders, out IReadOnlyList<string> spaces)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var foundFolders = new List<string>();
        var foundSpaces = new List<string>();
        folders = foundFolders;
        spaces = foundSpaces;
        if (arguments.Count == 0 || arguments.Count % 2 != 0) return false;
        for (var i = 0; i < arguments.Count; i += 2)
        {
            var value = arguments[i + 1];
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (arguments[i] == ProjectArgument && Path.IsPathFullyQualified(value)) foundFolders.Add(value);
            else if (arguments[i] == DesktopCommandLine.SpaceOption) foundSpaces.Add(value);
            else return false;
        }

        return foundFolders.Count is > 0 and <= DesktopCommandLine.MaximumProjects;
    }

    /// <summary>Adds folders as projects, in the spaces named, once the commands can run.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal void Request(IReadOnlyList<string> folders, IReadOnlyList<string> spaces)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(spaces);
        if (folders.Count == 0) return;
        lock (_gate)
        {
            _pending.Enqueue(([.. folders], [.. spaces]));
            Drain();
        }
    }

    /// <summary>Runs the requests with the <c>alta</c> commands of the application, those made already first.</summary>
    /// <param name="invoke">Runs one <c>alta</c> command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="invoke"/> is null.</exception>
    internal void Attach(Func<IReadOnlyList<string>, Task<AltaCommandResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        lock (_gate)
        {
            _invoke = invoke;
            Drain();
        }
    }

    // Under the gate.
    private void Drain()
    {
        if (_invoke is not { } invoke) return;
        while (_pending.TryDequeue(out var request))
        {
            var previous = _work;
            _work = OpenAfterAsync(previous, invoke, request.Folders, request.Spaces);
        }
    }

    private async Task OpenAfterAsync(Task previous, Func<IReadOnlyList<string>, Task<AltaCommandResult>> invoke, IReadOnlyList<string> folders, IReadOnlyList<string> spaces)
    {
        await previous.ConfigureAwait(false);
        // A start of `alta <folder>` reaches the running application on the thread of its window: the commands that
        // add the projects read and write the catalog, and run on the pool instead.
        await Task.Run(() => OpenAsync(invoke, folders, spaces)).ConfigureAwait(false);
    }

    private async Task OpenAsync(Func<IReadOnlyList<string>, Task<AltaCommandResult>> invoke, IReadOnlyList<string> folders, IReadOnlyList<string> spaces)
    {
        string? shown = null, space = null;
        foreach (var folder in folders)
        {
            try
            {
                var result = await invoke(["project", "add", folder, .. spaces.SelectMany(static name => new[] { "--space", name })]).ConfigureAwait(false);
                if (result.ExitCode != 0 || Added(result.Stdout) is not { } added)
                {
                    _warn($"The folder '{folder}' was not added as a project: {Reason(result)}");
                    continue;
                }

                shown ??= added.ProjectId;
                // The space a folder joined, for a window that shows a space without it.
                if (spaces.Count > 0) space ??= added.SpaceId;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _warn($"The folder '{folder}' was not added as a project: {exception.Message}");
            }
        }

        if (shown is not null) _show(shown, space);
    }

    // The project of an `alta.project.added` record, and the first space it is in besides the default one.
    private static (string ProjectId, string? SpaceId)? Added(string transcript)
    {
        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "alta.project.added"
                    || !root.TryGetProperty("projectId", out var id) || id.GetString() is not { Length: > 0 } projectId) continue;
                var spaceId = root.TryGetProperty("spaces", out var spaces) && spaces.ValueKind == JsonValueKind.Array
                    ? spaces.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).FirstOrDefault(static item => !string.IsNullOrEmpty(item))
                    : null;
                return (projectId, spaceId);
            }
            catch (JsonException)
            {
                // Not a record: the transcript has other lines.
            }
        }

        return null;
    }

    private static string Reason(AltaCommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        text = text.Trim();
        return text.Length > 400 ? text[..400] : text.Length == 0 ? $"exit code {result.ExitCode}" : text;
    }
}
