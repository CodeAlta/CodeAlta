using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Agent.Claude;

/// <summary>What a command of the CLI that runs and ends wrote.</summary>
/// <param name="ExitCode">Its exit code; null when it did not end in time and was ended.</param>
/// <param name="Output">What it wrote to its output.</param>
/// <param name="Error">What it wrote to its error output.</param>
internal sealed record ClaudeCodeCommandResult(int? ExitCode, string Output, string Error);

/// <summary>What the screen of <c>claude --cloud</c> says.</summary>
/// <param name="Created">Whether it says the cloud session was created.</param>
/// <param name="SessionId">The identifier of the cloud session, when it says one.</param>
/// <param name="Title">The title of the cloud session, when it says one.</param>
/// <param name="Note">What it said of where the session starts (a branch that is not on GitHub), or null.</param>
/// <param name="Message">The last lines it wrote, for a failure, or null.</param>
internal sealed record ClaudeCodeCloudScreen(bool Created, string? SessionId, string? Title, string? Note, string? Message);

/// <summary>
/// The cloud sessions of Claude Code through its documented commands only: <c>claude --cloud "&lt;task&gt;"</c> starts
/// one (in a terminal: the CLI refuses without one), <c>claude -p --cloud &lt;id&gt;</c> sends it a message, and
/// <c>claude -p /cost --teleport &lt;id&gt; --session-id &lt;uuid&gt;</c> brings its conversation to this computer
/// without a model call (<c>/cost</c> is a local command). Nothing is read from the cloud service by CodeAlta, and no
/// credential of the CLI is touched: the CLI does it all with its own sign-in.
/// </summary>
internal static partial class ClaudeCodeCloud
{
    /// <summary>The longest task a cloud session is started with, in characters.</summary>
    public const int MaxTaskLength = 16 * 1024;

    /// <summary>The longest message sent to a cloud session, in characters.</summary>
    public const int MaxMessageLength = 64 * 1024;

    // The CLI prints this when it created the session, then its address and how to bring it back.
    private const string CreatedMarker = "Created cloud session:";

    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^(?:session|cse)_[A-Za-z0-9]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CloudSessionIdPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:session|cse)_[A-Za-z0-9]{8,64}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex CloudSessionIdInText();

    // The address of the session the CLI printed (View: https://claude.ai/code/<id>?…), and its resume line.
    [GeneratedRegex(@"https://claude\.ai/code/(?<id>(?:session|cse)_[A-Za-z0-9]{8,64})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex CloudSessionLink();

    [GeneratedRegex(@"--teleport\s+(?<id>(?:session|cse)_[A-Za-z0-9]{8,64})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex CloudSessionResume();

    /// <summary>Whether a text is the identifier of a cloud session (<c>session_…</c> or <c>cse_…</c>).</summary>
    public static bool IsCloudSessionId(string? id) => id is { Length: <= 80 } && CloudSessionIdPattern().IsMatch(id);

    /// <summary>The identifier of a cloud session in a text a user pasted: the identifier itself, or a claude.ai/code link.</summary>
    /// <returns>The identifier, or null when the text holds none.</returns>
    public static string? FindCloudSessionId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) return null;
        var trimmed = text.Trim();
        if (IsCloudSessionId(trimmed)) return trimmed;
        if (!trimmed.StartsWith("https://claude.ai/", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("claude.ai/", StringComparison.OrdinalIgnoreCase)) return null;
        var match = CloudSessionIdInText().Match(trimmed);
        return match.Success ? match.Value : null;
    }

    /// <summary>The address of a cloud session on claude.ai.</summary>
    /// <exception cref="ArgumentException"><paramref name="cloudSessionId"/> is not the identifier of a cloud session.</exception>
    public static string UrlOf(string cloudSessionId)
        => IsCloudSessionId(cloudSessionId)
            ? $"https://claude.ai/code/{cloudSessionId}"
            : throw new ArgumentException("A cloud session is named session_… or cse_….", nameof(cloudSessionId));

    /// <summary>
    /// How <c>claude --cloud</c> is started: the executable of the provider, its account, the task bound to the option
    /// (<c>--cloud=…</c>, so that a task starting with a dash stays the task), and no API key, which a cloud session
    /// does not take: it runs on the claude.ai account.
    /// </summary>
    /// <exception cref="ArgumentException">The task is empty or too long.</exception>
    public static ClaudeCodeLaunch StartLaunch(string executable, ClaudeCodeModelProviderRuntimeOptions options, string workingDirectory, string task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(task) || task.Length > MaxTaskLength)
        {
            throw new ArgumentException($"The task of a cloud session is between 1 and {MaxTaskLength} characters.", nameof(task));
        }

        return new ClaudeCodeLaunch(executable, [$"--cloud={task.Trim()}"], workingDirectory, CloudEnvironment(options));
    }

    /// <summary>How a message is sent to a cloud session: <c>claude -p --cloud &lt;id&gt; --output-format json</c>, the message on its input.</summary>
    public static ClaudeCodeLaunch SendLaunch(string executable, ClaudeCodeModelProviderRuntimeOptions options, string workingDirectory, string cloudSessionId)
        => new(executable, ["-p", $"--cloud={Checked(cloudSessionId)}", "--output-format", "json"], workingDirectory,
            CloudEnvironment(options));

    /// <summary>
    /// How a cloud session is brought back: <c>claude -p /cost --teleport &lt;id&gt; --session-id=&lt;uuid&gt;</c>. The
    /// arguments go to the CLI as they are: a shell would make <c>/cost</c> a path (Git Bash), and that a model turn.
    /// </summary>
    public static ClaudeCodeLaunch TeleportLaunch(string executable, ClaudeCodeModelProviderRuntimeOptions options, string workingDirectory,
        string cloudSessionId, string conversationId)
        => new(executable, ["-p", "/cost", "--teleport", Checked(cloudSessionId), $"--session-id={CheckedConversation(conversationId)}", "--output-format", "json"],
            workingDirectory, CloudEnvironment(options));

    /// <summary>The command that brings a cloud session back in a terminal, into the conversation given.</summary>
    public static string TeleportCommand(string cloudSessionId, string conversationId)
        => $"claude --teleport {Checked(cloudSessionId)} --session-id {CheckedConversation(conversationId)}";

    /// <summary>Whether the screen of the CLI shows a question it waits on: the trust of the folder, a choice, a sign-in.</summary>
    public static bool Asks(IReadOnlyList<string> rows)
    {
        foreach (var row in rows)
        {
            if (row.Contains("Enter to confirm", StringComparison.OrdinalIgnoreCase) ||
                row.Contains("Esc to cancel", StringComparison.OrdinalIgnoreCase) ||
                row.Contains("Press Enter", StringComparison.OrdinalIgnoreCase) ||
                row.Contains("trust this folder", StringComparison.OrdinalIgnoreCase) ||
                row.Contains("Select login method", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads what <c>claude --cloud</c> wrote on its screen. What it prints is for people: what is not found is left out.</summary>
    public static ClaudeCodeCloudScreen ReadScreen(IReadOnlyList<string> rows)
    {
        var lines = rows.Select(static row => row.TrimEnd()).Where(static row => !row.StartsWith("[process exited", StringComparison.Ordinal)).ToList();
        var created = lines.FindIndex(static line => line.Contains(CreatedMarker, StringComparison.Ordinal));
        string? title = null;
        string? sessionId = null;
        if (created >= 0)
        {
            title = Clean(lines[created][(lines[created].IndexOf(CreatedMarker, StringComparison.Ordinal) + CreatedMarker.Length)..]);
            // The identity is the one of the address the CLI printed, never a word of the title: the rows of the address are
            // joined back when the terminal wrapped it, up to the next row that has a label of its own (Resume with: …).
            // Its resume line must name the same session when it names one.
            var after = lines.Skip(created + 1).ToList();
            var view = after.FindIndex(static line => line.Contains("https://claude.ai/code/", StringComparison.Ordinal));
            var address = view < 0 ? string.Empty
                : string.Concat(after.Skip(view).TakeWhile((line, index) => index == 0 || line.Length > 0 && !line.Contains(':', StringComparison.Ordinal)));
            var link = CloudSessionLink().Match(address) is { Success: true } found ? found.Groups["id"].Value : null;
            var resume = after.Select(static line => CloudSessionResume().Match(line)).FirstOrDefault(static match => match.Success)?.Groups["id"].Value;
            sessionId = link is not null && resume is not null && !string.Equals(link, resume, StringComparison.Ordinal) ? null : link ?? resume;
        }

        var noteStart = lines.FindIndex(static line => line.Contains("is not on GitHub", StringComparison.Ordinal));
        var note = noteStart < 0 ? null : Clean(string.Join(' ', lines.Skip(noteStart).TakeWhile(static line => line.Trim().Length > 0).Take(3).Select(static line => line.Trim())));
        var written = lines.Where(static line => line.Trim().Length > 0).ToList();
        var errorAt = written.FindLastIndex(static line => line.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase));
        var message = written.Count == 0 ? null : Clean(string.Join('\n', (errorAt >= 0 ? written.Skip(errorAt) : written.TakeLast(6)).Take(6).Select(static line => line.Trim())));
        return new ClaudeCodeCloudScreen(created >= 0 && sessionId is not null, sessionId, string.IsNullOrEmpty(title) ? null : title, note, message);
    }

    /// <summary>Reads the answer of <c>claude -p --cloud &lt;id&gt; --output-format json</c>.</summary>
    public static AgentCloudSendResult ReadSend(ClaudeCodeCommandResult result)
    {
        string? error = null;
        if (TryReadObject(result.Output) is { } answer)
        {
            if (answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && result.ExitCode == 0)
            {
                return new AgentCloudSendResult("ok");
            }

            error = ClaudeCodeJson.GetString(answer, "error");
        }

        var said = Clean(error ?? FirstLines(result.Error) ?? FirstLines(result.Output));
        var text = said ?? string.Empty;
        var status = text.Contains("archived", StringComparison.OrdinalIgnoreCase) ? "archived"
            : text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("invalid session", StringComparison.OrdinalIgnoreCase) ? "not_found"
            : text.Contains("policy", StringComparison.OrdinalIgnoreCase) ? "policy"
            : text.Contains("claude.ai", StringComparison.OrdinalIgnoreCase) || text.Contains("organization UUID", StringComparison.OrdinalIgnoreCase)
              || text.Contains("Anthropic account", StringComparison.OrdinalIgnoreCase) ? "needs_claude_ai"
            : result.ExitCode is null ? "failed"
            : "failed";
        return new AgentCloudSendResult(status, said ?? (result.ExitCode is null ? "Claude Code did not answer in time." : null));
    }

    /// <summary>Reads the result of <c>claude -p /cost --teleport …</c>: whether it ended well, and how many model turns it made (none is expected).</summary>
    public static (bool Ok, int Turns, string? Message) ReadTeleport(ClaudeCodeCommandResult result)
    {
        var answer = TryReadObject(result.Output);
        var turns = answer is { } value && ClaudeCodeJson.GetInt64(value, "num_turns") is { } count ? (int)count : 0;
        if (result.ExitCode == 0 && answer is { } done && !ClaudeCodeJson.GetBoolean(done, "is_error"))
        {
            return (true, turns, null);
        }

        // With an uncommitted change of a tracked file the CLI ends with 1 and writes nothing: CodeAlta checks it first.
        var said = Clean(answer is { } failed ? ClaudeCodeJson.GetString(failed, "result") : null) ?? Clean(FirstLines(result.Error));
        return (false, turns, said ?? (result.ExitCode is null
            ? "Claude Code did not bring the session back in time."
            : $"Claude Code did not bring the session back (exit code {result.ExitCode})."));
    }

    /// <summary>Runs a command of the CLI that ends by itself, its input given whole and then closed.</summary>
    /// <param name="launch">The command.</param>
    /// <param name="input">What it reads on its input, or null for nothing.</param>
    /// <param name="timeout">How long it may run before it is ended.</param>
    /// <param name="cancellationToken">Ends it.</param>
    /// <exception cref="InvalidOperationException">It could not be started.</exception>
    /// <exception cref="OperationCanceledException">It was canceled.</exception>
    public static async Task<ClaudeCodeCommandResult> RunAsync(ClaudeCodeLaunch launch, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(launch.WorkingDirectory)) startInfo.WorkingDirectory = launch.WorkingDirectory;
        foreach (var argument in launch.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Claude Code could not be started from '{launch.FileName}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Claude Code could not be started from '{launch.FileName}': {ex.Message}", ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            // The time limit covers the whole run, the writing of its input included: a CLI that does not read is ended too.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                try
                {
                    if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // It ended before it read its input: what it wrote says why.
                }

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                if (!timedOut) throw;
            }

            var written = await output.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            var errors = await error.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return new ClaudeCodeCommandResult(timedOut ? null : process.ExitCode, written, errors);
        }
    }

    /// <summary>How long a send may take.</summary>
    public static TimeSpan SendTimeLimit => SendTimeout;

    /// <summary>How long a bring-back may take: the CLI fetches the branch of the session.</summary>
    public static TimeSpan TeleportTimeLimit => TeleportTimeout;

    // The environment of a command of the cloud: the one of the provider, and never an API key, the variables of the
    // provider included: a cloud session runs on the claude.ai account the CLI is signed in to.
    private static Dictionary<string, string?> CloudEnvironment(ClaudeCodeModelProviderRuntimeOptions options)
    {
        var environment = ClaudeCodeLauncher.CreateEnvironment(options, null, withoutApiKey: true);
        // Any spelling of it: the environment of Windows does not tell case apart.
        foreach (var name in environment.Keys.Where(static name => string.Equals(name, ClaudeCodeApiKey.Variable, StringComparison.OrdinalIgnoreCase)).ToList())
            environment.Remove(name);
        environment[ClaudeCodeApiKey.Variable] = null;
        return environment;
    }

    private static string Checked(string cloudSessionId)
        => IsCloudSessionId(cloudSessionId) ? cloudSessionId : throw new ArgumentException("A cloud session is named session_… or cse_….", nameof(cloudSessionId));

    private static string CheckedConversation(string conversationId)
        => ClaudeCodeConversationLinks.IsConversationId(conversationId)
            ? conversationId
            : throw new ArgumentException("A conversation of the Claude Code CLI is named by a GUID.", nameof(conversationId));

    private static JsonElement? TryReadObject(string text)
    {
        // The JSON answer is the last line of the output.
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? FirstLines(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4));

    // What the CLI said, as one bounded text without control characters but new lines.
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kept = new string([.. text.Where(static character => character == '\n' || !char.IsControl(character))]).Trim();
        return kept.Length == 0 ? null : kept.Length <= 600 ? kept : kept[..600];
    }
}
