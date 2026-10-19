namespace CodeAlta.Agent.Cursor;

/// <summary>
/// Describes how to start the Cursor agent CLI for one provider definition.
/// </summary>
public sealed class CursorCliLaunch
{
    /// <summary>
    /// Initializes a launch for the Cursor agent executable.
    /// </summary>
    /// <param name="fileName">The executable, or <c>agent</c> when the definition leaves it unset.</param>
    /// <param name="arguments">Arguments placed before the verb (<c>login</c>, <c>status</c>, or <c>acp</c>).</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    public CursorCliLaunch(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        FileName = fileName;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Gets the executable file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the arguments that precede the verb.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the working directory, when one was configured.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// Builds a launch from a configured provider command. An empty command selects <c>agent</c> on the path.
    /// </summary>
    /// <param name="command">The configured executable, or empty to use <c>agent</c>.</param>
    /// <param name="arguments">Arguments inserted before the verb.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <returns>The launch description.</returns>
    public static CursorCliLaunch From(string? command, IReadOnlyList<string>? arguments, string? workingDirectory)
    {
        var fileName = string.IsNullOrWhiteSpace(command) ? "agent" : command.Trim();
        var prefix = arguments is null
            ? Array.Empty<string>()
            : arguments.Where(static argument => !string.IsNullOrWhiteSpace(argument)).Select(static argument => argument.Trim()).ToArray();
        return new CursorCliLaunch(fileName, prefix, string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim());
    }
}
