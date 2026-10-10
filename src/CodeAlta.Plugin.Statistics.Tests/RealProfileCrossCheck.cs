using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Checks the facts of a whole profile against a second, naive computation of the same counts (a JSON DOM over every line the
/// facts read), so that the streaming reader and the reducer are not the only witnesses of their own numbers. Skipped unless
/// <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder; it prints counts only.
/// </summary>
[TestClass]
public sealed class RealProfileCrossCheck
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheFactsAgreeWithANaiveDomReading()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var files = Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        // The profile may be written while the harness runs: both readings stop at the same line end of each file.
        var limits = files.ToDictionary(static path => path, SnapshotLength);
        var naive = new Counts();
        foreach (var path in files)
        {
            using var stream = new LimitedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), limits[path]);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length <= 3_000_000 && line.StartsWith("{\"$type\":\"", StringComparison.Ordinal))
                {
                    Naive(line, naive);
                }
            }
        }

        var facts = new Counts();
        using var catchUp = new SessionCatchUp();
        foreach (var path in files)
        {
            using var stream = new LimitedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan), limits[path]);
            Facts(catchUp.CatchUp(Path.GetFileNameWithoutExtension(path), stream, null, long.MaxValue, CancellationToken.None).Batch, facts);
        }

        TestContext.WriteLine($"requests {naive.Requests} / {facts.Requests}; compaction requests {naive.CompactionRequests} / {facts.CompactionRequests}");
        TestContext.WriteLine($"input {naive.Input} / {facts.Input}; fresh {naive.Fresh} / {facts.Fresh}; read {naive.Read} / {facts.Read}; write {naive.Write} / {facts.Write}; output {naive.Output} / {facts.Output}; reasoning {naive.Reasoning} / {facts.Reasoning}");
        TestContext.WriteLine($"tool calls started {naive.Started} / {facts.Started}; prompts {naive.Users} / {facts.Users}; answers {naive.Assistants} / {facts.Assistants}; reasonings {naive.Reasonings} / {facts.Reasonings}; errors {naive.Errors} / {facts.Errors}; compactions {naive.Compactions} / {facts.Compactions}; instructions {naive.Instructions} / {facts.Instructions}");
        Assert.AreEqual(naive.Requests, facts.Requests);
        Assert.AreEqual(naive.CompactionRequests, facts.CompactionRequests);
        Assert.AreEqual(naive.Input, facts.Input);
        Assert.AreEqual(naive.Fresh, facts.Fresh);
        Assert.AreEqual(naive.Read, facts.Read);
        Assert.AreEqual(naive.Write, facts.Write);
        Assert.AreEqual(naive.Output, facts.Output);
        Assert.AreEqual(naive.Reasoning, facts.Reasoning);
        Assert.AreEqual(naive.Users, facts.Users);
        Assert.AreEqual(naive.Assistants, facts.Assistants);
        Assert.AreEqual(naive.Reasonings, facts.Reasonings);
        Assert.AreEqual(naive.Errors, facts.Errors);
        Assert.AreEqual(naive.Compactions, facts.Compactions);
        Assert.AreEqual(naive.Instructions, facts.Instructions);
        Assert.IsTrue(facts.Started >= naive.Started, "every started call is counted; the ends without a start add to it");
    }

    private static long SnapshotLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var window = (int)Math.Min(length, 8 * 1024 * 1024);
        var buffer = new byte[window];
        stream.Position = length - window;
        var read = 0;
        while (read < window)
        {
            var count = stream.Read(buffer, read, window - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var lastLineEnd = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        return lastLineEnd < 0 ? 0 : length - window + lastLineEnd + 1;
    }

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => Math.Min(inner.Length, limit);

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = limit - inner.Position;
            return remaining <= 0 ? 0 : inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        }

        public override long Seek(long offset, SeekOrigin origin) => origin == SeekOrigin.End ? inner.Seek(Length + offset, SeekOrigin.Begin) : inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Counts
    {
        public long Requests, CompactionRequests, Input, Fresh, Read, Write, Output, Reasoning, Started, Users, Assistants, Reasonings, Errors, Compactions, Instructions;
    }

    private static void Facts(FactBatch batch, Counts counts)
    {
        foreach (var (key, value) in batch.Usage)
        {
            if (key.Purpose == UsagePurpose.Compaction)
            {
                counts.CompactionRequests += value.Requests;
                continue;
            }

            counts.Requests += value.Requests;
            counts.Input += value.InputTokens;
            counts.Fresh += value.FreshInputTokens;
            counts.Read += value.CacheReadTokens;
            counts.Write += value.CacheWriteTokens;
            counts.Output += value.OutputTokens;
            counts.Reasoning += value.ReasoningTokens;
        }

        counts.Started += batch.Tools.Sum(static pair => pair.Value.Calls);
        foreach (var (key, value) in batch.Content)
        {
            switch (key.Kind)
            {
                case ContentKind.Prompt:
                    counts.Users += value.Count;
                    break;
                case ContentKind.Answer:
                    counts.Assistants += value.Count;
                    break;
                case ContentKind.Reasoning:
                    counts.Reasonings += value.Count;
                    break;
                case ContentKind.Instructions:
                    counts.Instructions += value.Count;
                    break;
            }
        }

        counts.Errors += batch.Activity.Sum(static pair => pair.Value.Errors);
        counts.Compactions += batch.Activity.Sum(static pair => pair.Value.Compactions);
    }

    private static void Naive(string line, Counts counts)
    {
        if (line.StartsWith("{\"$type\":\"error\"", StringComparison.Ordinal))
        {
            counts.Errors++;
        }
        else if (line.StartsWith("{\"$type\":\"system_prompt\"", StringComparison.Ordinal))
        {
            counts.Instructions++;
        }
        else if (line.StartsWith("{\"$type\":\"contentCompleted\",\"kind\":\"User\"", StringComparison.Ordinal))
        {
            counts.Users++;
        }
        else if (line.StartsWith("{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\"", StringComparison.Ordinal))
        {
            counts.Assistants++;
        }
        else if (line.StartsWith("{\"$type\":\"contentCompleted\",\"kind\":\"Reasoning\"", StringComparison.Ordinal))
        {
            counts.Reasonings++;
        }
        else if (line.StartsWith("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\"", StringComparison.Ordinal))
        {
            counts.Started++;
        }
        else if (line.StartsWith("{\"$type\":\"sessionUpdate\",\"kind\":\"CompactionCompleted\"", StringComparison.Ordinal))
        {
            counts.Compactions++;
        }
        else if (line.StartsWith("{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpdated\"", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("usage", out var usage) || !usage.TryGetProperty("lastOperation", out var op))
            {
                return;
            }

            long? Long(string name) => op.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
            var snapshot = new AgentOperationUsageSnapshot(
                InputTokens: Long("inputTokens"),
                OutputTokens: Long("outputTokens"),
                CacheReadTokens: Long("cacheReadTokens"),
                CacheWriteTokens: Long("cacheWriteTokens"),
                CachedInputTokens: Long("cachedInputTokens"),
                ReasoningTokens: Long("reasoningTokens"));
            var hasTokens = snapshot.InputTokens is not null || snapshot.OutputTokens is not null || snapshot.CachedInputTokens is not null
                || snapshot.CacheReadTokens is not null || snapshot.CacheWriteTokens is not null || snapshot.ReasoningTokens is not null;
            if (!hasTokens)
            {
                return;
            }

            var initiator = op.TryGetProperty("initiator", out var value) ? value.GetString() : null;
            if (string.Equals(initiator, "compaction", StringComparison.OrdinalIgnoreCase))
            {
                counts.CompactionRequests++;
                return;
            }

            counts.Requests++;
            var split = AgentInputTokenUsage.From(snapshot);
            counts.Input += split?.Total ?? 0;
            counts.Fresh += split?.Uncached ?? 0;
            counts.Read += split?.CacheRead ?? 0;
            counts.Write += split?.CacheWrite ?? 0;
            counts.Output += Math.Max(0, snapshot.OutputTokens ?? 0);
            counts.Reasoning += Math.Max(0, snapshot.ReasoningTokens ?? 0);
        }
    }
}
