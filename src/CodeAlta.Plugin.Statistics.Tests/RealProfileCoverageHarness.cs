using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Counts, over a real profile, the records whose parsed fields came out empty where a field is expected, to find shapes the
/// parser does not read. Skipped unless <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder; it prints counts only.
/// </summary>
[TestClass]
public sealed class RealProfileCoverageHarness
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class CoverageSink : IJournalRecordSink
    {
        public readonly SortedDictionary<string, long> Counts = [];

        private void Bump(string name) => Counts[name] = Counts.GetValueOrDefault(name) + 1;

        public void OnRecord(JournalRecord record)
        {
            switch (record)
            {
                case ToolRecord tool:
                    Bump($"tool {tool.Phase}");
                    if (string.IsNullOrEmpty(tool.Name))
                    {
                        Bump("tool without a name");
                    }

                    if (string.IsNullOrEmpty(tool.ActivityId))
                    {
                        Bump("tool without an id");
                    }

                    if (tool.Phase == ToolPhase.Started && tool.ArgumentBytes == 0)
                    {
                        Bump("started tool without arguments");
                    }

                    if (tool.Phase != ToolPhase.Started && tool.ResultBytes == 0 && !tool.Oversize)
                    {
                        Bump("ended tool without a result");
                    }

                    if (tool.Phase == ToolPhase.Started && tool.Name is "shell_command" or "Bash" or "PowerShell" && tool.ShellProgram is null)
                    {
                        Bump("shell call without a program");
                    }

                    if (tool.Phase == ToolPhase.Started && tool.Name == "alta" && tool.AltaCommand is null)
                    {
                        Bump("alta call without a command");
                    }

                    break;
                case UsageRecord usage:
                    Bump(usage.Operation is null ? "usage without an operation" : "usage with an operation");
                    if (usage.Operation is { Model: null or "" })
                    {
                        Bump("usage without a model");
                    }

                    if (usage.WindowLimit is null)
                    {
                        Bump("usage without a window");
                    }

                    break;
                case UserContentRecord user:
                    if (user.Chars == 0 && !user.Oversize)
                    {
                        Bump("prompt without text");
                    }

                    break;
                case ContentRecord content:
                    if (content.Chars == 0 && !content.Oversize)
                    {
                        Bump($"{content.Channel} without text");
                    }

                    break;
                case ModelChangedRecord changed:
                    if (string.IsNullOrEmpty(changed.ModelId))
                    {
                        Bump("model change without a model");
                    }

                    break;
                case HeaderRecord header:
                    if (string.IsNullOrEmpty(header.ProjectRef))
                    {
                        Bump("header without a project");
                    }

                    break;
                case StateRecord state:
                    Bump("state");
                    if (state.Provenance.Count > 0)
                    {
                        Bump("state with new provenance");
                    }

                    break;
                case CompactionRecord compaction:
                    if (compaction.TokensBefore is null)
                    {
                        Bump("compaction without tokens");
                    }

                    break;
                case SystemPromptRecord prompt:
                    if (prompt.SystemChars + prompt.DeveloperChars == 0)
                    {
                        Bump("instructions without text");
                    }

                    break;
            }
        }
    }

    [TestMethod]
    public void ParsedRecordsHaveTheFieldsTheFactsNeed()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var sink = new CoverageSink();
        using var scanner = new JournalScanner();
        foreach (var path in Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);
        }

        foreach (var (name, count) in sink.Counts)
        {
            TestContext.WriteLine($"{name}: {count}");
        }
    }
}
