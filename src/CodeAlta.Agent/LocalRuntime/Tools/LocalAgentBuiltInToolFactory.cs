using System.ComponentModel;
using System.Diagnostics;
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

    private static readonly JsonElement ReadFileSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to read." },
            "offset": { "type": "integer", "description": "1-based line offset.", "minimum": 1 },
            "limit": { "type": "integer", "description": "Maximum number of lines to return.", "minimum": 1 }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ListDirSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory to list. Defaults to the session working directory." }
          },
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement GrepSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression to search within each line." },
            "path": { "type": "string", "description": "File or directory to search. Defaults to the session working directory." },
            "glob": { "type": "string", "description": "Optional file-name glob like *.cs." },
            "caseSensitive": { "type": "boolean", "description": "Whether matching is case-sensitive." },
            "maxMatches": { "type": "integer", "description": "Maximum matches to return.", "minimum": 1 }
          },
          "required": ["pattern"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement WebGetSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute URL to fetch." },
            "timeoutSeconds": { "type": "integer", "description": "Optional timeout override in seconds.", "minimum": 1 }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ShellCommandSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Shell command to execute." },
            "workdir": { "type": "string", "description": "Optional working directory override." },
            "timeoutMs": { "type": "integer", "description": "Optional timeout in milliseconds.", "minimum": 1 },
            "login": { "type": "boolean", "description": "Whether to use login-shell semantics when supported." }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ApplyPatchSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "input": { "type": "string", "description": "Full apply_patch envelope text." }
          },
          "required": ["input"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ViewImageSchema = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the local image file." }
          },
          "required": ["path"],
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
        return
        [
            new AgentToolDefinition(
                new AgentToolSpec(
                    "read_file",
                    "Read the contents of a text file or return a local image attachment.",
                    ReadFileSchema),
                (invocation, cancellationToken) => ReadFileAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "list_dir",
                    "List the direct children of a directory.",
                    ListDirSchema),
                (invocation, cancellationToken) => ListDirectoryAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "grep",
                    "Search a file or directory for line-based regular-expression matches.",
                    GrepSchema),
                (invocation, cancellationToken) => GrepAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "webget",
                    "Fetch web content from a known URL with basic size and content-type safeguards.",
                    WebGetSchema),
                (invocation, cancellationToken) => WebGetAsync(options, httpClient, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "shell_command",
                    "Execute a local shell command using the platform-appropriate shell after permission approval.",
                    ShellCommandSchema),
                (invocation, cancellationToken) => ShellCommandAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "apply_patch",
                    "Apply a structured file patch using the Codex-style apply_patch grammar.",
                    ApplyPatchSchema),
                (invocation, cancellationToken) => ApplyPatchAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "view_image",
                    "Return a local image as an attachment-like result.",
                    ViewImageSchema),
                (invocation, cancellationToken) => ViewImageAsync(options, invocation, cancellationToken)),
            new AgentToolDefinition(
                new AgentToolSpec(
                    "request_user_input",
                    "Pause and request structured user input from the host.",
                    RequestUserInputSchema),
                (invocation, cancellationToken) => RequestUserInputAsync(options, invocation, cancellationToken)),
        ];
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

        if (IsImagePath(resolvedPath))
        {
            return Task.FromResult(new AgentToolResult(
                true,
                [
                    new AgentToolResultItem.Text($"Image: {resolvedPath}"),
                    new AgentToolResultItem.ImageUrl(new Uri(resolvedPath).AbsoluteUri),
                ]));
        }

        var offset = Math.Max(1, GetOptionalInt(invocation.Arguments, "offset") ?? 1);
        var requestedLimit = GetOptionalInt(invocation.Arguments, "limit") ?? options.DefaultReadFileLineLimit;
        var limit = Math.Clamp(requestedLimit, 1, options.MaxReadFileLineLimit);

        var lines = new List<string>(limit);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(resolvedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < offset)
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

    private static Task<AgentToolResult> ListDirectoryAsync(
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = GetOptionalString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
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
            .OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(string.Join(Environment.NewLine, entries))]));
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

        var timeoutOverride = GetOptionalInt(invocation.Arguments, "timeoutSeconds");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutOverride is > 0 ? TimeSpan.FromSeconds(timeoutOverride.Value) : options.WebGetTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null &&
            !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return Failure($"Content type '{mediaType}' is not supported by webget.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: options.MaxWebGetBytes);
        var rented = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(rented, linkedCts.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxWebGetBytes)
            {
                return Failure($"Response exceeded the {options.MaxWebGetBytes} byte limit.");
            }

            buffer.Write(rented, 0, read);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            text = SimplifyHtml(text);
        }

        return new AgentToolResult(
            true,
            [new AgentToolResultItem.Text(text.Trim())]);
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

    private static async Task<AgentToolResult> ApplyPatchAsync(
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        var patchInput = GetRequiredString(invocation.Arguments, "input");
        var touchedPaths = LocalAgentApplyPatch.GetTouchedPaths(patchInput, workingDirectory);
        var permissionRequest = new AgentFileChangePermissionRequest(
            options.BackendId,
            options.SessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: invocation.ToolCallId,
            GrantRoot: workingDirectory,
            Reason: touchedPaths.Count == 0
                ? "The agent requested filesystem edits via apply_patch."
                : $"The agent requested filesystem edits via apply_patch affecting {touchedPaths.Count} path(s).");
        var decision = await options.OnPermissionRequest(permissionRequest, cancellationToken).ConfigureAwait(false);
        switch (decision.Kind)
        {
            case AgentPermissionDecisionKind.AllowOnce:
            case AgentPermissionDecisionKind.AllowForSession:
                break;
            case AgentPermissionDecisionKind.Deny:
                return Failure("apply_patch was denied by the host.");
            case AgentPermissionDecisionKind.Cancel:
                return Failure("apply_patch was canceled by the host.");
            default:
                return Failure($"Unsupported permission decision '{decision.Kind}'.");
        }

        return LocalAgentApplyPatch.Apply(patchInput, workingDirectory);
    }

    private static Task<AgentToolResult> ViewImageAsync(
        LocalAgentBuiltInToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetRequiredString(invocation.Arguments, "path");
        var resolvedPath = ResolvePath(options.WorkingDirectory, path);
        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(Failure($"Image '{resolvedPath}' was not found."));
        }

        if (!IsImagePath(resolvedPath))
        {
            return Task.FromResult(Failure($"'{resolvedPath}' is not a supported image path."));
        }

        return Task.FromResult(new AgentToolResult(
            true,
            [
                new AgentToolResultItem.Text($"Image: {resolvedPath}"),
                new AgentToolResultItem.ImageUrl(new Uri(resolvedPath).AbsoluteUri),
            ]));
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

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    private static string ResolvePath(string? workingDirectory, string? path)
    {
        var candidate = string.IsNullOrWhiteSpace(path) ? workingDirectory : path;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("A path or working directory is required.");
        }

        return Path.GetFullPath(Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(workingDirectory ?? Environment.CurrentDirectory, candidate));
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
            string[] shellArguments = login
                ? ["-Command", command]
                : ["-NoProfile", "-Command", command];
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

        return startInfo;
    }

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
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
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

    private readonly record struct SearchFileTarget(string FullPath, string DisplayPath, string RelativePath, string Name);
}
