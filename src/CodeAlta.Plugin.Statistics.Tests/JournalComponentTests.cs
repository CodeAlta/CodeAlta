using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class JournalComponentTests
{
    private static JournalRecordKind Classify(string line, out RunEndKind end)
        => JournalPrefixClassifier.Classify(Encoding.UTF8.GetBytes(line.Length > JournalPrefixClassifier.PrefixBytes ? line[..JournalPrefixClassifier.PrefixBytes] : line), out end);

    [TestMethod]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":{}", "Header")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"codealta.threadHeader\",\"raw\":{}", "Header")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionState\",\"raw\":{}", "State")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"codealta.threadState\",\"raw\":{}", "State")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"local.sessionState\",\"raw\":{}", "Touch")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"local.sessionSummary\",\"raw\":{}", "Touch")]
    [DataRow("{\"$type\":\"raw\",\"backendEventType\":\"local.toolMessage\",\"raw\":{}", "Touch")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\",\"activityId\":\"x\"", "Tool")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Completed\",\"activityId\":\"x\"", "Tool")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Failed\",\"activityId\":\"x\"", "Tool")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"CommandExecution\",\"phase\":\"Canceled\",\"activityId\":\"x\"", "Tool")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Requested\",\"activityId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Progressed\",\"activityId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"Turn\",\"phase\":\"Started\",\"activityId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"activity\",\"kind\":\"Compaction\",\"phase\":\"Completed\",\"activityId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"contentCompleted\",\"kind\":\"User\",\"contentId\":\"x\"", "UserContent")]
    [DataRow("{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\",\"contentId\":\"x\"", "Content")]
    [DataRow("{\"$type\":\"contentCompleted\",\"kind\":\"Reasoning\",\"contentId\":\"x\"", "Content")]
    [DataRow("{\"$type\":\"contentCompleted\",\"kind\":\"ReasoningSummary\",\"contentId\":\"x\"", "Content")]
    [DataRow("{\"$type\":\"contentCompleted\",\"kind\":\"ToolOutput\",\"contentId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"contentDelta\",\"kind\":\"Assistant\",\"contentId\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpdated\",\"message\":\"x\"", "Usage")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"ModelChanged\",\"details\":{}", "ModelChanged")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"CompactionCompleted\",\"message\":\"x\"", "Compaction")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"CompactionStarted\",\"message\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"DiffUpdated\",\"message\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"sessionUpdate\",\"kind\":\"Shutdown\"", "Touch")]
    [DataRow("{\"$type\":\"system_prompt\",\"reason\":\"session_start\"", "SystemPrompt")]
    [DataRow("{\"$type\":\"notes\",\"kind\":\"Set\",\"markdown\":\"x\"", "Touch")]
    [DataRow("{\"$type\":\"planSnapshot\",\"snapshot\":{}", "Touch")]
    [DataRow("{\"$type\":\"permissionCommand\",\"x\":1", "Touch")]
    [DataRow("{\"$type\":\"somethingNew\",\"x\":1", "Unknown")]
    [DataRow("{\"kind\":\"UsageUpdated\",\"$type\":\"sessionUpdate\"", "Unknown")]
    [DataRow("[1,2,3]", "Unknown")]
    [DataRow("", "Unknown")]
    public void Classifier_TellsTheKindFromTheStartOfTheLine(string line, string expected)
    {
        Assert.AreEqual(Enum.Parse<JournalRecordKind>(expected), Classify(line, out _));
    }

    [TestMethod]
    public void Classifier_TellsHowARunEnded()
    {
        Assert.AreEqual(JournalRecordKind.RunEnd, Classify("{\"$type\":\"sessionUpdate\",\"kind\":\"Idle\",\"usage\":{}", out var idle));
        Assert.AreEqual(RunEndKind.Idle, idle);
        Assert.AreEqual(JournalRecordKind.RunEnd, Classify("{\"$type\":\"error\",\"message\":\"x\"", out var error));
        Assert.AreEqual(RunEndKind.Error, error);
    }

    [TestMethod]
    public void TextMetrics_AgreeWithAStringForRandomTexts()
    {
        var random = new Random(20261009);
        var alphabet = new[] { "a", "b", "word", " ", "  ", "\t", "\n", "\r\n", "\"", "\\", "/", "\u00e9", "\u4e2d", "\ud83d\ude00", "\u00a0", "'", "<", ">", "+", "&" };
        var encoders = new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        for (var round = 0; round < 500; round++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 40);
            for (var index = 0; index < parts; index++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var text = builder.ToString();
            var expectedWords = text.Split([' ', '\t', '\n', '\r', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries).Length;
            foreach (var encoder in encoders)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(text, new JsonSerializerOptions { Encoder = encoder });
                var raw = json.AsSpan(1, json.Length - 2);
                var escaped = raw.IndexOf((byte)'\\') >= 0;

                Assert.AreEqual(text.Length, JournalTextMetrics.CountChars(raw, escaped), $"characters of \"{text}\"");
                Assert.AreEqual(text.Length, JournalTextMetrics.Measure(raw, escaped, out var words), $"measured characters of \"{text}\"");
                Assert.AreEqual(expectedWords, words, $"words of \"{text}\"");
            }
        }
    }

    [TestMethod]
    public void DiffCounter_CountsLikeTheTerminal_ForEveryEscapeTheSerializerWrites()
    {
        var diff = "diff --git a/x b/x\r\n--- a/x\r\n+++ b/x\r\n@@ -1,3 +1,4 @@\r\n context\r\n-removed one\r\n-removed two\r\n+added\r\n+++not a header? it is\r\n+tail";
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(diff, new JsonSerializerOptions { Encoder = encoder });

            JournalDiffCounter.Count(json.AsSpan(1, json.Length - 2), out var added, out var removed);

            // "+++not a header..." starts with +++, so the terminal rule does not count it either.
            Assert.AreEqual(2, added);
            Assert.AreEqual(2, removed);
        }

        JournalDiffCounter.Count("+only\\n-and\\n\\\\n+not a line start"u8, out var a, out var r);
        Assert.AreEqual(1, a, "an escaped backslash followed by n is not a line end");
        Assert.AreEqual(1, r);
        JournalDiffCounter.Count(default, out var none, out var noneRemoved);
        Assert.AreEqual(0, none + noneRemoved);
    }

    [TestMethod]
    [DataRow("git status", "git")]
    [DataRow("  git   log --oneline", "git")]
    [DataRow("cd /x && dotnet build", "cd")]
    [DataRow("C:/tools/Git/bin/GIT.exe status", "git")]
    [DataRow("./build.sh --release", "build.sh")]
    [DataRow("(cd x; ls)", "cd")]
    [DataRow("& \\\"C:\\\\Program Files\\\\dotnet\\\\dotnet.exe\\\" build", "dotnet")]
    [DataRow("pwsh -NoProfile -Command \\\"Get-ChildItem\\\"", "pwsh")]
    [DataRow("npm\\ntest", "npm")]
    [DataRow("", null)]
    [DataRow("   ", null)]
    // A variable set for the command is not its program, and its value is never kept.
    [DataRow("PGPASSWORD=hunter2 psql -h db", "psql")]
    [DataRow("FOO=1 BAR=two make all", "make")]
    [DataRow("TOKEN=abc/def curl https://example.org", "curl")]
    [DataRow("API_KEY=\\\"sk live\\\" node app.js", "node")]
    [DataRow("API_KEY='sk live' node app.js", "node")]
    [DataRow("$env:GH_TOKEN='abc'; gh pr list", "gh")]
    [DataRow("$env:GH_TOKEN = 'abc'; gh pr list", null)]
    [DataRow("PGPASSWORD=hunter2", null)]
    [DataRow("PGPASSWORD=", null)]
    [DataRow("TOKEN=$(cat secret.txt) deploy", null)]
    [DataRow("TOKEN=`cat secret.txt` deploy", null)]
    [DataRow("KEY='never closed psql", null)]
    // An escape of the shell in a value: where the value ends cannot be told, and a word of it is not taken for the program.
    [DataRow("PGPASSWORD=my\\\\ secret psql", null)]
    [DataRow("TOKEN=\\\"a \\\\\\\"b c\\\\\\\" d\\\" deploy", null)]
    [DataRow("A=\\\"x\\\"y z", null)]
    [DataRow("$env:T=\\\"a `\\\"b c`\\\" d\\\"; gh pr list", null)]
    [DataRow("$env:T='a ''b c'' d'; gh pr list", null)]
    [DataRow("TOKEN=\\\"$(echo \\\" x \\\" y)\\\" deploy", null)]
    [DataRow("$env:GH_TOKEN= \\\"abcdef\\\"; gh pr list", null)]
    [DataRow("TOKEN= abcdef", null)]
    [DataRow("X=1|login hunter2", null)]
    [DataRow("DOTNET_ROOT=C:\\\\dotnet dotnet build", null)]
    // The journals write a quote, an ampersand and what is not ASCII as \uXXXX: it is the character, not six letters.
    [DataRow("API_KEY=\\u0027sk live\\u0027 node app.js", "node")]
    [DataRow("API_KEY=\\u0022sk live\\u0022 node app.js", "node")]
    [DataRow("$env:GH_TOKEN=\\u0027abc def\\u0027; gh pr list", "gh")]
    [DataRow("TOKEN=\\u0022a \\\\\\u0022b c\\\\\\u0022 d\\u0022 deploy", null)]
    [DataRow("$env:T=\\u0027a \\u0027\\u0027b c\\u0027\\u0027 d\\u0027; gh pr list", null)]
    [DataRow("$env:T=\\u0022a \\u0060\\u0022b c\\u0060\\u0022 d\\u0022; gh pr list", null)]
    [DataRow("TOKEN=\\u0060cat secret.txt\\u0060 deploy", null)]
    [DataRow("\\u0026 \\u0022C:\\\\Program Files\\\\dotnet\\\\dotnet.exe\\u0022 build", "dotnet")]
    [DataRow("cd x \\u0026\\u0026 dotnet build", "cd")]
    [DataRow("git\\u0009status", "git")]
    [DataRow("caf\\u00E9 --version", "caf\u00e9")]
    [DataRow("\\uD83D\\uDE00 x", null)]
    [DataRow("C:\\\\tools\\\\Git\\\\bin\\\\git.exe status", "git")]
    [DataRow("/opt/my\\\\ tool/run x", null)]
    [DataRow("KEY=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa psql", null)]
    // What is not the name of a program is not kept either.
    [DataRow("\\\"hunter2=x\\\" | tool", null)]
    [DataRow("[System.IO.File]::ReadAllText('x')", null)]
    [DataRow("@'", null)]
    [DataRow("Get-ChildItem -Recurse", "get-childitem")]
    [DataRow("g++ -O2 a.cpp", "g++")]
    public void ShellProgram_IsTheFirstWordOfTheCommand(string rawJsonText, string? expected)
    {
        var b = new JournalBuilder();
        var line = $"{{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\",\"activityId\":\"a\",\"name\":\"shell_command\",\"details\":{{\"toolCallId\":\"a\",\"toolName\":\"shell_command\",\"arguments\":{{\"command\":\"{rawJsonText}\"}},\"readFiles\":[],\"modifiedFiles\":[]}},{b.Envelope(JournalBuilder.Time(0), "r")}";
        using var scanner = new JournalScanner();
        var sink = new CollectingSink();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));

        scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(expected, ((ToolRecord)sink.Records.Single()).ShellProgram);
    }

    [TestMethod]
    [DataRow("\"session\",\"create\",\"--project\",\"p\"", "session create")]
    [DataRow("\"session\",\"set_agent\",\"--prompt-id\",\"plan\"", "session set_agent")]
    [DataRow("\"mcp\",\"activate\",\"codealta-dev\"", "mcp activate")]
    [DataRow("\"notes\",\"set\",\"--stdin\"", "notes set")]
    [DataRow("\"--help\"", null)]
    // A word that is not written as a command is a value: a sentence, a name, a path. It is not kept.
    [DataRow("\"ask\",\"What is the password of prod?\"", "ask")]
    [DataRow("\"task\",\"Hunter2\"", "task")]
    [DataRow("\"estimate\",\"src/secret.txt\"", "estimate")]
    [DataRow("\"A sentence first\",\"list\"", null)]
    public void AltaCommand_IsTheFirstTwoCommandWords(string words, string? expected)
    {
        var b = new JournalBuilder();
        var line = $"{{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\",\"activityId\":\"a\",\"name\":\"alta\",\"details\":{{\"toolCallId\":\"a\",\"toolName\":\"alta\",\"arguments\":{{\"args\":[{words}]}},\"readFiles\":[],\"modifiedFiles\":[]}},{b.Envelope(JournalBuilder.Time(0), "r")}";
        using var scanner = new JournalScanner();
        var sink = new CollectingSink();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));

        scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(expected, ((ToolRecord)sink.Records.Single()).AltaCommand);
    }

    [TestMethod]
    [DataRow(AgentActivityKind.ToolCall, "read_file", "Files")]
    [DataRow(AgentActivityKind.ToolCall, "Edit", "Files")]
    [DataRow(AgentActivityKind.ToolCall, "apply_patch", "Files")]
    [DataRow(AgentActivityKind.ToolCall, "GREP", "Search")]
    [DataRow(AgentActivityKind.ToolCall, "Glob", "Search")]
    [DataRow(AgentActivityKind.ToolCall, "shell_command", "Shell")]
    [DataRow(AgentActivityKind.ToolCall, "PowerShell", "Shell")]
    [DataRow(AgentActivityKind.ToolCall, "Bash", "Shell")]
    [DataRow(AgentActivityKind.CommandExecution, "anything", "Shell")]
    [DataRow(AgentActivityKind.ToolCall, "webget", "Web")]
    [DataRow(AgentActivityKind.WebSearch, "q", "Web")]
    [DataRow(AgentActivityKind.ToolCall, "alta", "Alta")]
    [DataRow(AgentActivityKind.ToolCall, "mcp__codealta__alta", "Alta")]
    [DataRow(AgentActivityKind.ToolCall, "mcp__github__issue_read", "Mcp")]
    [DataRow(AgentActivityKind.McpToolCall, "search", "Mcp")]
    [DataRow(AgentActivityKind.Skill, "ilspy", "Skill")]
    [DataRow(AgentActivityKind.ToolCall, "codealta_skills_activate", "Skill")]
    [DataRow(AgentActivityKind.ToolCall, "gh", "Other")]
    [DataRow(AgentActivityKind.ToolCall, "", "Other")]
    [DataRow(AgentActivityKind.DynamicToolCall, "x", "Other")]
    public void ToolKinds_FollowTheFixedList(AgentActivityKind kind, string name, string expected)
    {
        Assert.AreEqual(Enum.Parse<ToolKind>(expected), StatisticsToolBuckets.KindOf(kind, name));
    }

    [TestMethod]
    public void ToolBucket_IsTheRuleOfTheCards()
    {
        Assert.AreEqual("shell", StatisticsToolBuckets.Bucket(AgentActivityKind.ToolCall, "shell_command"));
        Assert.AreEqual("shell", StatisticsToolBuckets.Bucket(AgentActivityKind.CommandExecution, null));
        Assert.AreEqual("ToolCall:read_file", StatisticsToolBuckets.Bucket(AgentActivityKind.ToolCall, " read_file "));
        Assert.AreEqual("WebSearch:WebSearch", StatisticsToolBuckets.Bucket(AgentActivityKind.WebSearch, null));
        Assert.IsTrue(StatisticsToolBuckets.TrySplitMcp("mcp__codealta_dev__take_snapshot", out var server, out var tool));
        Assert.AreEqual("codealta_dev", server);
        Assert.AreEqual("take_snapshot", tool);
        Assert.IsFalse(StatisticsToolBuckets.TrySplitMcp("mcp__only", out _, out _));
        Assert.IsFalse(StatisticsToolBuckets.TrySplitMcp("read_file", out _, out _));
    }

    [TestMethod]
    public void Providers_FoldTheirOldNames()
    {
        Assert.AreEqual("codex", StatisticsProviders.Fold("codex_cli"));
        Assert.AreEqual("copilot", StatisticsProviders.Fold("copilot_cli"));
        Assert.AreEqual("claude-code", StatisticsProviders.Fold("claude-code"));
        Assert.AreEqual("mistral", StatisticsProviders.Fold("mistral"));
        Assert.AreEqual(StatisticsProviders.Unknown, StatisticsProviders.Fold(null));
        Assert.AreEqual(StatisticsProviders.Unknown, StatisticsProviders.Fold(" "));
    }

    [TestMethod]
    public void QuarterHours_CoverTheTimeInWholeUtcSteps()
    {
        var time = new DateTimeOffset(2026, 10, 9, 10, 14, 59, 999, TimeSpan.Zero);
        var quarter = QuarterHour.Of(time);

        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), quarter.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 10, 15, 0, TimeSpan.Zero), quarter.End);
        Assert.AreEqual(quarter.Index + 1, QuarterHour.Of(quarter.End).Index);
        Assert.AreEqual(quarter.Index, QuarterHour.Of(quarter.End.AddTicks(-1)).Index);
        // The same instant in another offset is the same quarter: a local day is a sum of quarters whatever the zone.
        Assert.AreEqual(quarter, QuarterHour.Of(time.ToOffset(TimeSpan.FromHours(5.75))));
        Assert.AreEqual("2026-10-09T10:00Z", quarter.ToString());
        Assert.AreEqual(-1, QuarterHour.Of(DateTimeOffset.UnixEpoch.AddMinutes(-1)).Index);
    }

    [TestMethod]
    public void FactsState_RoundTripsEveryField()
    {
        var state = new SessionFactsState
        {
            HasHeader = true,
            ProjectRef = "p",
            SessionKind = "ProjectSession",
            ParentSessionId = "parent",
            CreatedByKind = "agent",
            CreatedBySessionId = "creator",
            AutomationId = "auto",
            Title = "t",
            InitialProvider = "codex",
            OriginCounted = true,
            FirstRecord = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero),
            LastRecord = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
            Provider = "codex",
            Model = "m",
            Effort = "high",
            AgentPrompt = "default",
            PermissionMode = "ask",
            ModelAliases = { ["opus"] = "claude-opus-5-5" },
            OpenRuns = [new OpenRunState { RunId = "r", Start = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), AccountedTo = new DateTimeOffset(2026, 10, 9, 10, 5, 0, TimeSpan.Zero), RemainderTicks = 1234, Sender = PromptSender.Agent, PromptKind = PromptKind.Queued, CostUsd = 1.5, LastCost = 1.5, LastCostDuration = 20 }],
            ClosedRuns = ["a", "b"],
            OpenTools = [new OpenToolState { ActivityId = "t", Start = new DateTimeOffset(2026, 10, 9, 10, 1, 0, TimeSpan.Zero) }],
            RecentPrompts = [new RecentPromptState { At = new DateTimeOffset(2026, 10, 9, 10, 2, 0, TimeSpan.Zero), RunId = "r", Sender = PromptSender.Reminder, Kind = PromptKind.Steer, Bound = true, Chars = 5, Words = 2 }],
            UnmatchedProvenance = [new PendingProvenanceState { IdHash = ulong.MaxValue, DispatchKind = "send", Queued = true, ActorKind = "agent", CreatedAt = new DateTimeOffset(2026, 10, 9, 10, 3, 0, TimeSpan.Zero) }],
            SeenPrompts = [1UL, ulong.MaxValue],
        };

        var json = state.ToJson();
        var back = SessionFactsState.FromJson(json);

        Assert.AreEqual(json, back.ToJson());
        Assert.AreEqual(ulong.MaxValue, back.UnmatchedProvenance[0].IdHash);
        Assert.AreEqual(PromptSender.Agent, back.OpenRuns[0].Sender);
        Assert.AreEqual("claude-opus-5-5", back.ModelAliases["opus"]);
        Assert.AreEqual(state.ToUtf8Json().Length, SessionFactsState.FromUtf8Json(state.ToUtf8Json()).ToUtf8Json().Length);
        Assert.AreEqual(0, SessionFactsState.FromJson("not json").Version, "a state that cannot be read is of no version");
        Assert.AreEqual(0, SessionFactsState.FromUtf8Json("["u8).Version);
    }
}
