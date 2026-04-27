using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XenoAtom.Glob;
using XenoAtom.Glob.Git;
using XenoAtom.Glob.IO;

namespace CodeAlta.Agent.LocalRuntime.Tools;

/// <summary>
/// Creates the default non-provider built-in tools used by local raw-API sessions.
/// </summary>
public static class LocalAgentBuiltInToolFactory
{
    private static readonly FileTreeWalker FileTreeWalker = new();
    private const string SupportedWebContentTypesDescription = "text/*, application/json, application/xml, and application/xhtml+xml";
    private const string WriteFileToolDescription =
        "Write an entire text file in one deterministic operation. Creates parent directories as needed and replaces any existing file.";
    private const string ReplaceInFileToolDescription =
        "Replace exact text in a file. Deterministic only: no regex, no fuzzy matching. When replace_all is false, the tool errors unless exactly one match exists and leaves the file unchanged. Use replace_all=true to replace every match.";
    private const string DeleteFileOrDirToolDescription =
        "Delete a file or a directory. Directory deletes are recursive.";
    private const string RenameFileOrDirToolDescription =
        "Rename or move a file or directory. Will not overwrite an existing destination.";
    private const string ApplyPatchToolDescription =
        "Use the `apply_patch` tool to edit files.";

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
            "replace_all": { "type": "boolean", "description": "Replace every exact match. When false, the tool errors unless exactly one match exists and leaves the file unchanged." }
          },
          "required": ["path", "old_string", "new_string"],
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
    /// Creates the default built-in tools.
    /// </summary>
    /// <param name="options">Tool options.</param>
    /// <returns>The built-in tools.</returns>
    public static IReadOnlyList<AgentToolDefinition> CreateDefaultTools(LocalAgentBuiltInToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var httpClient = options.HttpClient ?? new HttpClient();
        AgentToolDefinition[] tools =
        [
            new AgentToolDefinition(
                new AgentToolSpec(
                    "read_file",
                    $"Read a local text file by line number. Offsets are 1-based; use a negative offset to count from the end (-1 is the last line). Offsets past EOF return an empty text result, and oversized negative offsets clamp to line 1. Omitting limit returns up to {options.DefaultReadFileLineLimit} lines by default.",
                    CreateReadFileSchema(options)),
                (invocation, cancellationToken) => ReadFileAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "list_dir",
                    "List the direct children of a directory as [dir] and [file] entries.",
                    CreateListDirSchema()),
                (invocation, cancellationToken) => ListDirectoryAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "grep",
                    $"Search files for line-based matches using a .NET regular expression. Recurses into directories, defaults to case-insensitive matching, skips likely-binary files, and returns '(no matches)' when nothing matches.",
                    CreateGrepSchema(options)),
                (invocation, cancellationToken) => GrepAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "webget",
                    $"Fetch text-like web content from a known absolute URL. On success, returns a text result containing the fetched body. By default, HTML responses are simplified to plain text; set rawHtml=true to return raw HTML markup. Set includeHttpStatus=true to prefix the HTTP status line and response content type before the body. JSON and XML are returned as text bodies. Applies content-type checks and a {options.MaxWebGetBytes.ToString(CultureInfo.InvariantCulture)}-byte limit; default timeout is {FormatSeconds(options.WebGetTimeout)} seconds.",
                    CreateWebGetSchema(options)),
                (invocation, cancellationToken) => WebGetAsync(options, httpClient, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "shell_command",
                    "Execute a local shell command or short shell script using the platform shell, subject to host approval. Some hosts may auto-approve. Returns exit_code, working_directory, stdout, and stderr as emitted by the shell and child processes; child commands may still include ANSI/control sequences. On Windows, pwsh runs with -NoProfile and preserves the final external command exit code when that command fails.",
                    CreateShellCommandSchema()),
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
            // Intentionally not registered yet: the local raw-API host does not currently expose
            // the structured UI feedback loop needed to pause for request_user_input safely.
            // Keep the implementation around so the tool can be enabled once the host supports it.
        ];

        return tools
            .Where(tool => ShouldIncludeBuiltInTool(options, tool.Spec.Name))
            .ToArray();
    }

    private static Task<AgentToolResult> ReadFileAsync(
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = GetRequiredString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(Failure($"File '{resolvedPath}' was not found."));
        }

        if (LocalAgentFileTypeDetector.IsProbablyBinaryFile(resolvedPath))
        {
            return Task.FromResult(Failure($"'{resolvedPath}' appears to be a binary file. read_file only supports text files."));
        }

        var offset = GetOptionalInt(invocation.Arguments, "offset") ?? 1;
        if (offset == 0)
        {
            return Task.FromResult(Failure("The 'offset' value must be >= 1, or negative to count from the end. 0 is not allowed."));
        }

        var requestedLimit = GetOptionalInt(invocation.Arguments, "limit") ?? options.DefaultReadFileLineLimit;
        var limit = Math.Clamp(requestedLimit, 1, options.MaxReadFileLineLimit);
        var startLine = offset >= 1 ? offset : GetStartLineFromEnd(resolvedPath, offset, cancellationToken);

        var lines = new List<string>(limit);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(resolvedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < startLine)
            {
                continue;
            }

            lines.Add($"{lineNumber,5}: {line}");
            if (lines.Count >= limit)
            {
                break;
            }
        }

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(string.Join(Environment.NewLine, lines))]));
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
        LocalAgentBuiltInToolOptions options,
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
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pattern = GetRequiredString(invocation.Arguments, "pattern");
        var targetPath = ResolvePath(options.WorkingDirectory, GetOptionalString(invocation.Arguments, "path"));
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            return Task.FromResult(Failure($"Path '{targetPath}' was not found."));
        }

        var glob = GetOptionalString(invocation.Arguments, "glob");
        GlobPattern? globPattern = null;
        var useRelativePathGlob = false;
        if (!string.IsNullOrWhiteSpace(glob))
        {
            var parseResult = GlobPattern.TryParse(glob);
            if (!parseResult.Success)
            {
                return Task.FromResult(Failure($"Invalid glob pattern '{glob}'."));
            }

            globPattern = parseResult.Pattern;
            useRelativePathGlob = glob.Contains('/') || glob.Contains('\\');
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
            if (globPattern is not null && !GlobMatches(globPattern, file, useRelativePathGlob))
            {
                continue;
            }

            if (IsImagePath(file.FullPath))
            {
                continue;
            }

            try
            {
                if (LocalAgentFileTypeDetector.IsProbablyBinaryFile(file.FullPath))
                {
                    continue;
                }

                SearchFileLines(file.FullPath, file.DisplayPath, regex, maxMatches, matches, cancellationToken);
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

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(matches.Count == 0 ? "(no matches)" : string.Join(Environment.NewLine, matches))]));
    }

    private static async Task<AgentToolResult> WebGetAsync(
        LocalAgentBuiltInToolOptions options,
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
            return Failure($"webget timed out after {effectiveTimeout.TotalSeconds:0.###} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return Failure($"webget failed to fetch '{uri}': {ex.Message}");
        }
    }

    private static async Task<AgentToolResult> ShellCommandAsync(
        LocalAgentBuiltInToolOptions options,
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

        var permissionRequest = new AgentCommandPermissionRequest(
            options.BackendId,
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

        var processSpec = CreateShellProcessSpec(command, workdir, login);
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

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = PumpProcessStreamAsync(
            process.StandardOutput,
            "stdout",
            stdoutBuilder,
            invocation.Progress,
            cancellationToken);
        var stderrTask = PumpProcessStreamAsync(
            process.StandardError,
            "stderr",
            stderrBuilder,
            invocation.Progress,
            cancellationToken);

        try
        {
            if (timeout is > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout.Value);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Failure(timeout is > 0
                ? $"shell_command timed out after {timeout.Value} ms."
                : "shell_command was canceled.");
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        var output = FormatShellCommandOutput(process.ExitCode, stdout, stderr, workdir);
        if (process.ExitCode != 0)
        {
            return new AgentToolResult(
                false,
                [new AgentToolResultItem.Text(output)],
                $"shell_command exited with code {process.ExitCode}.");
        }

        return new AgentToolResult(true, [new AgentToolResultItem.Text(output)]);
    }

    private static async Task<AgentToolResult> WriteFileAsync(
        LocalAgentBuiltInToolOptions options,
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

        var content = GetRequiredString(invocation.Arguments, "content");
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
        LocalAgentBuiltInToolOptions options,
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

        if (LocalAgentFileTypeDetector.IsProbablyBinaryFile(resolution.FullPath))
        {
            return Failure($"replace_in_file does not support binary files: '{resolution.DisplayPath}'.");
        }

        var oldString = GetRequiredString(invocation.Arguments, "old_string");
        if (oldString.Length == 0)
        {
            return Failure("The 'old_string' value must not be empty.");
        }

        var newString = GetRequiredString(invocation.Arguments, "new_string");
        var replaceAll = GetOptionalBool(invocation.Arguments, "replace_all") ?? false;

        var original = await File.ReadAllTextAsync(resolution.FullPath, cancellationToken).ConfigureAwait(false);
        var newline = DetectExistingNewline(original);
        var replacementTarget = oldString;
        var replacementValue = newString;

        if (!original.Contains(replacementTarget, StringComparison.Ordinal))
        {
            var normalizedOldString = NormalizeNewlines(oldString, newline);
            if (!string.Equals(normalizedOldString, oldString, StringComparison.Ordinal) &&
                original.Contains(normalizedOldString, StringComparison.Ordinal))
            {
                replacementTarget = normalizedOldString;
                replacementValue = NormalizeNewlines(newString, newline);
            }
        }

        var matchCount = CountOccurrences(original, replacementTarget);
        if (matchCount == 0)
        {
            return Failure($"replace_in_file could not find the requested text in '{resolution.DisplayPath}'.");
        }

        if (!replaceAll && matchCount != 1)
        {
            return Failure(
                $"replace_in_file found {matchCount} matches in '{resolution.DisplayPath}'. Set replace_all=true to replace every match.");
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

        var updated = replaceAll
            ? original.Replace(replacementTarget, replacementValue, StringComparison.Ordinal)
            : ReplaceFirstOccurrence(original, replacementTarget, replacementValue);
        await File.WriteAllTextAsync(resolution.FullPath, updated, cancellationToken).ConfigureAwait(false);
        var replacedCount = replaceAll ? matchCount : 1;
        return SuccessResult($"Replaced {replacedCount} occurrence(s) in {resolution.DisplayPath}");
    }

    private static async Task<AgentToolResult> DeleteFileOrDirAsync(
        LocalAgentBuiltInToolOptions options,
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
        LocalAgentBuiltInToolOptions options,
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
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        var patchInput = GetRequiredString(invocation.Arguments, "input");
        IReadOnlyList<string> touchedPaths;
        try
        {
            touchedPaths = LocalAgentApplyPatch.GetTouchedPaths(patchInput, workingDirectory);
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
            return LocalAgentApplyPatch.Apply(patchInput, workingDirectory);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
    }

    private static async Task<AgentToolResult> RequestUserInputAsync(
        LocalAgentBuiltInToolOptions options,
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
            options.BackendId,
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

    private static bool ShouldIncludeBuiltInTool(LocalAgentBuiltInToolOptions options, string toolName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (options.Provider?.Profile?.BuiltInToolOverrides is { Count: > 0 } overrides &&
            overrides.TryGetValue(toolName, out var enabled))
        {
            return enabled;
        }

        return !string.Equals(toolName, "apply_patch", StringComparison.Ordinal) ||
               IsApplyPatchSupported(options.Provider, options.BackendId);
    }

    private static bool IsApplyPatchSupported(LocalAgentProviderDescriptor? provider, AgentBackendId backendId)
    {
        if (provider is null)
        {
            return backendId == AgentBackendIds.OpenAIResponses || backendId == AgentBackendIds.OpenAIChat;
        }

        if (provider.TransportKind is not (LocalAgentTransportKind.OpenAIResponses or LocalAgentTransportKind.OpenAIChatCompletions))
        {
            return false;
        }

        if (string.Equals(provider.ProtocolFamily, "openai-codex-subscription", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (provider.BaseUri is null)
        {
            return true;
        }

        return string.Equals(provider.BaseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWorkingDirectoryRoot(LocalAgentBuiltInToolOptions options)
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
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        string toolName,
        IReadOnlyList<string> touchedPaths,
        CancellationToken cancellationToken)
    {
        var workingDirectory = GetWorkingDirectoryRoot(options);
        var permissionRequest = new AgentFileChangePermissionRequest(
            options.BackendId,
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

    private static bool GlobMatches(GlobPattern globPattern, SearchFileTarget entry, bool useRelativePath)
        => useRelativePath
            ? globPattern.IsMatch(entry.RelativePath)
            : globPattern.IsMatch(entry.Name);

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
            if (!regex.IsMatch(line))
            {
                continue;
            }

            matches.Add($"{displayPath}:{lineNumber}: {line}");
            if (matches.Count >= maxMatches)
            {
                break;
            }
        }
    }

    private static ShellProcessSpec CreateShellProcessSpec(string command, string workdir, bool login)
    {
        if (OperatingSystem.IsWindows())
        {
            var fileName = "pwsh";
            // Always suppress the user profile on Windows so prompt theming and other profile-time output
            // cannot leak ANSI/control sequences into tool results. The login flag is Unix-oriented here.
            // Wrap the command so a failing final external command can propagate its native exit code
            // instead of PowerShell collapsing it to 1.
            string[] shellArguments = ["-NoProfile", "-Command", WrapWindowsShellCommand(command)];
            return new ShellProcessSpec(CreateProcessStartInfo(fileName, shellArguments, workdir));
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

        return new ShellProcessSpec(CreateProcessStartInfo(shellPath, arguments, workdir));
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IReadOnlyList<string> arguments, string workdir)
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

        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CLICOLOR"] = "0";
        startInfo.Environment["CLICOLOR_FORCE"] = "0";

        return startInfo;
    }

    private static JsonElement CreateReadFileSchema(LocalAgentBuiltInToolOptions options)
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

    private static JsonElement CreateGrepSchema(LocalAgentBuiltInToolOptions options)
        => ParseSchema(
            $$"""
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "Regular expression to search within each line (.NET regex syntax)." },
                "path": { "type": "string", "description": "File or directory to search. Defaults to the session working directory." },
                "glob": { "type": "string", "description": "Optional file-name glob like *.cs. If it includes path separators, it is matched against each file's relative path." },
                "caseSensitive": { "type": "boolean", "description": "Whether matching is case-sensitive. Defaults to false." },
                "maxMatches": { "type": "integer", "description": "Maximum matches to return. Defaults to {{options.MaxGrepMatches}}.", "minimum": 1 }
              },
              "required": ["pattern"],
              "additionalProperties": false
            }
            """);

    private static JsonElement CreateWebGetSchema(LocalAgentBuiltInToolOptions options)
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

    private static JsonElement CreateShellCommandSchema()
        => ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "Shell command or short shell script to execute." },
                "workdir": { "type": "string", "description": "Optional working directory override. Defaults to the session working directory." },
                "timeoutMs": { "type": "integer", "description": "Optional timeout in milliseconds.", "minimum": 1 },
                "login": { "type": "boolean", "description": "Whether to use login-shell semantics on Unix shells that support it. Ignored on Windows, where pwsh always runs with -NoProfile for predictable output." }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """);

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

    private static string FormatShellCommandOutput(int exitCode, string stdout, string stderr, string workdir)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"exit_code: {exitCode}");
        builder.AppendLine($"working_directory: {workdir}");
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
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
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
        catch (InvalidOperationException)
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

    private readonly record struct SearchFileTarget(string FullPath, string DisplayPath, string RelativePath, string Name);
}
