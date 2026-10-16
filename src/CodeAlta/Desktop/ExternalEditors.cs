using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeAlta.Desktop;

/// <summary>How an editor of the list is given a folder, and a file at a line.</summary>
internal enum ExternalEditorStyle
{
    /// <summary>
    /// The editors made from Visual Studio Code: <c>&lt;folder&gt;</c>, then <c>--goto &lt;file&gt;:&lt;line&gt;:&lt;column&gt;</c>.
    /// </summary>
    VsCode,

    /// <summary>Zed and Sublime Text: <c>&lt;folder&gt; &lt;file&gt;:&lt;line&gt;:&lt;column&gt;</c>.</summary>
    PathAndLine,
}

/// <summary>An editor CodeAlta knows how to find and to start.</summary>
/// <param name="Id">The id the preferences and the page name it by; also the logo of the page.</param>
/// <param name="Name">The name of the editor.</param>
/// <param name="Style">How it is given a folder and a file.</param>
/// <param name="WindowsFiles">
/// Where it is installed on Windows, relative to the folders of the programs of the user (<c>%LOCALAPPDATA%\Programs</c>)
/// and of the machine (<c>%ProgramFiles%</c>).
/// </param>
/// <param name="WindowsCommands">
/// The names of its command on <c>PATH</c> on Windows; a <c>.cmd</c> is never started, so the executable is looked for
/// in the folders above it (<see cref="WindowsExecutable"/>).
/// </param>
/// <param name="WindowsExecutable">The name of the executable that a <c>.cmd</c> of <see cref="WindowsCommands"/> starts.</param>
/// <param name="MacApps">Its bundle in the folders of the applications, and its command inside the bundle.</param>
/// <param name="UnixCommands">The names of its command on <c>PATH</c> on macOS and Linux.</param>
internal sealed record ExternalEditorKind(string Id, string Name, ExternalEditorStyle Style, ImmutableArray<string> WindowsFiles,
    ImmutableArray<string> WindowsCommands, string? WindowsExecutable, ImmutableArray<(string Bundle, string Command)> MacApps, ImmutableArray<string> UnixCommands);

/// <summary>An editor found on this machine.</summary>
/// <param name="Id">The id of its kind, or <see cref="ExternalEditors.CustomId"/>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Program">The full path of the program that is started.</param>
internal sealed record ExternalEditorFound(string Id, string Name, string Program);

/// <summary>The machine editors are looked for on. Tests describe another one.</summary>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>.</param>
/// <param name="PathVariable">The <c>PATH</c> of the application.</param>
/// <param name="Home">The folder of the user.</param>
/// <param name="LocalAppData">The local application data of the user (Windows).</param>
/// <param name="ProgramFiles">The folders of the programs of the machine (Windows).</param>
/// <param name="FileExists">Whether a file is there.</param>
/// <param name="DirectoryExists">Whether a folder is there.</param>
internal sealed record ExternalEditorEnvironment(string Platform, string? PathVariable, string? Home, string? LocalAppData, ImmutableArray<string> ProgramFiles,
    Func<string, bool> FileExists, Func<string, bool> DirectoryExists)
{
    /// <summary>This machine.</summary>
    internal static ExternalEditorEnvironment Current() => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        [.. new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(static folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        File.Exists, Directory.Exists);

    internal bool IsWindows => Platform == "windows";
}

/// <summary>The choice of the user, as the preferences keep it.</summary>
/// <param name="Id">The editor chosen, or null when none was: the first editor found is used.</param>
/// <param name="Program">The program of a custom editor.</param>
/// <param name="Arguments">The arguments of a custom editor, with <c>{folder}</c>, <c>{file}</c>, <c>{line}</c> and <c>{column}</c>.</param>
internal sealed record ExternalEditorChoice(string? Id, string? Program, string? Arguments)
{
    /// <summary>No choice: the first editor found.</summary>
    internal static ExternalEditorChoice None { get; } = new(null, null, null);

    /// <summary>The longest program and argument template that are kept.</summary>
    internal const int MaximumLength = 1024;

    /// <summary>
    /// The choice as it is kept, or null when it is not one: an id that is neither an editor of the list nor
    /// <see cref="ExternalEditors.CustomId"/>, or a custom editor without a program, or a text too long.
    /// </summary>
    internal static ExternalEditorChoice? Valid(string? id, string? program, string? arguments)
    {
        if (id is null) return None;
        if (id == ExternalEditors.CustomId)
        {
            var path = program?.Trim();
            var template = string.IsNullOrWhiteSpace(arguments) ? ExternalEditors.DefaultArguments : arguments.Trim();
            if (path is not { Length: > 0 and <= MaximumLength } || template.Length > MaximumLength || path.Any(char.IsControl) || template.Any(char.IsControl)) return null;
            return new(id, path, template);
        }

        return ExternalEditors.Kinds.Any(kind => kind.Id == id) ? new(id, null, null) : null;
    }
}

/// <summary>
/// The editors of the machine that a folder or a file can be opened in, beside the code editor of CodeAlta. They
/// are looked for once, when they are first asked for, by the files they install: nothing is started and nothing
/// is looked for while the application starts. A program is started with its arguments one by one, never through a
/// shell, and never a batch file, which Windows runs through <c>cmd.exe</c> that reads its arguments again.
/// </summary>
internal sealed class ExternalEditors
{
    /// <summary>The id of the editor the user describes by its program and its arguments.</summary>
    internal const string CustomId = "custom";

    /// <summary>The arguments of a custom editor that names none: the folder.</summary>
    internal const string DefaultArguments = "{folder}";

    /// <summary>The editors CodeAlta knows, in the order the first one found is chosen.</summary>
    internal static ImmutableArray<ExternalEditorKind> Kinds { get; } =
    [
        new("vscode", "Visual Studio Code", ExternalEditorStyle.VsCode, [@"Microsoft VS Code\Code.exe"], ["code.cmd"], "Code.exe",
            [("Visual Studio Code.app", "Contents/Resources/app/bin/code")], ["code"]),
        new("vscodeinsiders", "Visual Studio Code - Insiders", ExternalEditorStyle.VsCode, [@"Microsoft VS Code Insiders\Code - Insiders.exe"], ["code-insiders.cmd"], "Code - Insiders.exe",
            [("Visual Studio Code - Insiders.app", "Contents/Resources/app/bin/code")], ["code-insiders"]),
        new("cursor", "Cursor", ExternalEditorStyle.VsCode, [@"cursor\Cursor.exe"], ["cursor.cmd"], "Cursor.exe",
            [("Cursor.app", "Contents/Resources/app/bin/cursor")], ["cursor"]),
        new("windsurf", "Windsurf", ExternalEditorStyle.VsCode, [@"Windsurf\Windsurf.exe"], ["windsurf.cmd"], "Windsurf.exe",
            [("Windsurf.app", "Contents/Resources/app/bin/windsurf")], ["windsurf"]),
        new("zed", "Zed", ExternalEditorStyle.PathAndLine, [@"Zed\bin\zed.exe", @"Zed\Zed.exe"], ["zed.exe"], null,
            [("Zed.app", "Contents/MacOS/cli")], ["zed", "zeditor"]),
        new("sublimetext", "Sublime Text", ExternalEditorStyle.PathAndLine, [@"Sublime Text\subl.exe", @"Sublime Text 3\subl.exe"], ["subl.exe"], null,
            [("Sublime Text.app", "Contents/SharedSupport/bin/subl")], ["subl"]),
    ];

    private readonly Func<ExternalEditorChoice> _choice;
    private readonly ExternalEditorEnvironment _environment;
    private readonly Func<ProcessStartInfo, bool> _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<ExternalEditorFound>? _found;

    /// <param name="choice">The choice of the user, as it is now.</param>
    /// <param name="environment">The machine; this one by default.</param>
    /// <param name="start">Starts a program; <see cref="Process.Start(ProcessStartInfo)"/> by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null.</exception>
    internal ExternalEditors(Func<ExternalEditorChoice> choice, ExternalEditorEnvironment? environment = null, Func<ProcessStartInfo, bool>? start = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        _choice = choice;
        _environment = environment ?? ExternalEditorEnvironment.Current();
        _start = start ?? Start;
    }

    /// <summary><c>windows</c>, <c>macos</c> or <c>linux</c>.</summary>
    internal string Platform => _environment.Platform;

    /// <summary>The choice of the user, as it is now.</summary>
    internal ExternalEditorChoice Choice => _choice();

    /// <summary>The editors of the list found on the machine, looked for the first time they are asked for.</summary>
    /// <param name="refresh">Looks for them again, as after an editor was installed.</param>
    /// <param name="cancellationToken">Cancels the wait for a search that runs.</param>
    internal async Task<ImmutableArray<ExternalEditorFound>> FoundAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (refresh || _found is null) _found = await Task.Run(() => Detect(_environment), CancellationToken.None).ConfigureAwait(false);
            return _found.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The editor a folder is opened in: the one the user chose when it is found, or the custom one; else the
    /// first editor of the list found. Null when there is none.
    /// </summary>
    internal async Task<ExternalEditorFound?> CurrentAsync(CancellationToken cancellationToken)
    {
        var choice = _choice();
        if (choice.Id == CustomId)
            return choice.Program is { Length: > 0 } program ? new(CustomId, Path.GetFileNameWithoutExtension(program), program) : null;
        var found = await FoundAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault(editor => editor.Id == choice.Id) ?? found.FirstOrDefault();
    }

    /// <summary>
    /// Opens a folder in the editor, and a file of it at a line when one is given.
    /// </summary>
    /// <param name="folder">The full path of the folder.</param>
    /// <param name="file">The full path of a file of the folder, or null.</param>
    /// <param name="line">The line of the file, from 1.</param>
    /// <param name="column">The column of the line, from 1.</param>
    /// <param name="cancellationToken">Cancels the search of the editors.</param>
    /// <returns><c>ok</c>, <c>no_editor</c> (none was found or chosen), <c>refused</c> (the custom program cannot be started) or <c>failed</c>.</returns>
    internal async Task<string> OpenAsync(string folder, string? file, int? line, int? column, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (await CurrentAsync(cancellationToken).ConfigureAwait(false) is not { } editor) return "no_editor";
        IReadOnlyList<string>? arguments;
        if (editor.Id == CustomId)
        {
            if (!Path.IsPathFullyQualified(editor.Program) || IsBatchFile(editor.Program) || !_environment.FileExists(editor.Program)) return "refused";
            arguments = CustomArguments(_choice().Arguments ?? DefaultArguments, folder, file, line, column);
            if (arguments is null) return "refused";
        }
        else
        {
            arguments = Arguments(Kinds.First(kind => kind.Id == editor.Id).Style, folder, file, line, column);
        }

        var start = new ProcessStartInfo(editor.Program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return _start(start) ? "ok" : "failed";
    }

    /// <summary>The arguments an editor of the list is started with.</summary>
    internal static IReadOnlyList<string> Arguments(ExternalEditorStyle style, string folder, string? file, int? line, int? column)
    {
        if (file is null) return [folder];
        var at = Position(file, line, column);
        return style == ExternalEditorStyle.VsCode ? [folder, "--goto", at] : [folder, at];
    }

    // A file at a line and a column as editors read it: the column only with a line.
    private static string Position(string file, int? line, int? column)
        => line is > 0 ? column is > 0 ? $"{file}:{line.Value.ToString(CultureInfo.InvariantCulture)}:{column.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"{file}:{line.Value.ToString(CultureInfo.InvariantCulture)}" : file;

    /// <summary>
    /// The arguments of a custom editor: the template is cut into arguments first (a quoted part is one argument,
    /// <c>""</c> is a quote inside it), then <c>{folder}</c>, <c>{file}</c>, <c>{line}</c> and <c>{column}</c> are
    /// replaced in each, so that a path with spaces stays one argument. An argument with <c>{file}</c> is left out
    /// when no file is opened; a missing line or column is 1.
    /// </summary>
    /// <returns>The arguments, or null when the template has an unclosed quote.</returns>
    internal static IReadOnlyList<string>? CustomArguments(string template, string folder, string? file, int? line, int? column)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Split(template) is not { } parts) return null;
        var lineText = (line is > 0 ? line.Value : 1).ToString(CultureInfo.InvariantCulture);
        var columnText = (column is > 0 ? column.Value : 1).ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (file is null && part.Contains("{file}", StringComparison.Ordinal)) continue;
            arguments.Add(part.Replace("{folder}", folder, StringComparison.Ordinal).Replace("{file}", file, StringComparison.Ordinal)
                .Replace("{line}", lineText, StringComparison.Ordinal).Replace("{column}", columnText, StringComparison.Ordinal));
        }

        return arguments;
    }

    // The arguments of a template, as a command line writes them: separated by spaces, a quoted part kept whole.
    internal static List<string>? Split(string template)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character == '"')
            {
                if (quoted && index + 1 < template.Length && template[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (started) parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        if (quoted) return null;
        if (started) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The editors of the list that are on a machine, in the order of the list.</summary>
    internal static ImmutableArray<ExternalEditorFound> Detect(ExternalEditorEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var found = ImmutableArray.CreateBuilder<ExternalEditorFound>();
        foreach (var kind in Kinds)
        {
            if (Find(kind, environment) is { } program) found.Add(new(kind.Id, kind.Name, program));
        }

        return found.ToImmutable();
    }

    private static string? Find(ExternalEditorKind kind, ExternalEditorEnvironment environment)
    {
        if (environment.IsWindows)
        {
            // The installers of the user, then those of the machine, then PATH.
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(environment.LocalAppData)) roots.Add(Path.Combine(environment.LocalAppData, "Programs"));
            roots.AddRange(environment.ProgramFiles.Where(static folder => !string.IsNullOrWhiteSpace(folder)));
            foreach (var root in roots)
            {
                foreach (var file in kind.WindowsFiles)
                {
                    var candidate = Path.Combine(root, file);
                    if (environment.FileExists(candidate)) return candidate;
                }
            }

            foreach (var folder in PathFolders(environment))
            {
                foreach (var command in kind.WindowsCommands)
                {
                    var candidate = Path.Combine(folder, command);
                    if (!environment.FileExists(candidate)) continue;
                    if (!IsBatchFile(candidate)) return candidate;
                    // The launcher script of an editor is in a folder below its executable: bin, or resources\app\bin.
                    if (kind.WindowsExecutable is null) continue;
                    var above = folder;
                    for (var level = 0; level < 3 && Path.GetDirectoryName(above) is { } parent; level++)
                    {
                        above = parent;
                        var executable = Path.Combine(above, kind.WindowsExecutable);
                        if (environment.FileExists(executable)) return executable;
                    }
                }
            }

            return null;
        }

        if (environment.Platform == "macos")
        {
            var applications = new List<string> { "/Applications" };
            if (!string.IsNullOrWhiteSpace(environment.Home)) applications.Add(Path.Combine(environment.Home, "Applications"));
            foreach (var folder in applications)
            {
                foreach (var (bundle, command) in kind.MacApps)
                {
                    var candidate = Path.Combine(folder, bundle, command);
                    if (environment.FileExists(candidate)) return candidate;
                }
            }
        }

        // A desktop application does not get the PATH of the user's shell: the folders of the package managers are looked at as well.
        var folders = PathFolders(environment).Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin"]);
        if (!string.IsNullOrWhiteSpace(environment.Home)) folders = folders.Append(Path.Combine(environment.Home, ".local", "bin"));
        foreach (var folder in folders.Distinct(StringComparer.Ordinal))
        {
            foreach (var command in kind.UnixCommands)
            {
                var candidate = Path.Combine(folder, command);
                if (environment.FileExists(candidate)) return candidate;
            }
        }

        return null;
    }

    // The folders of PATH that are full paths: a relative one resolves against the current folder, which is not the user's choice.
    private static IEnumerable<string> PathFolders(ExternalEditorEnvironment environment)
        => (environment.PathVariable ?? string.Empty).Split(environment.IsWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted);

    /// <summary>Whether a program is a batch file, which Windows runs through <c>cmd.exe</c>.</summary>
    internal static bool IsBatchFile(string path)
    {
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Start(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
