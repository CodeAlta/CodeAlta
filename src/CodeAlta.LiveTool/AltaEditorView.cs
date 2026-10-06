namespace CodeAlta.LiveTool;

/// <summary>
/// Shows the files of a project to the user in the code editor of a host that has one. A host without one
/// registers no such service, and <c>alta editor</c> is then not part of its commands.
/// </summary>
public interface IAltaEditorView
{
    /// <summary>Asks the host to open the code editor of a project, and a file in it.</summary>
    /// <param name="projectId">The id of the project.</param>
    /// <param name="path">The file to open, relative to the project folder; null to show the project's files only.</param>
    /// <param name="line">The 1-based line to go to in the file; null to keep where the file was.</param>
    /// <param name="column">The 1-based column on that line; null for its start.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="projectId"/> is null or blank.</exception>
    bool Open(string projectId, string? path, int? line, int? column);
}
