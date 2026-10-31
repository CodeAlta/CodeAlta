namespace CodeAlta.Cli.Tests;

/// <summary>
/// The help of every command: what an agent needs to use it alone (the facts below), in a bounded length, without option
/// descriptions that wrap.
/// </summary>
[TestClass]
public sealed class CliHelpTests
{
    // The options every command that composes a host takes.
    private static readonly string[] StorageAndConfig =
    [
        "--isolated", "--profile=FOLDER", "--ephemeral", "--dev", "CODEALTA_HOME", "sessions/markers", "sessions/leases", "Running in altacli",
        "-c, --config=KEY=VALUE", "--ignore-user-config", "--strict-config", "CODEALTA_CONFIG_CONTENT", "config.toml is never written",
        "api_key_env", "required = true",
    ];

    [TestMethod]
    public async Task TopLevel_ListsTheCommandsAndTheExitCodes_AndStaysShort()
    {
        using var root = new CliTestRoot();
        var help = await HelpAsync(root, null, "--help");

        foreach (var command in new[] { "run", "acp", "alta", "models", "auth" }) StringAssert.Contains(help, "\n  " + command + " ");
        AssertExitCodes(help, CliExitCodes.Completed, CliExitCodes.Failed, CliExitCodes.InvalidUsage, CliExitCodes.Aborted, CliExitCodes.Timeout,
            CliExitCodes.SessionInUse, CliExitCodes.ProviderNotReady, CliExitCodes.InvalidOutput);
        StringAssert.Contains(help, "`altacli <command> --help` for the details");
        AssertConcise(help, maximumLines: 40, maximumCharacters: 2000);

        var none = await root.RunAsync(null, []);
        Assert.AreEqual(CliExitCodes.InvalidUsage, none.ExitCode);
        Assert.AreEqual(help, none.Stdout.ReplaceLineEndings("\n"), "Without a command, the same help.");
    }

    [TestMethod]
    public async Task Run_SaysItsInputsItsEndItsOutputAndItsExitCodes()
    {
        using var root = new CliTestRoot();
        var help = await HelpAsync(root, null, "run", "--help");

        StringAssert.StartsWith(help, "Usage: altacli run [options] <prompt words...>\nUsage: altacli run [options] - < prompt.md\n");
        foreach (var fact in new[]
        {
            // Input, end, trust, environment.
            "`-` alone reads it from stdin", "<stdin>...</stdin>", "byte order mark", "-f attaches", "Ends when settled", "background jobs",
            "Full trust: no permission prompt, no question to a user", "ask, review", "CODEALTA_SESSION_ID", "CODEALTA_MODEL", "AI_AGENT=codealta",
            // Output.
            "stdout: the final answer alone", "-q hides the progress", "one ACP JSON-RPC notification per line",
            "agent_message_chunk", "agent_thought_chunk", "tool_call", "tool_call_update", "plan or usage_update",
            "sessionId, runId, stopReason", "finalMessage", "failure", "inputTokens", "cost", "durationMs",
            "--output-last-message", "--output-schema", "a mismatch is exit 7",
            // Options.
            "-m, --model=MODEL", "--tier=TIER", "--reasoning=EFFORT", "--prompt-id=ID", "--resume=ID", "--session-id=ID", "--timeout=DURATION",
            "--format=FORMAT", "-q, --quiet", "--disable-tools=PATTERNS", "`web` = webget, web_search, web_fetch", "--no-mcp", "--no-plugins", "--no-skills",
            "Examples:",
        }.Concat(StorageAndConfig))
        {
            StringAssert.Contains(help, fact);
        }

        // The notifications in the order of the lines: the header first, the result last.
        var methods = new[]
        {
            "_codealta/session ", "session/update", "_codealta/session_started", "_codealta/compaction", "_codealta/retry", "_codealta/fallback",
            "_codealta/error", "_codealta/operation", "_codealta/result",
        };
        var positions = methods.Select(method => help.IndexOf(method, StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(positions.All(static position => position >= 0) && positions.SequenceEqual(positions.Order()), string.Join(", ", positions));
        AssertExitCodes(help, CliExitCodes.Completed, CliExitCodes.Failed, CliExitCodes.InvalidUsage, CliExitCodes.Aborted, CliExitCodes.Timeout,
            CliExitCodes.SessionInUse, CliExitCodes.ProviderNotReady, CliExitCodes.InvalidOutput);
        AssertConcise(help, maximumLines: 161, maximumCharacters: 10000);
    }

    [TestMethod]
    public async Task Acp_ModelsAuthAndAlta_SayWhatTheyDoTheirOutputAndTheirExitCodes()
    {
        using var root = new CliTestRoot();

        var acp = await HelpAsync(root, null, "acp", "--help");
        foreach (var fact in new[] { "session/new", "session/load", "stopReason end_turn", "auth_required", "-32000", "Full trust", "Exit codes:", "Examples:" }.Concat(StorageAndConfig))
        {
            StringAssert.Contains(acp, fact);
        }

        AssertConcise(acp, maximumLines: 120, maximumCharacters: 8000);

        var models = await HelpAsync(root, null, "models", "--help");
        foreach (var fact in new[] { "--provider=ID", "--format=FORMAT", "{provider, model, ref, name, default}", "6 the --provider named is not ready", "Examples:" })
        {
            StringAssert.Contains(models, fact);
        }

        AssertConcise(models, maximumLines: 80, maximumCharacters: 5000);

        var auth = await HelpAsync(root, null, "auth", "--help");
        StringAssert.Contains(auth, "check");
        AssertConcise(auth, maximumLines: 20, maximumCharacters: 1000);
        var check = await HelpAsync(root, null, "auth", "check", "--help");
        foreach (var fact in new[] { "0 ready", "6 not ready", "2 unknown provider", "{provider, ready, availability, message, models, exitCode}", "Examples:" })
        {
            StringAssert.Contains(check, fact);
        }

        AssertConcise(check, maximumLines: 80, maximumCharacters: 5000);

        // altacli alta --help: its own help, then the commands of the alta tool.
        var alta = await HelpAsync(root, new FakeProvider(), "alta", "--no-plugins", "--help");
        foreach (var fact in new[] { "Usage: altacli alta [altacli options] <alta arguments...>", "alta.result first", "Left out", "--stdin", "Exit codes:", "Examples:", "session" })
        {
            StringAssert.Contains(alta, fact);
        }

        var own = alta[..alta.IndexOf("Examples:", StringComparison.Ordinal)];
        AssertConcise(own, maximumLines: 80, maximumCharacters: 5000);
    }

    private static async Task<string> HelpAsync(CliTestRoot root, FakeProvider? provider, params string[] args)
    {
        var run = await root.RunAsync(provider, args);
        Assert.AreEqual(0, run.ExitCode, run.Stderr);
        return run.Stdout.ReplaceLineEndings("\n");
    }

    private static void AssertExitCodes(string help, params int[] codes)
    {
        foreach (var code in codes) StringAssert.Matches(help, new System.Text.RegularExpressions.Regex($@"(^|\s){code} [a-z-]"), $"exit code {code}");
    }

    // Short, and no option whose description wraps to a second line.
    private static void AssertConcise(string help, int maximumLines, int maximumCharacters)
    {
        var lines = help.Split('\n');
        Assert.IsTrue(lines.Length <= maximumLines, $"{lines.Length} lines");
        Assert.IsTrue(help.Length <= maximumCharacters, $"{help.Length} characters");
        Assert.IsTrue(lines.All(static line => line.Length <= 80), string.Join("\n", lines.Where(static line => line.Length > 80)));
        var wrapped = lines.Where(static line => line.StartsWith(new string(' ', 31), StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(0, wrapped.Length, "Wrapped option descriptions:\n" + string.Join("\n", wrapped));
    }
}
