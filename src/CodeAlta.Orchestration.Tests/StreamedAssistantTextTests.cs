using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

// The incremental sanitizing of a streamed assistant text tells, delta by delta, exactly what stripping and trimming the
// whole text again for each delta told.
[TestClass]
public sealed class StreamedAssistantTextTests
{
    private static readonly Regex ScheduleBlockRegex = new(
        @"```codealta_schedule\s*\n.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    [TestMethod]
    public void Append_TellsWhatTheWholeTextStrippedAgainTells_ForRandomStreams()
    {
        string[] pieces =
        [
            "Hello", " ", "world", "\n", "\n\n", "  ", "\t", "```", "csharp\n", "var x = 1;\n", "```\n", "`", "``",
            "```codealta_schedule\n", "```CODEALTA_SCHEDULE \n", "{\"at\":\"later\"}\n", "codealta_", "schedule\n",
            "é", "\U0001F600", " ", " ", "done.", "", "```codealta_sched", "ule\npayload", "```",
        ];
        var random = new Random(20261019);
        for (var stream = 0; stream < 4000; stream++)
        {
            var reference = new ReferenceText();
            var text = new StreamedAssistantText();
            var count = random.Next(1, 40);
            for (var index = 0; index < count; index++)
            {
                var delta = pieces[random.Next(pieces.Length)];
                if (random.Next(5) == 0) delta += pieces[random.Next(pieces.Length)];
                Assert.AreEqual(reference.Append(delta), text.Append(delta), $"stream {stream}, delta {index}");
            }
        }
    }

    [TestMethod]
    public void Append_LongTextWithoutBlock_TellsEachDeltaOnce()
    {
        var text = new StreamedAssistantText();
        var told = new StringBuilder();
        for (var index = 0; index < 20_000; index++)
        {
            told.Append(text.Append(index % 50 == 0 ? "\n" : "word "));
        }

        Assert.AreEqual(Enumerable.Range(0, 20_000).Aggregate(new StringBuilder(), (b, i) => b.Append(i % 50 == 0 ? "\n" : "word ")).ToString().Trim(),
            told.ToString());
    }

    [TestMethod]
    public void Append_NullOrEmptyDelta_TellsNothing()
    {
        var text = new StreamedAssistantText();
        Assert.IsNull(text.Append(null));
        Assert.IsNull(text.Append(string.Empty));
        Assert.AreEqual("a", text.Append("a"));
        Assert.IsNull(text.Append(null));
    }

    // The sanitizing as it was: the whole text stripped and trimmed again for each delta.
    private sealed class ReferenceText
    {
        private readonly StringBuilder _raw = new();
        private string _previous = string.Empty;

        public string? Append(string delta)
        {
            _raw.Append(delta);
            var raw = _raw.ToString();
            var stripped = string.IsNullOrEmpty(raw) ? raw : ScheduleBlockRegex.Replace(raw, string.Empty).Trim();
            if (stripped.Length == 0) return null;
            var told = stripped.StartsWith(_previous, StringComparison.Ordinal) ? stripped[_previous.Length..] : stripped;
            _previous = stripped;
            return string.IsNullOrEmpty(told) ? null : told;
        }
    }
}
