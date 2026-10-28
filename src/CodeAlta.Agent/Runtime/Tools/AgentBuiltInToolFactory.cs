using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent.Runtime.Images;
using XenoAtom.Glob;
using XenoAtom.Glob.Git;
using XenoAtom.Glob.IO;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Creates the default non-provider built-in tools used by local raw-API sessions.
/// </summary>
public static partial class AgentBuiltInToolFactory
{
    private static readonly FileTreeWalker FileTreeWalker = new();
    private static readonly Lazy<string> WindowsShellFileName = new(
        () => ResolveWindowsShellFileName(Environment.GetEnvironmentVariable("PATH")),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly TimeSpan KilledProcessExitTimeout = TimeSpan.FromSeconds(5);
    private const string SupportedWebContentTypesDescription = "text/*, application/json, application/xml, and application/xhtml+xml";
    private const string WriteFileToolDescription =
        "Write an entire text file in one deterministic operation. Creates parent directories as needed and replaces any existing file.";
    private const string ReplaceInFileToolDescription =
        "Replace exact text in a file. Deterministic only: no regex, no fuzzy matching. When replace_all is false, the tool errors unless exactly one match exists and leaves the file unchanged. Use replace_all=true to replace every match. For several changes in one file, give an edits list instead of old_string and new_string: the edits apply in their order, each to the text the edits before it left, and if one fails the file is left unchanged.";
    private const string DeleteFileOrDirToolDescription =
        "Delete a file or a directory. Directory deletes are recursive.";
    private const string RenameFileOrDirToolDescription =
        "Rename or move a file or directory. Will not overwrite an existing destination.";
    private const string ApplyPatchToolDescription =
        "Use the `apply_patch` tool to edit files.";
    private const string ShellMaxOutputTokensArgument = "max_output_tokens";
    // The least output a call of shell_command can ask for: the head and the tail of each stream, and the note.
    private const int MinimumShellCommandOutputCharacters = 1_000;
    private const string ViewImageToolName = "view_image";
    private const string ViewImageToolDescription =
        "Look at a local image file, such as a screenshot, a diagram, a mockup or a photo. The image is attached to the result so that you see it. PNG, JPEG, GIF, WebP and BMP files are supported; a large image is scaled down. Use this tool instead of read_file for image files.";

    private static readonly JsonElement ViewImageSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Image file path. Relative paths resolve from the session working directory; absolute paths are accepted." }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement WriteFileSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path. Relative paths resolve from the session working directory; absolute paths are accepted." },
            "content": { "type": "string", "description": "Exact file contents to write." }
          },
          "required": ["path", "content"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ReplaceInFileSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path. Relative paths resolve from the session working directory; absolute paths are accepted." },
            "old_string": { "type": "string", "description": "Exact text to replace. Newlines are matched exactly, with deterministic normalization to the file's newline style when needed." },
            "new_string": { "type": "string", "description": "Replacement text." },
            "replace_all": { "type": "boolean", "description": "Replace every exact match. When false, the tool errors unless exactly one match exists and leaves the file unchanged." },
            "edits": {
              "type": "array",
              "description": "Several replacements in this file, instead of old_string, new_string and replace_all: applied in order, all or none.",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "old_string": { "type": "string", "description": "Exact text to replace, in the text the edits before it left." },
                  "new_string": { "type": "string", "description": "Replacement text." },
                  "replace_all": { "type": "boolean", "description": "Replace every exact match. When false, exactly one match must exist." }
                },
                "required": ["old_string", "new_string"],
                "additionalProperties": false
              }
            }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement DeleteFileOrDirSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File or directory path. Relative paths resolve from the session working directory; absolute paths are accepted." }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement RenameFileOrDirSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "old_path": { "type": "string", "description": "Existing file or directory path. Relative paths resolve from the session working directory; absolute paths are accepted." },
            "new_path": { "type": "string", "description": "Destination path. Relative paths resolve from the session working directory; absolute paths are accepted. Will not overwrite an existing destination." }
          },
          "required": ["old_path", "new_path"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ApplyPatchSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "input": {
              "type": "string",
              "description": "Patch text in the Codex/OpenAI apply_patch format. Start with '*** Begin Patch' and end with '*** End Patch'. Use '*** Add File:', '*** Delete File:', or '*** Update File:'. Paths resolve from the session working directory unless absolute, and may use '..'. An update may include '*** Move to:'. Hunks begin with '@@' or '@@ anchor text'; inside hunks use space for context, '-' for deletions, and '+' for additions."
            }
          },
          "required": ["input"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement RequestUserInputSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "prompts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "question": { "type": "string" },
                  "header": { "type": "string" },
                  "allowFreeform": { "type": "boolean" },
                  "isSecret": { "type": "boolean" },
                  "options": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "label": { "type": "string" },
                        "description": { "type": "string" }
                      },
                      "required": ["label"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["id", "question"],
                "additionalProperties": false
              }
            }
          },
          "required": ["prompts"],
          "additionalProperties": false
        }
        """);

    /// <summary>
    /// Gets the names of the built-in tools, in the order a session is given them: what a session, a space or an
    /// agent prompt can turn off.
    /// </summary>
    public static IReadOnlyList<string> BuiltInToolNames { get; } =
    [
        "read_file", "view_image", "list_dir", "grep", "webget", "shell_command", "write_file", "replace_in_file",
        "delete_file_or_dir", "rename_file_or_dir", "apply_patch", "request_user_input",
    ];

    /// <summary>
    /// Creates the default built-in tools.
    /// </summary>
    /// <param name="options">Tool options.</param>
    /// <returns>The built-in tools.</returns>
    public static IReadOnlyList<AgentToolDefinition> CreateDefaultTools(AgentBuiltInToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var httpClient = options.HttpClient ?? new HttpClient();
        AgentToolDefinition[] tools =
        [
            new AgentToolDefinition(
                new AgentToolSpec(
                    "read_file",
                    $"Read a local text file by line number. Offsets are 1-based; use a negative offset to count from the end (-1 is the last line). Offsets past EOF return an empty text result, and oversized negative offsets clamp to line 1. Omitting limit returns up to {options.DefaultReadFileLineLimit} lines by default. Each line is returned as its number, a colon, then the line as it is (`12:    return x;`).{DescribeReadFileCharacterLimits(options)}",
                    CreateReadFileSchema(options)) { RunsInParallel = true, Idempotent = true, OutputSchema = ReadFileOutputSchema },
                (invocation, cancellationToken) => ReadFileAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    ViewImageToolName,
                    ViewImageToolDescription,
                    // The model looks at the image itself: a script would receive only its description.
                    ViewImageSchema) { RunsInParallel = true, Idempotent = true, StaysDirect = true },
                (invocation, cancellationToken) => ViewImageAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "list_dir",
                    "List the direct children of a directory as [dir] and [file] entries.",
                    CreateListDirSchema()) { RunsInParallel = true, Idempotent = true },
                (invocation, cancellationToken) => ListDirectoryAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "grep",
                    $"Search one or more files or directories for line-based matches using a .NET regular expression. Recurses into directories, defaults to case-insensitive matching, skips likely-binary files, accepts one or more optional globs, and returns '(no matches)' when nothing matches.{(options.MaxGrepLineCharacters > 0 ? FormattableString.Invariant($" A matching line longer than {options.MaxGrepLineCharacters} characters is cut around its first match.") : string.Empty)}",
                    CreateGrepSchema(options)) { RunsInParallel = true, Idempotent = true },
                (invocation, cancellationToken) => GrepAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "webget",
                    $"Fetch text-like web content from a known absolute URL. On success, returns a text result containing the fetched body. By default, HTML responses are simplified to plain text; set rawHtml=true to return raw HTML markup. Set includeHttpStatus=true to prefix the HTTP status line and response content type before the body. JSON and XML are returned as text bodies. Applies content-type checks and a {options.MaxWebGetBytes.ToString(CultureInfo.InvariantCulture)}-byte limit; default timeout is {FormatSeconds(options.WebGetTimeout)} seconds.",
                    CreateWebGetSchema(options)) { RunsInParallel = true, Idempotent = true },
                (invocation, cancellationToken) => WebGetAsync(options, httpClient, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "shell_command",
                    $"Execute a local shell command or short shell script using the platform shell, subject to host approval. Some hosts may auto-approve. Returns exit_code, working_directory, stdout, and stderr as emitted by the shell and child processes; child commands may still include ANSI/control sequences. On Windows, pwsh runs with -NoProfile and preserves the final external command exit code when that command fails.{DescribeShellCommandOutputLimit(options)}",
                    CreateShellCommandSchema(options)) { OutputSchema = ShellCommandOutputSchema },
                (invocation, cancellationToken) => ShellCommandAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "write_file",
                    WriteFileToolDescription,
                    WriteFileSchema),
                (invocation, cancellationToken) => WriteFileAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "replace_in_file",
                    ReplaceInFileToolDescription,
                    ReplaceInFileSchema),
                (invocation, cancellationToken) => ReplaceInFileAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "delete_file_or_dir",
                    DeleteFileOrDirToolDescription,
                    DeleteFileOrDirSchema),
                (invocation, cancellationToken) => DeleteFileOrDirAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "rename_file_or_dir",
                    RenameFileOrDirToolDescription,
                    RenameFileOrDirSchema),
                (invocation, cancellationToken) => RenameFileOrDirAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "apply_patch",
                    ApplyPatchToolDescription,
                    ApplyPatchSchema),
                (invocation, cancellationToken) => ApplyPatchAsync(options, invocation, cancellationToken)),
        ];

        if (options.EnableUserInputTool && options.OnUserInputRequest is not null)
        {
            tools = [.. tools, new AgentToolDefinition(
                // A script cannot wait for the user: the question is the model's.
                new AgentToolSpec("request_user_input", "Request structured user input.", RequestUserInputSchema) { Exposure = AgentToolExposure.Model },
                (invocation, cancellationToken) => RequestUserInputAsync(options, invocation, cancellationToken))];
        }

        return tools
            .Where(tool => ShouldIncludeBuiltInTool(options, tool.Spec.Name))
            .ToArray();
    }

    private static Task<AgentToolResult> ReadFileAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = GetRequiredString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(Failure($"File '{resolvedPath}' was not found."));
        }

        if (AgentFileTypeDetector.IsProbablyBinaryFile(resolvedPath))
        {
            return Task.FromResult(Failure(
                AgentImagePreparation.FindFileMediaType(resolvedPath) is not null && ShouldIncludeBuiltInTool(options, ViewImageToolName)
                    ? $"'{resolvedPath}' is an image. read_file only supports text files: use {ViewImageToolName} to look at it."
                    : $"'{resolvedPath}' appears to be a binary file. read_file only supports text files."));
        }

        var offset = GetOptionalInt(invocation.Arguments, "offset") ?? 1;
        if (offset == 0)
        {
            return Task.FromResult(Failure("The 'offset' value must be >= 1, or negative to count from the end. 0 is not allowed."));
        }

        var requestedLimit = GetOptionalInt(invocation.Arguments, "limit") ?? options.DefaultReadFileLineLimit;
        var limit = Math.Clamp(requestedLimit, 1, options.MaxReadFileLineLimit);
        var startLine = offset >= 1 ? offset : GetStartLineFromEnd(resolvedPath, offset, cancellationToken);

        // The model is given numbered lines within its character limits; a script receives the lines read whole.
        var text = new StringBuilder();
        var raw = new StringBuilder();
        var lineCount = 0;
        var lineNumber = 0;
        var more = false;
        int? stoppedAtLine = null;
        foreach (var line in File.ReadLines(resolvedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < startLine)
            {
                continue;
            }

            if (lineCount >= limit)
            {
                more = true;
                break;
            }

            if (stoppedAtLine is null)
            {
                // The number, a colon, then the line as it is: no padding nor space, which every line would pay for.
                var numbered = string.Concat(lineNumber.ToString(CultureInfo.InvariantCulture), ":", CapLine(line, options.MaxReadFileLineCharacters));
                if (lineCount > 0 && options.MaxReadFileCharacters > 0
                    && text.Length + Environment.NewLine.Length + numbered.Length > options.MaxReadFileCharacters)
                {
                    stoppedAtLine = lineNumber;
                }
                else
                {
                    if (lineCount > 0)
                    {
                        text.Append(Environment.NewLine);
                    }

                    text.Append(numbered);
                }
            }

            lineCount++;
            if (raw.Length > 0)
            {
                raw.Append('\n');
            }

            raw.Append(line);
        }

        if (stoppedAtLine is { } nextLine)
        {
            text.Append(Environment.NewLine).Append(FormattableString.Invariant(
                $"[read_file stopped at {options.MaxReadFileCharacters:#,0} characters: read on with offset={nextLine}.]"));
        }

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(text.ToString())])
        {
            Structured = CreateReadFileValue(resolvedPath, startLine, startLine + lineCount - 1, raw.ToString(), more),
        });
    }

    // The line to read on from that the note of a read stopped at its limit of characters names, or null without the note.
    internal static int? ReadFileStoppedAt(string text)
        => ReadFileStopRegex().Match(text) is { Success: true } match
           && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var next)
            ? next
            : null;

    [GeneratedRegex(@"\[read_file stopped at [\d,]+ characters: read on with offset=(\d+)\.\]\z")]
    private static partial Regex ReadFileStopRegex();

    private static string DescribeReadFileCharacterLimits(AgentBuiltInToolOptions options)
        => (options.MaxReadFileLineCharacters > 0 ? FormattableString.Invariant($" Lines longer than {options.MaxReadFileLineCharacters} characters are cut.") : string.Empty)
            + (options.MaxReadFileCharacters > 0 ? FormattableString.Invariant($" A read stops before {options.MaxReadFileCharacters} characters and names the offset to read on from.") : string.Empty);

    private static string DescribeShellCommandOutputLimit(AgentBuiltInToolOptions options)
        => options.MaxShellCommandOutputCharacters > 0
            ? FormattableString.Invariant($" An output longer than {options.MaxShellCommandOutputCharacters} characters keeps the head and the tail of each stream, and the whole output is saved to a file the result names.")
            : string.Empty;

    // A line of read_file longer than the limit keeps its start, and says how long it is.
    private static string CapLine(string line, int maxCharacters)
        => maxCharacters <= 0 || line.Length <= maxCharacters
            ? line
            : string.Concat(line.AsSpan(0, maxCharacters), FormattableString.Invariant($"… [line cut: {line.Length:#,0} characters]"));

    // What a script of Code Mode receives for a read: the text of the lines read as it is, without their numbers.
    private static JsonElement CreateReadFileValue(string path, int startLine, int endLine, string content, bool more)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteNumber("startLine", startLine);
            writer.WriteNumber("endLine", Math.Max(startLine - 1, endLine));
            writer.WriteString("content", content);
            writer.WriteBoolean("more", more);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static readonly JsonElement ReadFileOutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The full path of the file." },
            "startLine": { "type": "integer" },
            "endLine": { "type": "integer" },
            "content": { "type": "string", "description": "The lines read, as they are in the file, joined by new lines." },
            "more": { "type": "boolean", "description": "Whether the file has lines after endLine: read them with offset." }
          },
          "required": ["path", "startLine", "endLine", "content", "more"]
        }
        """).RootElement.Clone();

    private static async Task<AgentToolResult> ViewImageAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = GetRequiredString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
        if (Directory.Exists(resolvedPath))
        {
            return Failure($"'{resolvedPath}' is a directory. {ViewImageToolName} reads one image file.");
        }

        if (!File.Exists(resolvedPath))
        {
            return Failure($"File '{resolvedPath}' was not found.");
        }

        var limits = AgentImageLimits.Default;
        byte[] bytes;
        try
        {
            var length = new FileInfo(resolvedPath).Length;
            if (length > limits.MaximumSourceBytes)
            {
                return Failure($"'{resolvedPath}' is too large to view ({AgentImagePreparation.FormatBytes(length)}; the limit is {AgentImagePreparation.FormatBytes(limits.MaximumSourceBytes)}).");
            }

            bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure($"'{resolvedPath}' could not be read: {exception.Message}");
        }

        // Decoding and resizing a large image takes time: it does not hold the session's thread.
        var (prepared, error) = await Task.Run(
                () => AgentImagePreparation.TryPrepare(bytes, limits, out var image, out var reason) ? (image, (string?)null) : (null, reason),
                cancellationToken)
            .ConfigureAwait(false);
        if (prepared is null)
        {
            return Failure($"'{resolvedPath}' cannot be viewed. {error}");
        }

        var size = AgentImagePreparation.FormatBytes(bytes.Length);
        var summary = prepared.Width == 0
            ? $"Viewed image {resolvedPath} ({prepared.MediaType}, {size})."
            : prepared.Resized
                ? string.Create(CultureInfo.InvariantCulture, $"Viewed image {resolvedPath} ({prepared.SourceWidth}x{prepared.SourceHeight}, {size}), scaled down to {prepared.Width}x{prepared.Height}.")
                : string.Create(CultureInfo.InvariantCulture, $"Viewed image {resolvedPath} ({prepared.Width}x{prepared.Height}, {size}).");
        return new AgentToolResult(
            true,
            [
                new AgentToolResultItem.Text(summary),
                new AgentToolResultItem.Image(
                    Convert.ToBase64String(prepared.Bytes),
                    prepared.MediaType,
                    Path.GetFileName(resolvedPath)),
            ]);
    }

    private static int GetStartLineFromEnd(string path, int offset, CancellationToken cancellationToken)
    {
        var totalLines = 0;
        foreach (var _ in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalLines++;
        }

        var startLine = totalLines + offset + 1;
        return Math.Max(1, startLine);
    }

    private static Task<AgentToolResult> ListDirectoryAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = GetOptionalString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
        if (File.Exists(resolvedPath))
        {
            return Task.FromResult(Failure($"Path '{resolvedPath}' is not a directory."));
        }

        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult(Failure($"Directory '{resolvedPath}' was not found."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var entries = Directory.EnumerateFileSystemEntries(resolvedPath)
            .Select(static entry =>
            {
                var name = Path.GetFileName(entry);
                return Directory.Exists(entry) ? $"[dir]  {name}" : $"[file] {name}";
            })
            .OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(entries.Length == 0 ? "(empty directory)" : string.Join(Environment.NewLine, entries))]));
    }

    private static Task<AgentToolResult> GrepAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(invocation.Arguments, "pattern");
        if (!TryResolveGrepTargetPaths(options.WorkingDirectory, invocation.Arguments, out var targetPaths, out var targetPathError))
        {
            return Task.FromResult(Failure(targetPathError));
        }

        foreach (var targetPath in targetPaths)
        {
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return Task.FromResult(Failure($"Path '{targetPath}' was not found."));
            }
        }

        if (!TryGetGrepGlobPatterns(invocation.Arguments, out var globPatterns, out var globError))
        {
            return Task.FromResult(Failure(globError));
        }

        var caseSensitive = GetOptionalBool(invocation.Arguments, "caseSensitive") ?? false;
        var maxMatches = Math.Clamp(GetOptionalInt(invocation.Arguments, "maxMatches") ?? options.MaxGrepMatches, 1, options.MaxGrepMatches);
        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                matchTimeout: TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(Failure($"Invalid regular expression '{pattern}': {ex.Message}"));
        }

        var matches = new List<string>(maxMatches);

        var visitedFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var targetPath in targetPaths)
        {
            IEnumerable<FileTreeEntry>? entries = null;
            if (Directory.Exists(targetPath))
            {
                var walkOptions = new FileTreeWalkOptions
                {
                    CancellationToken = cancellationToken,
                    RepositoryContext = RepositoryDiscovery.TryDiscover(targetPath, out var repositoryContext) ? repositoryContext : null,
                };
                entries = FileTreeWalker.Enumerate(targetPath, walkOptions);
            }

            foreach (var file in EnumerateSearchFiles(targetPath, entries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedFiles.Add(file.FullPath))
                {
                    continue;
                }

                if (globPatterns.Length > 0 && !GlobMatches(globPatterns, file))
                {
                    continue;
                }

                if (IsImagePath(file.FullPath))
                {
                    continue;
                }

                try
                {
                    if (AgentFileTypeDetector.IsProbablyBinaryFile(file.FullPath))
                    {
                        continue;
                    }

                    SearchFileLines(file.FullPath, file.DisplayPath, regex, maxMatches, options.MaxGrepLineCharacters, matches, cancellationToken);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                if (matches.Count >= maxMatches)
                {
                    break;
                }
            }

            if (matches.Count >= maxMatches)
            {
                break;
            }
        }

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(matches.Count == 0 ? "(no matches)" : JoinGrepMatches(matches, options.MaxGrepCharacters))]));
    }

    // The matches that fit the limit, then a line that says how many were left out.
    private static string JoinGrepMatches(List<string> matches, int maxCharacters)
    {
        var builder = new StringBuilder();
        var shown = 0;
        foreach (var match in matches)
        {
            var length = (shown > 0 ? Environment.NewLine.Length : 0) + match.Length;
            if (shown > 0 && maxCharacters > 0 && builder.Length + length > maxCharacters)
            {
                break;
            }

            if (shown > 0)
            {
                builder.Append(Environment.NewLine);
            }

            builder.Append(match);
            shown++;
        }

        if (shown < matches.Count)
        {
            builder.Append(Environment.NewLine).Append(FormattableString.Invariant(
                $"[grep stopped at {maxCharacters:#,0} characters: {matches.Count - shown:#,0} more of the first {matches.Count:#,0} matches left out. Narrow the pattern, the path or the glob.]"));
        }

        return builder.ToString();
    }

    // A matching line longer than the limit keeps the part around its first match, and says how long it is.
    private static string CapMatchingLine(string line, Match match, int maxCharacters)
    {
        if (maxCharacters <= 0 || line.Length <= maxCharacters)
        {
            return line;
        }

        var start = Math.Clamp(match.Index - (maxCharacters / 4), 0, line.Length - maxCharacters);
        var end = start + maxCharacters;
        return string.Concat(
            start > 0 ? "…" : string.Empty,
            line.AsSpan(start, maxCharacters),
            end < line.Length ? "…" : string.Empty,
            FormattableString.Invariant($" [line cut: {line.Length:#,0} characters]"));
    }

    private static async Task<AgentToolResult> WebGetAsync(
        AgentBuiltInToolOptions options,
        HttpClient httpClient,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var url = GetRequiredString(invocation.Arguments, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Failure($"'{url}' is not a valid absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Failure($"The '{uri.Scheme}' scheme is not supported by webget. Use http or https.");
        }

        var timeoutOverride = GetOptionalInt(invocation.Arguments, "timeoutSeconds");
        var rawHtml = GetOptionalBool(invocation.Arguments, "rawHtml") ?? false;
        var includeHttpStatus = GetOptionalBool(invocation.Arguments, "includeHttpStatus") ?? false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveTimeout = timeoutOverride is > 0 ? TimeSpan.FromSeconds(timeoutOverride.Value) : options.WebGetTimeout;
        linkedCts.CancelAfter(effectiveTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reasonPhrase = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                    ? "Unknown Status"
                    : response.ReasonPhrase;
                return Failure(
                    $"webget request to '{uri}' failed with HTTP {(int)response.StatusCode} ({reasonPhrase}).");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !IsSupportedWebContentType(mediaType))
            {
                return Failure($"Content type '{mediaType}' is not supported by webget. Supported types are {SupportedWebContentTypesDescription}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(capacity: options.MaxWebGetBytes);
            var rented = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(rented, linkedCts.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > options.MaxWebGetBytes)
                {
                    return Failure($"Response exceeded the {options.MaxWebGetBytes.ToString(CultureInfo.InvariantCulture)} byte limit enforced by webget.");
                }

                buffer.Write(rented, 0, read);
            }

            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (!rawHtml &&
                (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            {
                text = SimplifyHtml(text);
            }

            if (includeHttpStatus)
            {
                text = FormatWebGetSuccessResponse(response, text);
            }

            var trimmedText = text.Trim();
            return new AgentToolResult(
                true,
                [new AgentToolResultItem.Text(trimmedText.Length == 0 ? "(empty response body)" : trimmedText)]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(string.Create(CultureInfo.InvariantCulture, $"webget timed out after {effectiveTimeout.TotalSeconds:0.###} seconds."));
        }
        catch (HttpRequestException ex)
        {
            return Failure($"webget failed to fetch '{uri}': {ex.Message}");
        }
    }

    private static async Task<AgentToolResult> ShellCommandAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var command = GetRequiredString(invocation.Arguments, "command");
        var workdir = ResolvePath(options.WorkingDirectory, GetOptionalString(invocation.Arguments, "workdir"));
        if (!Directory.Exists(workdir))
        {
            return Failure($"Directory '{workdir}' was not found.");
        }

        var timeout = GetOptionalInt(invocation.Arguments, "timeoutMs");
        var login = GetOptionalBool(invocation.Arguments, "login") ?? false;

        // Checked before any permission: a command the host refuses is refused in every mode.
        if (options.CommandGuard is { } guard && await guard(command, workdir, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Failure(refusal);
        }

        var permissionRequest = new AgentCommandPermissionRequest(
            options.ProviderId,
            options.SessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: invocation.ToolCallId,
            ApprovalId: null,
            Command: command,
            WorkingDirectory: workdir,
            Actions: null,
            Reason: "The agent requested local shell execution.",
            Network: null,
            ProposedExecPolicyAmendment: null,
            ProposedNetworkPolicyAmendments: null);

        var decision = await options.OnPermissionRequest(permissionRequest, cancellationToken).ConfigureAwait(false);
        switch (decision.Kind)
        {
            case AgentPermissionDecisionKind.AllowOnce:
            case AgentPermissionDecisionKind.AllowForSession:
                break;
            case AgentPermissionDecisionKind.Deny:
                return Failure("shell_command was denied by the host.");
            case AgentPermissionDecisionKind.Cancel:
                return Failure("shell_command was canceled by the host.");
            default:
                return Failure($"Unsupported permission decision '{decision.Kind}'.");
        }

        var processSpec = CreateShellProcessSpec(command, workdir, login, options.Environment);
        using var process = new Process
        {
            StartInfo = processSpec.StartInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                return Failure("shell_command did not start a process.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Failure($"shell_command failed to start: {ex.Message}");
        }

        // A child may retain the redirected pipes after the shell exits. Keep the same
        // deadline alive until both streams finish, not merely until WaitForExitAsync.
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is > 0)
        {
            executionCts.CancelAfter(timeout.Value);
        }

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = PumpProcessStreamAsync(
            process.StandardOutput,
            "stdout",
            stdoutBuilder,
            invocation.Progress,
            executionCts.Token);
        var stderrTask = PumpProcessStreamAsync(
            process.StandardError,
            "stderr",
            stderrBuilder,
            invocation.Progress,
            executionCts.Token);
        var outputTask = Task.WhenAll(stdoutTask, stderrTask);

        try
        {
            await process.WaitForExitAsync(executionCts.Token).ConfigureAwait(false);
            await outputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
        {
            TryKill(process);
            // Killing only asks the system to end the shell: wait a bounded time for it to be gone, so the tool does not report
            // the end of a command whose shell still runs in its folder (and holds it as its working directory).
            await WaitForKilledProcessAsync(process).ConfigureAwait(false);
            // Join both canceled readers before disposing their process/stream owners.
            // Do not try to rediscover orphaned children by PID after the shell has exited.
            try
            {
                await outputTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }

            return Failure(!cancellationToken.IsCancellationRequested && timeout is > 0
                ? $"shell_command timed out after {timeout.Value} ms."
                : "shell_command was canceled.");
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        var output = FormatShellCommandOutput(process.ExitCode, stdout, stderr, workdir);
        var structured = CreateShellCommandValue(process.ExitCode, stdout, stderr, workdir);
        // A long output keeps its head and tail for the model; the user and a script have the whole of it.
        string? display = null;
        var limit = ShellCommandOutputLimit(options, GetOptionalInt(invocation.Arguments, ShellMaxOutputTokensArgument));
        if (limit > 0 && output.Length > limit)
        {
            display = output;
            output = await CutShellCommandOutputAsync(options, limit, invocation.ToolCallId, process.ExitCode, stdout, stderr, workdir, output, cancellationToken)
                .ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            return new AgentToolResult(
                false,
                [new AgentToolResultItem.Text(output)],
                $"shell_command exited with code {process.ExitCode}.")
            { Structured = structured, Display = display };
        }

        return new AgentToolResult(true, [new AgentToolResultItem.Text(output)]) { Structured = structured, Display = display };
    }

    // The characters of the output the model is given: the limit of the options, which the call can lower (never raise)
    // with a budget in tokens of about four characters, down to a floor that still shows the head, the tail and the note.
    private static int ShellCommandOutputLimit(AgentBuiltInToolOptions options, int? maxOutputTokens)
    {
        if (maxOutputTokens is not > 0)
        {
            return options.MaxShellCommandOutputCharacters;
        }

        var asked = (int)Math.Clamp(maxOutputTokens.Value * 4L, MinimumShellCommandOutputCharacters, int.MaxValue);
        return options.MaxShellCommandOutputCharacters > 0 ? Math.Min(asked, options.MaxShellCommandOutputCharacters) : asked;
    }

    private static async Task<string> CutShellCommandOutputAsync(
        AgentBuiltInToolOptions options,
        int limit,
        string toolCallId,
        int exitCode,
        string stdout,
        string stderr,
        string workdir,
        string output,
        CancellationToken cancellationToken)
    {
        string note;
        try
        {
            var path = await SaveToolOutputAsync(options, "shell", toolCallId, output, cancellationToken).ConfigureAwait(false);
            note = FormattableString.Invariant(
                $"the output has {output.Length:#,0} characters: the head and the tail of each stream are shown, and the whole output is in {path}; read it with read_file (offset, limit) or grep.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            note = FormattableString.Invariant(
                $"the output has {output.Length:#,0} characters: the head and the tail of each stream are shown; the whole output could not be saved ({ex.Message}).");
        }

        var overhead = FormatShellCommandOutput(exitCode, string.Empty, string.Empty, workdir, note).Length;
        // Each marker of a cut takes about 80 characters of the budget.
        var budget = Math.Max(limit - overhead - 200, 0);
        var stdoutBudget = budget;
        var stderrBudget = budget;
        if (stdout.Length + stderr.Length > budget)
        {
            var half = budget / 2;
            (stdoutBudget, stderrBudget) = stdout.Length <= half ? (stdout.Length, budget - stdout.Length)
                : stderr.Length <= half ? (budget - stderr.Length, stderr.Length)
                : (half, budget - half);
        }

        return FormatShellCommandOutput(exitCode, KeepHeadAndTail(stdout, stdoutBudget), KeepHeadAndTail(stderr, stderrBudget), workdir, note);
    }

    // Keeps two fifths of the budget from the start of the text and the rest from its end, cut at line ends where it can:
    // what a command prints last (errors, a summary) often matters most.
    private static string KeepHeadAndTail(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var headLength = maxCharacters * 2 / 5;
        var tailLength = maxCharacters - headLength;
        var headEnd = text.LastIndexOf('\n', Math.Max(headLength - 1, 0));
        if (headEnd < headLength / 2)
        {
            headEnd = headLength;
        }

        var tailStart = text.IndexOf('\n', text.Length - tailLength);
        if (tailStart < 0 || tailStart - (text.Length - tailLength) > tailLength / 2)
        {
            tailStart = text.Length - tailLength;
        }
        else
        {
            tailStart++;
        }

        var cut = text.AsSpan(headEnd, tailStart - headEnd);
        var cutLines = cut.Count('\n');
        return string.Concat(
            text.AsSpan(0, headEnd).TrimEnd(),
            FormattableString.Invariant($"{Environment.NewLine}[... {cut.Length:#,0} characters ({cutLines:#,0} lines) cut ...]{Environment.NewLine}"),
            text.AsSpan(tailStart));
    }

    // Saves the whole output of a call to a file of the session's tool output folder, and returns its path.
    private static async Task<string> SaveToolOutputAsync(
        AgentBuiltInToolOptions options,
        string toolName,
        string toolCallId,
        string output,
        CancellationToken cancellationToken)
    {
        var folder = options.ResolveToolOutputDirectoryAsync is { } resolve
            ? await resolve(cancellationToken).ConfigureAwait(false)
            : null;
        folder ??= Path.Combine(Path.GetTempPath(), "codealta", "tool-output", SafeFileName(options.SessionId));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{toolName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{SafeFileName(toolCallId)}.txt");
        await File.WriteAllTextAsync(path, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string SafeFileName(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var character in value)
        {
            if (builder.Length == 64)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }

        return builder.Length == 0 ? Guid.NewGuid().ToString("N") : builder.ToString();
    }

    private static async Task<AgentToolResult> WriteFileAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetWorkingDirectoryRoot(options);
        WorkspacePathResolution resolution;
        try
        {
            resolution = ResolveWorkspacePath(rootPath, GetRequiredString(invocation.Arguments, "path"));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        var existed = File.Exists(resolution.FullPath);
        if (Directory.Exists(resolution.FullPath))
        {
            return Failure($"'{resolution.DisplayPath}' is an existing directory.");
        }

        var content = GetRequiredStringValue(invocation.Arguments, "content");
        var permission = await RequestFileChangePermissionAsync(
                options,
                invocation,
                "write_file",
                [resolution.FullPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (permission is not null)
        {
            return permission;
        }

        var parentDirectory = Path.GetDirectoryName(resolution.FullPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        await File.WriteAllTextAsync(resolution.FullPath, content, cancellationToken).ConfigureAwait(false);
        var verb = existed ? "Wrote" : "Created";
        return SuccessResult($"{verb} {resolution.DisplayPath}");
    }

    private static async Task<AgentToolResult> ReplaceInFileAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetWorkingDirectoryRoot(options);
        WorkspacePathResolution resolution;
        try
        {
            resolution = ResolveWorkspacePath(rootPath, GetRequiredString(invocation.Arguments, "path"));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        if (!File.Exists(resolution.FullPath))
        {
            return Failure($"File '{resolution.DisplayPath}' was not found.");
        }

        if (AgentFileTypeDetector.IsProbablyBinaryFile(resolution.FullPath))
        {
            return Failure($"replace_in_file does not support binary files: '{resolution.DisplayPath}'.");
        }

        if (ReadReplacements(invocation.Arguments) is not { } edits)
        {
            return Failure("replace_in_file takes either old_string and new_string, or an edits list of objects with a non-empty old_string and a new_string, not both.");
        }

        if (edits.Exists(static edit => edit.OldString.Length == 0))
        {
            return Failure(edits.Count == 1 && !invocation.Arguments.TryGetProperty("edits", out _)
                ? "The 'old_string' value must not be empty."
                : "The 'old_string' value of every edit must not be empty.");
        }

        var original = await File.ReadAllTextAsync(resolution.FullPath, cancellationToken).ConfigureAwait(false);
        var newline = DetectExistingNewline(original);
        // The edits apply in their order, each to the text the edits before it left; one that fails changes nothing.
        var updated = original;
        var replacedCount = 0;
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var replacementTarget = edit.OldString;
            var replacementValue = edit.NewString;
            if (!updated.Contains(replacementTarget, StringComparison.Ordinal))
            {
                var normalizedOldString = NormalizeNewlines(edit.OldString, newline);
                if (!string.Equals(normalizedOldString, edit.OldString, StringComparison.Ordinal) &&
                    updated.Contains(normalizedOldString, StringComparison.Ordinal))
                {
                    replacementTarget = normalizedOldString;
                    replacementValue = NormalizeNewlines(edit.NewString, newline);
                }
            }

            var matchCount = CountOccurrences(updated, replacementTarget);
            var which = edits.Count == 1 ? string.Empty : FormattableString.Invariant($" (edit {index + 1} of {edits.Count}, after the edits before it; no edit was made)");
            if (matchCount == 0)
            {
                return Failure($"replace_in_file could not find the requested text in '{resolution.DisplayPath}'{which}.");
            }

            if (!edit.ReplaceAll && matchCount != 1)
            {
                return Failure(
                    $"replace_in_file found {matchCount} matches in '{resolution.DisplayPath}'{which}. Set replace_all=true to replace every match.");
            }

            updated = edit.ReplaceAll
                ? updated.Replace(replacementTarget, replacementValue, StringComparison.Ordinal)
                : ReplaceFirstOccurrence(updated, replacementTarget, replacementValue);
            replacedCount += edit.ReplaceAll ? matchCount : 1;
        }

        var permission = await RequestFileChangePermissionAsync(
                options,
                invocation,
                "replace_in_file",
                [resolution.FullPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (permission is not null)
        {
            return permission;
        }

        await File.WriteAllTextAsync(resolution.FullPath, updated, cancellationToken).ConfigureAwait(false);
        return SuccessResult(edits.Count == 1
            ? $"Replaced {replacedCount} occurrence(s) in {resolution.DisplayPath}"
            : $"Made {edits.Count} edits, {replacedCount} occurrence(s), in {resolution.DisplayPath}");
    }

    // One replacement of replace_in_file.
    private sealed record Replacement(string OldString, string NewString, bool ReplaceAll);

    // The replacements a call asks for: its old_string and new_string, or its edits list; null when it gives both, or a
    // list that is empty or has an item that is not a replacement. A missing old_string or new_string throws, as it did.
    private static List<Replacement>? ReadReplacements(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("edits", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [new Replacement(GetRequiredStringValue(arguments, "old_string"), GetRequiredStringValue(arguments, "new_string"),
                GetOptionalBool(arguments, "replace_all") ?? false)];
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0
            || arguments.TryGetProperty("old_string", out _) || arguments.TryGetProperty("new_string", out _))
        {
            return null;
        }

        var edits = new List<Replacement>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || GetOptionalString(item, "old_string") is not { } oldString
                || GetOptionalString(item, "new_string") is not { } newString)
            {
                return null;
            }

            edits.Add(new Replacement(oldString, newString, GetOptionalBool(item, "replace_all") ?? false));
        }

        return edits;
    }

    private static async Task<AgentToolResult> DeleteFileOrDirAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetWorkingDirectoryRoot(options);
        WorkspacePathResolution resolution;
        try
        {
            resolution = ResolveWorkspacePath(rootPath, GetRequiredString(invocation.Arguments, "path"));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        if (string.Equals(resolution.FullPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("delete_file_or_dir cannot delete the session working directory.");
        }

        var existsAsFile = File.Exists(resolution.FullPath);
        var existsAsDirectory = Directory.Exists(resolution.FullPath);
        if (!existsAsFile && !existsAsDirectory)
        {
            return Failure($"Path '{resolution.DisplayPath}' was not found.");
        }

        var permission = await RequestFileChangePermissionAsync(
                options,
                invocation,
                "delete_file_or_dir",
                [resolution.FullPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (permission is not null)
        {
            return permission;
        }

        try
        {
            if (existsAsDirectory)
            {
                Directory.Delete(resolution.FullPath, recursive: true);
                return SuccessResult($"Deleted directory {resolution.DisplayPath}");
            }

            File.Delete(resolution.FullPath);
            return SuccessResult($"Deleted file {resolution.DisplayPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure($"delete_file_or_dir failed for '{resolution.DisplayPath}': {ex.Message}");
        }
    }

    private static async Task<AgentToolResult> RenameFileOrDirAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetWorkingDirectoryRoot(options);
        WorkspacePathResolution sourceResolution;
        WorkspacePathResolution destinationResolution;
        try
        {
            sourceResolution = ResolveWorkspacePath(rootPath, GetRequiredString(invocation.Arguments, "old_path"));
            destinationResolution = ResolveWorkspacePath(rootPath, GetRequiredString(invocation.Arguments, "new_path"));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        if (string.Equals(sourceResolution.FullPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("rename_file_or_dir cannot move the session working directory.");
        }

        if (string.Equals(sourceResolution.FullPath, destinationResolution.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("rename_file_or_dir requires different source and destination paths.");
        }

        var sourceIsFile = File.Exists(sourceResolution.FullPath);
        var sourceIsDirectory = Directory.Exists(sourceResolution.FullPath);
        if (!sourceIsFile && !sourceIsDirectory)
        {
            return Failure($"Source path '{sourceResolution.DisplayPath}' was not found.");
        }

        if (File.Exists(destinationResolution.FullPath) || Directory.Exists(destinationResolution.FullPath))
        {
            return Failure($"Destination '{destinationResolution.DisplayPath}' already exists.");
        }

        var permission = await RequestFileChangePermissionAsync(
                options,
                invocation,
                "rename_file_or_dir",
                [sourceResolution.FullPath, destinationResolution.FullPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (permission is not null)
        {
            return permission;
        }

        try
        {
            var destinationParent = Path.GetDirectoryName(destinationResolution.FullPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            if (sourceIsDirectory)
            {
                Directory.Move(sourceResolution.FullPath, destinationResolution.FullPath);
                return SuccessResult($"Renamed directory {sourceResolution.DisplayPath} -> {destinationResolution.DisplayPath}");
            }

            File.Move(sourceResolution.FullPath, destinationResolution.FullPath);
            return SuccessResult($"Renamed file {sourceResolution.DisplayPath} -> {destinationResolution.DisplayPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(
                $"rename_file_or_dir failed for '{sourceResolution.DisplayPath}' -> '{destinationResolution.DisplayPath}': {ex.Message}");
        }
    }

    private static async Task<AgentToolResult> ApplyPatchAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        var patchInput = GetRequiredString(invocation.Arguments, "input");
        IReadOnlyList<string> touchedPaths;
        try
        {
            touchedPaths = AgentApplyPatch.GetTouchedPaths(patchInput, workingDirectory);
        }
        catch (InvalidOperationException)
        {
            touchedPaths = [];
        }

        var permission = await RequestFileChangePermissionAsync(
                options,
                invocation,
                "apply_patch",
                touchedPaths,
                cancellationToken)
            .ConfigureAwait(false);
        if (permission is not null)
        {
            return permission;
        }

        try
        {
            return AgentApplyPatch.Apply(patchInput, workingDirectory);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
    }

    private static async Task<AgentToolResult> RequestUserInputAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (options.OnUserInputRequest is null)
        {
            return Failure("No user-input handler is configured for this session.");
        }

        var promptsElement = invocation.Arguments.TryGetProperty("prompts", out var prompts)
            ? prompts
            : throw new ArgumentException("The 'prompts' field is required.", nameof(invocation));
        if (promptsElement.ValueKind != JsonValueKind.Array)
        {
            return Failure("The 'prompts' field must be an array.");
        }

        var mappedPrompts = new List<AgentUserInputPrompt>();
        foreach (var prompt in promptsElement.EnumerateArray())
        {
            var id = GetRequiredString(prompt, "id");
            var question = GetRequiredString(prompt, "question");
            var header = GetOptionalString(prompt, "header");
            var allowFreeform = GetOptionalBool(prompt, "allowFreeform") ?? true;
            var isSecret = GetOptionalBool(prompt, "isSecret") ?? false;
            var optionsList = prompt.TryGetProperty("options", out var promptOptions) && promptOptions.ValueKind == JsonValueKind.Array
                ? promptOptions.EnumerateArray()
                    .Select(static option => new AgentUserInputOption(
                        GetRequiredString(option, "label"),
                        GetOptionalString(option, "description")))
                    .ToArray()
                : null;
            mappedPrompts.Add(new AgentUserInputPrompt(id, question, header, optionsList, allowFreeform, isSecret));
        }

        var request = new AgentUserInputRequest(
            options.ProviderId,
            options.SessionId,
            DateTimeOffset.UtcNow,
            null,
            $"tool-input:{Guid.NewGuid():N}",
            new AgentUserInputForm(mappedPrompts));
        var response = await options.OnUserInputRequest(request, cancellationToken).ConfigureAwait(false);

        var json = SerializeAnswers(response.Answers);
        return new AgentToolResult(true, [new AgentToolResultItem.Text(json)]);
    }

    private static JsonElement ParseSchema([StringSyntax("json")] string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    private static AgentToolResult SuccessResult(string message)
        => new(true, [new AgentToolResultItem.Text(message)]);

    private static bool ShouldIncludeBuiltInTool(AgentBuiltInToolOptions options, string toolName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var included = options.Provider?.Profile?.BuiltInToolOverrides is { Count: > 0 } overrides && overrides.TryGetValue(toolName, out var enabled)
            ? enabled
            : !string.Equals(toolName, "apply_patch", StringComparison.Ordinal) || IsApplyPatchSupported(options.Provider, options.ProviderId);
        // After the provider: what the session turns off is off, whatever its provider would offer.
        return included && options.DisabledTools?.Contains(toolName) != true;
    }

    private static bool IsApplyPatchSupported(ModelProviderRuntimeDescriptor? provider, ModelProviderId ProviderId)
    {
        if (provider is null)
        {
            return ProviderId == ModelProviderIds.OpenAIResponses || ProviderId == ModelProviderIds.OpenAIChat;
        }

        if (provider.TransportKind is not (AgentTransportKind.OpenAIResponses or AgentTransportKind.OpenAIChatCompletions))
        {
            return false;
        }

        if (string.Equals(provider.ProtocolFamily, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (provider.BaseUri is null)
        {
            return true;
        }

        return string.Equals(provider.BaseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWorkingDirectoryRoot(AgentBuiltInToolOptions options)
        => Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);

    private static WorkspacePathResolution ResolveWorkspacePath(string rootPath, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A non-empty path is required.");
        }

        var fullPath = ResolvePath(rootPath, path);
        var displayPath = Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return new WorkspacePathResolution(fullPath, displayPath);
    }

    private static async Task<AgentToolResult?> RequestFileChangePermissionAsync(
        AgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        string toolName,
        IReadOnlyList<string> touchedPaths,
        CancellationToken cancellationToken)
    {
        // A session without the shell writes nothing that would run a command later or give it back its tools.
        if (options.WriteGuard is { } guard)
        {
            if (touchedPaths.Count == 0)
            {
                return Failure($"{toolName} could not tell which files it changes. `shell_command` is turned off for this session: name the files plainly.");
            }

            // A folder that is deleted or moved takes what it holds with it.
            foreach (var touched in touchedPaths)
            {
                if (guard.CheckTree(touched) is { } refusal)
                {
                    return Failure($"{toolName} was refused: {refusal}");
                }
            }
        }

        var workingDirectory = GetWorkingDirectoryRoot(options);
        var permissionRequest = new AgentFileChangePermissionRequest(
            options.ProviderId,
            options.SessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: invocation.ToolCallId,
            GrantRoot: workingDirectory,
            Reason: touchedPaths.Count == 0
                ? $"The agent requested filesystem edits via {toolName}."
                : $"The agent requested filesystem edits via {toolName} affecting {touchedPaths.Count} path(s).");
        var decision = await options.OnPermissionRequest(permissionRequest, cancellationToken).ConfigureAwait(false);
        return decision.Kind switch
        {
            AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession => null,
            AgentPermissionDecisionKind.Deny => Failure($"{toolName} was denied by the host."),
            AgentPermissionDecisionKind.Cancel => Failure($"{toolName} was canceled by the host."),
            _ => Failure($"Unsupported permission decision '{decision.Kind}'."),
        };
    }

    private static string DetectExistingNewline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string NormalizeNewlines(string text, string newline)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirstOccurrence(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    private static string ResolvePath(string? workingDirectory, string? path)
    {
        var candidate = string.IsNullOrWhiteSpace(path) ? workingDirectory : path;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("A path or working directory is required.");
        }

        var baseDirectory = workingDirectory ?? Environment.CurrentDirectory;
        // Path-like tool arguments should resolve relative inputs from the session working directory
        // while still accepting rooted inputs unchanged.
        return Path.GetFullPath(Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(baseDirectory, candidate));
    }

    private static bool IsImagePath(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private static string SimplifyHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    private static bool GlobMatches(GlobPattern globPattern, FileTreeEntry entry, bool useRelativePath)
        => useRelativePath
            ? globPattern.IsMatch(entry.RelativePath)
            : globPattern.IsMatch(entry.Name);

    private static bool GlobMatches(IReadOnlyList<SearchGlobPattern> globPatterns, SearchFileTarget entry)
    {
        foreach (var globPattern in globPatterns)
        {
            if (GlobMatches(globPattern.Pattern, entry, globPattern.UseRelativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool GlobMatches(GlobPattern globPattern, SearchFileTarget entry, bool useRelativePath)
        => useRelativePath
            ? globPattern.IsMatch(entry.RelativePath)
            : globPattern.IsMatch(entry.Name);

    private static bool TryResolveGrepTargetPaths(
        string? workingDirectory,
        JsonElement arguments,
        out string[] targetPaths,
        [NotNullWhen(false)] out string? errorMessage)
    {
        targetPaths = [];
        errorMessage = null;

        if (!arguments.TryGetProperty("path", out var pathElement) || pathElement.ValueKind == JsonValueKind.Null)
        {
            targetPaths = [ResolvePath(workingDirectory, null)];
            return true;
        }

        if (pathElement.ValueKind == JsonValueKind.String)
        {
            targetPaths = [ResolvePath(workingDirectory, pathElement.GetString())];
            return true;
        }

        if (pathElement.ValueKind != JsonValueKind.Array)
        {
            errorMessage = "The 'path' field must be a string or an array of strings.";
            return false;
        }

        var paths = new List<string>();
        foreach (var item in pathElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errorMessage = "The 'path' field must be a string or an array of strings.";
                return false;
            }

            var path = item.GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "The 'path' array must not contain empty paths.";
                return false;
            }

            paths.Add(ResolvePath(workingDirectory, path));
        }

        if (paths.Count == 0)
        {
            errorMessage = "The 'path' array must include at least one path.";
            return false;
        }

        targetPaths = DeduplicatePaths(paths);
        return true;
    }

    private static bool TryGetGrepGlobPatterns(
        JsonElement arguments,
        out SearchGlobPattern[] globPatterns,
        [NotNullWhen(false)] out string? errorMessage)
    {
        globPatterns = [];
        errorMessage = null;

        if (!arguments.TryGetProperty("glob", out var globElement) || globElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var patterns = new List<SearchGlobPattern>();
        if (globElement.ValueKind == JsonValueKind.String)
        {
            if (!TryAddGrepGlobPattern(globElement.GetString(), patterns, out errorMessage))
            {
                return false;
            }

            globPatterns = patterns.ToArray();
            return true;
        }

        if (globElement.ValueKind != JsonValueKind.Array)
        {
            errorMessage = "The 'glob' field must be a string or an array of strings.";
            return false;
        }

        foreach (var item in globElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errorMessage = "The 'glob' field must be a string or an array of strings.";
                return false;
            }

            if (!TryAddGrepGlobPattern(item.GetString(), patterns, out errorMessage))
            {
                return false;
            }
        }

        globPatterns = patterns.ToArray();
        return true;
    }

    private static bool TryAddGrepGlobPattern(
        string? glob,
        List<SearchGlobPattern> patterns,
        [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(glob))
        {
            return true;
        }

        var parseResult = GlobPattern.TryParse(glob);
        if (!parseResult.Success)
        {
            errorMessage = $"Invalid glob pattern '{glob}'.";
            return false;
        }

        patterns.Add(new SearchGlobPattern(parseResult.Pattern!, glob.Contains('/') || glob.Contains('\\')));
        return true;
    }

    private static string[] DeduplicatePaths(IReadOnlyList<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var uniquePaths = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (seen.Add(path))
            {
                uniquePaths.Add(path);
            }
        }

        return uniquePaths.ToArray();
    }

    private static IEnumerable<SearchFileTarget> EnumerateSearchFiles(
        string targetPath,
        IEnumerable<FileTreeEntry>? directoryEntries)
    {
        if (File.Exists(targetPath))
        {
            var fileName = Path.GetFileName(targetPath);
            yield return new SearchFileTarget(targetPath, fileName, fileName, fileName);
            yield break;
        }

        foreach (var entry in directoryEntries ?? [])
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            yield return new SearchFileTarget(entry.FullPath, entry.RelativePath, entry.RelativePath, entry.Name);
        }
    }

    private static void SearchFileLines(
        string fullPath,
        string displayPath,
        Regex regex,
        int maxMatches,
        int maxLineCharacters,
        List<string> matches,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(fullPath);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            matches.Add($"{displayPath}:{lineNumber}: {CapMatchingLine(line, match, maxLineCharacters)}");
            if (matches.Count >= maxMatches)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Creates what starts a command in the shell <c>shell_command</c> uses, for a host that runs a command by itself
    /// (a background job): the same interpreter, arguments and environment, with both outputs redirected.
    /// </summary>
    /// <param name="command">The command line.</param>
    /// <param name="workdir">The folder the command runs in.</param>
    /// <param name="environment">The variables of the session the command is run for; null for none.</param>
    /// <returns>The start information; the caller sets what it needs more (encodings, a closed input).</returns>
    internal static ProcessStartInfo CreateShellStartInfo(string command, string workdir, AgentProcessEnvironment? environment = null)
        => CreateShellProcessSpec(command, workdir, login: false, environment).StartInfo;

    private static ShellProcessSpec CreateShellProcessSpec(string command, string workdir, bool login, AgentProcessEnvironment? environment = null)
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer pwsh (PowerShell Core) when available; fall back to the built-in
            // powershell.exe so machines without pwsh can still run shell commands.
            var fileName = WindowsShellFileName.Value;
            // Always suppress the user profile on Windows so prompt theming and other profile-time output
            // cannot leak ANSI/control sequences into tool results. The login flag is Unix-oriented here.
            // Wrap the command so a failing final external command can propagate its native exit code
            // instead of PowerShell collapsing it to 1.
            string[] shellArguments = ["-NoProfile", "-Command", WrapWindowsShellCommand(command)];
            return new ShellProcessSpec(CreateProcessStartInfo(fileName, shellArguments, workdir, environment));
        }

        var shellPath = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shellPath))
        {
            shellPath = "/bin/sh";
        }

        var shellFileName = Path.GetFileName(shellPath);
        string[] arguments;
        if (login && shellFileName is "bash" or "zsh")
        {
            arguments = ["-lc", command];
        }
        else
        {
            arguments = ["-c", command];
        }

        return new ShellProcessSpec(CreateProcessStartInfo(shellPath, arguments, workdir, environment));
    }

    internal static string ResolveWindowsShellFileName(string? pathValue)
    {
        if (IsExecutableOnPath("pwsh.exe", pathValue))
        {
            return "pwsh";
        }

        if (IsExecutableOnPath("powershell.exe", pathValue))
        {
            return "powershell";
        }

        // Keep the historical fallback name so Process.Start reports the platform error
        // if neither shell is discoverable through PATH on an unusual Windows install.
        return "powershell";
    }

    internal static string GetWindowsShellFileNameForCurrentProcess()
        => WindowsShellFileName.Value;

    private static bool IsExecutableOnPath(string fileName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedDirectory = directory.Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                continue;
            }

            try
            {
                if (File.Exists(Path.Combine(normalizedDirectory, fileName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (PathTooLongException)
            {
            }
        }

        return false;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IReadOnlyList<string> arguments, string workdir, AgentProcessEnvironment? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The identity of the session, such as its Git author: over the environment of the application.
        environment?.ApplyTo(startInfo.Environment);
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CLICOLOR"] = "0";
        // Unset rather than "0": the tools of macOS and BSD (`ls`) force colors whenever it is set.
        startInfo.Environment.Remove("CLICOLOR_FORCE");

        return startInfo;
    }

    private static JsonElement CreateReadFileSchema(AgentBuiltInToolOptions options)
        => ParseSchema(
            $$"""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path to the file to read." },
                "offset": { "type": "integer", "description": "1-based line offset. Use a negative value to count from the end (-1 is the last line). 0 is invalid. Offsets past EOF return an empty text result, and oversized negative offsets clamp to line 1." },
                "limit": { "type": "integer", "description": "Maximum number of lines to return. Defaults to {{options.DefaultReadFileLineLimit}} and is capped at {{options.MaxReadFileLineLimit}}.", "minimum": 1 }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """);

    private static JsonElement CreateListDirSchema()
        => ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Directory to list. Defaults to the session working directory." }
              },
              "additionalProperties": false
            }
            """);

    private static JsonElement CreateGrepSchema(AgentBuiltInToolOptions options)
        => ParseSchema(
            $$"""
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "Regular expression to search within each line (.NET regex syntax)." },
                "path": { "type": ["string", "array"], "items": { "type": "string" }, "description": "File or directory, or array of files/directories, to search. Defaults to the session working directory." },
                "glob": { "type": ["string", "array"], "items": { "type": "string" }, "description": "Optional file-name glob or globs like *.cs. Multiple globs match when any glob matches. If a glob includes path separators, it is matched against each file's relative path." },
                "caseSensitive": { "type": "boolean", "description": "Whether matching is case-sensitive. Defaults to false." },
                "maxMatches": { "type": "integer", "description": "Maximum matches to return. Defaults to {{options.MaxGrepMatches}}.", "minimum": 1 }
              },
              "required": ["pattern"],
              "additionalProperties": false
            }
            """);

    private static JsonElement CreateWebGetSchema(AgentBuiltInToolOptions options)
        => ParseSchema(
            $$"""
            {
              "type": "object",
              "properties": {
                "url": { "type": "string", "description": "Absolute http or https URL to fetch. On success, webget returns a text result containing the fetched body. By default, HTML responses are simplified to plain text; JSON and XML are returned as text bodies after webget's content-type and size checks." },
                "timeoutSeconds": { "type": "integer", "description": "Optional timeout override in seconds. Defaults to {{FormatSeconds(options.WebGetTimeout)}} seconds.", "minimum": 1 },
                "rawHtml": { "type": "boolean", "description": "When true, return raw HTML markup instead of simplified plain text for HTML/XHTML responses. Defaults to false." },
                "includeHttpStatus": { "type": "boolean", "description": "When true, prefix the HTTP status line and response content type before the returned body text. Defaults to false." }
              },
              "required": ["url"],
              "additionalProperties": false
            }
            """);

    private static JsonElement CreateShellCommandSchema(AgentBuiltInToolOptions options)
        => ParseSchema(
            $$"""
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "Shell command or short shell script to execute." },
                "workdir": { "type": "string", "description": "Optional working directory override. Defaults to the session working directory." },
                "timeoutMs": { "type": "integer", "description": "Optional timeout in milliseconds, covering shell execution and output collection. Omit for no deadline.", "minimum": 1 },
                "login": { "type": "boolean", "description": "Whether to use login-shell semantics on Unix shells that support it. Ignored on Windows, where pwsh always runs with -NoProfile for predictable output." },
                "{{ShellMaxOutputTokensArgument}}": { "type": "integer", "description": "{{DescribeShellMaxOutputTokens(options)}}", "minimum": 1 }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """);

    private static string DescribeShellMaxOutputTokens(AgentBuiltInToolOptions options)
        => options.MaxShellCommandOutputCharacters > 0
            ? FormattableString.Invariant($"Optional budget of the output you are given, in tokens of about 4 characters, to get less than the default of {options.MaxShellCommandOutputCharacters} characters; it cannot raise it. A longer output keeps the head and the tail of each stream, and the whole output is saved to a file the result names.")
            : "Optional budget of the output you are given, in tokens of about 4 characters. A longer output keeps the head and the tail of each stream, and the whole output is saved to a file the result names.";

    private static bool IsSupportedWebContentType(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static string FormatWebGetSuccessResponse(HttpResponseMessage response, string body)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP ").Append((int)response.StatusCode);
        if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
        {
            builder.Append(' ').Append(response.ReasonPhrase);
        }

        var contentType = response.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            builder.AppendLine();
            builder.Append("content-type: ").Append(contentType);
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.Append(body);
        return builder.ToString();
    }

    private static string WrapWindowsShellCommand(string command)
    {
        const string postlude =
            """
            if (-not $?) {
                if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                    exit $LASTEXITCODE
                }

                exit 1
            }
            """;

        return string.Concat(
            command,
            command.EndsWith('\n') ? string.Empty : Environment.NewLine,
            postlude);
    }

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    // What a script of Code Mode receives for a command, also when its exit code is not 0: scripts branch on the code.
    private static JsonElement CreateShellCommandValue(int exitCode, string stdout, string stderr, string workdir)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("exitCode", exitCode);
            writer.WriteString("stdout", stdout);
            writer.WriteString("stderr", stderr);
            writer.WriteString("workingDirectory", workdir);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static readonly JsonElement ShellCommandOutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "exitCode": { "type": "integer", "description": "The exit code; a script receives the output whatever it is." },
            "stdout": { "type": "string" },
            "stderr": { "type": "string" },
            "workingDirectory": { "type": "string" }
          },
          "required": ["exitCode", "stdout", "stderr", "workingDirectory"]
        }
        """).RootElement.Clone();

    private static string FormatShellCommandOutput(int exitCode, string stdout, string stderr, string workdir, string? truncation = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"exit_code: {exitCode}");
        builder.AppendLine($"working_directory: {workdir}");
        if (truncation is not null)
        {
            builder.AppendLine($"output_truncated: {truncation}");
        }

        builder.AppendLine("stdout:");
        builder.AppendLine(string.IsNullOrEmpty(stdout) ? "(empty)" : stdout);
        builder.AppendLine("stderr:");
        builder.Append(string.IsNullOrEmpty(stderr) ? "(empty)" : stderr);
        return builder.ToString().TrimEnd();
    }

    private static async Task<string> PumpProcessStreamAsync(
        StreamReader reader,
        string streamName,
        StringBuilder builder,
        AgentToolProgressHandler? progress,
        CancellationToken cancellationToken)
    {
        string? line;
        var firstLine = true;
        // On Windows a thread of its own reads the pipe: a read of it would hold a thread of the pool while the command runs.
        using var lines = ProcessOutputLineReader.Start(reader, "shell_command " + streamName);
        while ((line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!firstLine)
            {
                builder.AppendLine();
            }

            builder.Append(line);
            firstLine = false;

            if (progress is not null)
            {
                await progress(
                        new AgentToolProgressUpdate(
                            line + Environment.NewLine,
                            CreateShellStreamDetails(streamName)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return builder.ToString();
    }

    private static JsonElement CreateShellStreamDetails(string streamName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("stream", streamName);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task WaitForKilledProcessAsync(Process process)
    {
        try
        {
            // The shell's own exit, not the end of its output: a child that outlives it may still hold the pipes.
            await process.WaitForExitAsync().WaitAsync(KilledProcessExitTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"The '{propertyName}' field is required.")
            : value;
    }

    private static string GetRequiredStringValue(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return value is null
            ? throw new ArgumentException($"The '{propertyName}' field is required.")
            : value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static string SerializeAnswers(IReadOnlyDictionary<string, string> answers)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var answer in answers)
            {
                writer.WriteString(answer.Key, answer.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record ShellProcessSpec(ProcessStartInfo StartInfo);

    private readonly record struct WorkspacePathResolution(string FullPath, string DisplayPath);

    private readonly record struct SearchGlobPattern(GlobPattern Pattern, bool UseRelativePath);

    private readonly record struct SearchFileTarget(string FullPath, string DisplayPath, string RelativePath, string Name);
}
