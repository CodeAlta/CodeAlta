using System.Diagnostics;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Read-only harness over a real session store. Skipped unless <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder
/// (for example <c>~/.alta/sessions</c>); it prints counts, sizes and timings only, never a text of a session.
/// </summary>
[TestClass]
public sealed class RealProfileHarness
{
    private sealed class CountingSink : IJournalRecordSink
    {
        public readonly long[] Kinds = new long[16];
        public readonly long[] Malformed = new long[16];

        public void OnRecord(JournalRecord record) => Kinds[(int)record.Kind]++;
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ScanTheWholeProfile_Throughput()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var files = Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var sink = new CountingSink();
        using var scanner = new JournalScanner();
        var malformedKinds = new long[16];
        long bytes = 0, lines = 0, malformed = 0, unknown = 0, oversize = 0, skippedBytes = 0, parsed = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var path in files)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var result = scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);
            bytes += result.EndOffset;
            lines += result.Lines;
            malformed += result.MalformedLines;
            for (var kind = 0; kind < malformedKinds.Length; kind++)
            {
                malformedKinds[kind] += result.MalformedByKind[kind];
            }

            unknown += result.UnknownLines;
            oversize += result.OversizeRecords;
            skippedBytes += result.SkippedBytes;
            parsed += result.ParsedRecords;
        }

        stopwatch.Stop();
        var seconds = stopwatch.Elapsed.TotalSeconds;
        TestContext.WriteLine($"files={files.Length} bytes={bytes / 1048576.0:F0} MiB lines={lines} parsed={parsed} skippedBytes={skippedBytes / 1048576.0:F0} MiB oversize={oversize} malformed={malformed} unknown={unknown}");
        TestContext.WriteLine($"elapsed={seconds:F1}s throughput={bytes / 1048576.0 / seconds:F0} MiB/s peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB");
        TestContext.WriteLine("kinds: " + string.Join(", ", Enum.GetValues<JournalRecordKind>().Select(kind => $"{kind}={sink.Kinds[(int)kind]}")));
        TestContext.WriteLine("malformed: " + string.Join(", ", Enum.GetValues<JournalRecordKind>().Select(kind => $"{kind}={malformedKinds[(int)kind]}")));
        Assert.AreEqual(0, unknown);
    }
}

/// <summary>The same harness, with the reducer: prints the sanity totals of the facts of a whole profile.</summary>
[TestClass]
public sealed class RealProfileFactsHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ReduceTheWholeProfile_Totals()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var files = Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var totals = new Totals();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var catchUp = new SessionCatchUp();
        foreach (var path in files)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var result = catchUp.CatchUp(id, stream, null, long.MaxValue, CancellationToken.None);
            totals.Add(result);
        }

        stopwatch.Stop();
        TestContext.WriteLine($"sessions={files.Length} elapsed={stopwatch.Elapsed.TotalSeconds:F1}s peakWorkingSet={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB gcAllocated={GC.GetTotalAllocatedBytes() / 1048576.0:F0} MiB");
        foreach (var line in totals.Lines())
        {
            TestContext.WriteLine(line);
        }
    }

    private sealed class Totals
    {
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public void Add(CatchUpResult result)
        {
            var b = result.Batch;
            Bump("sessions", 1);
            if (b.Session?.HasHeader == true)
            {
                Bump("sessions with header", 1);
            }

            foreach (var (key, v) in b.Activity)
            {
                Bump("runs started", v.RunsStarted);
                Bump("runs completed", v.RunsCompleted);
                Bump("runs failed", v.RunsFailed);
                Bump("runs interrupted", v.RunsInterrupted);
                Bump("errors", v.Errors);
                Bump("active seconds", v.ActiveMs / 1000);
                Bump("compactions", v.Compactions);
            }

            Bump("runs still open at the end of the file", result.Cursor.State.OpenRuns.Count);
            foreach (var (key, v) in b.Usage)
            {
                var suffix = key.Purpose == UsagePurpose.Turn ? "turn" : "compaction";
                Bump($"requests ({suffix})", v.Requests);
                Bump($"input tokens ({suffix})", v.InputTokens);
                Bump($"  fresh input ({suffix})", v.FreshInputTokens);
                Bump($"  cache read ({suffix})", v.CacheReadTokens);
                Bump($"  cache write ({suffix})", v.CacheWriteTokens);
                Bump($"output tokens ({suffix})", v.OutputTokens);
                Bump($"  reasoning ({suffix})", v.ReasoningTokens);
                Bump($"requests by provider {key.Provider}", v.Requests);
            }

            foreach (var (key, v) in b.Cost)
            {
                Bump($"cost {key.Unit} (x1000)", (long)(v.Total * 1000));
                Bump($"cost records {key.Unit}", v.Records);
            }

            foreach (var (key, v) in b.Tools)
            {
                Bump("tool calls", v.Calls);
                Bump("tool failures", v.Failures);
                Bump("tool canceled", v.Canceled);
                Bump($"tool calls {key.Kind}", v.Calls);
                if (key.Kind is ToolKind.Other or ToolKind.Mcp)
                {
                    Bump($"tool bucket {key.Kind} {(key.Kind == ToolKind.Mcp ? key.Tool[..Math.Min(key.Tool.Length, 28)] : key.Tool)}", v.Calls);
                }

                Bump("tool duration seconds", v.DurationMsTotal / 1000);
                Bump("tool bytes in", v.BytesIn);
                Bump("tool bytes out", v.BytesOut);
                Bump("files read", v.FilesRead);
                Bump("files changed", v.FilesChanged);
                Bump("lines added", v.LinesAdded);
                Bump("lines removed", v.LinesRemoved);
            }

            foreach (var (key, v) in b.Content)
            {
                var name = key.Kind == ContentKind.Prompt ? $"prompts {key.Sender}/{key.PromptKind}" : $"content {key.Kind}";
                Bump(name, v.Count);
                Bump(name + " chars", v.Chars);
            }

            foreach (var (key, v) in b.Details)
            {
                Bump($"detail {key.List}", v);
            }

            foreach (var (key, v) in b.Histograms)
            {
                Bump($"histogram observations {key.Measure}", v);
            }

            foreach (var row in b.Runs.Values)
            {
                Bump($"run rows {row.Outcome}", 1);
                if (row.Sender != PromptSender.None)
                {
                    Bump($"run rows started by {row.Sender}", 1);
                }
            }

            var d = result.Diagnostics;
            Bump("diag records after run end", d.RecordsAfterRunEnd);
            Bump("diag runs interrupted by next run", d.RunsInterruptedByNextRun);
            Bump("diag records after interruption", d.RecordsAfterInterruption);
            Bump("diag requests with another model than in force", d.ModelRenamedByUsage);
            Bump("diag repeated operations", d.RepeatedOperations);
            Bump("diag provenance entries", d.ProvenanceEntries);
            Bump("diag provenance matched a prompt counted before", d.ProvenanceMatchedLate);
            Bump("diag provenance matched a prompt after it", d.ProvenanceMatchedEarly);
            Bump("diag provenance corrections", d.ProvenanceCorrections);
            Bump("scan lines", result.Scan.Lines);
            Bump("scan malformed", result.Scan.MalformedLines);
            Bump("scan oversize", result.Scan.OversizeRecords);
            Bump("restarts", result.Restarted ? 1 : 0);
            Bump("open tool calls left", result.Cursor.State.OpenTools.Count);
            Bump("unmatched provenance left", result.Cursor.State.UnmatchedProvenance.Count);
            Max("largest state json bytes", result.Cursor.State.ToUtf8Json().Length);
        }

        private void Bump(string name, long value) => _counts[name] = _counts.GetValueOrDefault(name) + value;

        private void Max(string name, long value) => _counts[name] = Math.Max(_counts.GetValueOrDefault(name), value);

        public IEnumerable<string> Lines() => _counts.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}: {pair.Value}");
    }
}
