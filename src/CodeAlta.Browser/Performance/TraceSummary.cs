using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Browser.Performance;

/// <summary>
/// What a performance trace of Chromium says of a page, computed in .NET from the events of the trace: its paints,
/// layout shifts, long tasks, the time of its main thread and its slowest requests. DevTools analyses more (its
/// insights are JavaScript); the trace file opens in its Performance panel for that.
/// </summary>
internal sealed class TraceSummary
{
    private const double LongTaskMilliseconds = 50;

    // The events of the main thread by kind of work, as the summary of the Performance panel groups them. Only the
    // outermost event of a kind is counted, so that nested events are not counted twice.
    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
    {
        ["EvaluateScript"] = "scripting", ["v8.compile"] = "scripting", ["v8.compileModule"] = "scripting", ["v8.evaluateModule"] = "scripting",
        ["FunctionCall"] = "scripting", ["TimerFire"] = "scripting", ["EventDispatch"] = "scripting", ["FireAnimationFrame"] = "scripting",
        ["FireIdleCallback"] = "scripting", ["RunMicrotasks"] = "scripting", ["v8.run"] = "scripting", ["XHRReadyStateChange"] = "scripting",
        ["Layout"] = "rendering", ["UpdateLayoutTree"] = "rendering", ["RecalculateStyles"] = "rendering", ["HitTest"] = "rendering",
        ["PrePaint"] = "rendering", ["UpdateLayer"] = "rendering", ["IntersectionObserverController::computeIntersections"] = "rendering",
        ["Paint"] = "painting", ["PaintImage"] = "painting", ["CompositeLayers"] = "painting", ["Commit"] = "painting", ["Layerize"] = "painting",
        ["Decode Image"] = "painting", ["ParseHTML"] = "loading", ["ParseAuthorStyleSheet"] = "loading",
    };

    public string Url { get; private set; } = string.Empty;
    public double DurationMs { get; private set; }
    public double? FirstContentfulPaintMs { get; private set; }
    public double? LargestContentfulPaintMs { get; private set; }
    public double LayoutShift { get; private set; }
    public double? DomContentLoadedMs { get; private set; }
    public double? LoadMs { get; private set; }
    public List<(double StartMs, double DurationMs)> LongTasks { get; } = [];
    public SortedDictionary<string, double> MainThreadMs { get; } = new(StringComparer.Ordinal);
    public List<(string Url, double DurationMs, long Bytes)> SlowestRequests { get; } = [];

    /// <summary>Reads a trace, as <c>{"traceEvents": [...]}</c> or a bare array of events.</summary>
    public static TraceSummary Summarize(Stream trace, string pageUrl)
    {
        using var document = JsonDocument.Parse(trace, new JsonDocumentOptions { MaxDepth = 256 });
        var events = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
            : document.RootElement.TryGetProperty("traceEvents", out var list) ? list : default;
        var summary = new TraceSummary { Url = pageUrl };
        if (events.ValueKind != JsonValueKind.Array) return summary;

        // The renderer main thread is the one named CrRendererMain; the navigation of its main frame starts the clock.
        var mainThreads = new HashSet<(long Pid, long Tid)>();
        double? first = null, last = null, navigation = null;
        foreach (var item in events.EnumerateArray())
        {
            if (Number(item, "ts") is { } ts)
            {
                first = first is null ? ts : Math.Min(first.Value, ts);
                last = last is null ? ts : Math.Max(last.Value, ts);
            }

            var name = Text(item, "name");
            if (name == "thread_name" && Text(item, "ph") == "M" && item.TryGetProperty("args", out var args) && Text(args, "name") == "CrRendererMain")
            {
                mainThreads.Add((Integer(item, "pid"), Integer(item, "tid")));
            }

            if (name == "navigationStart" && Number(item, "ts") is { } start && Data(item) is { } data
                && data.TryGetProperty("isLoadingMainFrame", out var main) && main.ValueKind == JsonValueKind.True
                && Text(data, "documentLoaderURL") is { Length: > 0 } url && url != "about:blank")
            {
                navigation = start;
                summary.Url = url;
            }
        }

        if (first is null) return summary;
        var origin = navigation ?? first.Value;
        summary.DurationMs = (last!.Value - first.Value) / 1000;

        var tasks = new List<(double Start, double End, string Category)>();
        var requests = new Dictionary<string, (string Url, double Start)>(StringComparer.Ordinal);
        foreach (var item in events.EnumerateArray())
        {
            var name = Text(item, "name");
            if (name is null || Number(item, "ts") is not { } ts) continue;
            var relative = (ts - origin) / 1000;
            switch (name)
            {
                case "firstContentfulPaint" when ts >= origin:
                    summary.FirstContentfulPaintMs ??= relative;
                    break;
                case "largestContentfulPaint::Candidate" when ts >= origin:
                    summary.LargestContentfulPaintMs = relative;
                    break;
                case "LayoutShift" when Data(item) is { } shift && !(shift.TryGetProperty("had_recent_input", out var input) && input.ValueKind == JsonValueKind.True):
                    summary.LayoutShift += Number(shift, "weighted_score_delta") ?? Number(shift, "score") ?? 0;
                    break;
                case "MarkDOMContent" when ts >= origin && IsMainFrame(item):
                    summary.DomContentLoadedMs ??= relative;
                    break;
                case "MarkLoad" when ts >= origin && IsMainFrame(item):
                    summary.LoadMs ??= relative;
                    break;
                case "ResourceSendRequest" when Data(item) is { } request && Text(request, "requestId") is { } id:
                    requests[id] = (Text(request, "url") ?? string.Empty, ts);
                    break;
                case "ResourceFinish" when Data(item) is { } finish && Text(finish, "requestId") is { } id && requests.TryGetValue(id, out var sent):
                    summary.SlowestRequests.Add((sent.Url, (ts - sent.Start) / 1000, (long)(Number(finish, "encodedDataLength") ?? 0)));
                    break;
            }

            if (Text(item, "ph") != "X" || Number(item, "dur") is not { } duration) continue;
            if (mainThreads.Count != 0 && !mainThreads.Contains((Integer(item, "pid"), Integer(item, "tid")))) continue;
            if (name == "RunTask" && duration / 1000 > LongTaskMilliseconds) summary.LongTasks.Add((relative, duration / 1000));
            if (Categories.TryGetValue(name, out var category)) tasks.Add((ts, ts + duration, category));
        }

        // Count the outermost events of a kind: an event inside a counted one of any kind is part of it.
        tasks.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));
        var coveredUntil = double.MinValue;
        foreach (var (start, end, category) in tasks)
        {
            if (start < coveredUntil) continue;
            summary.MainThreadMs[category] = summary.MainThreadMs.GetValueOrDefault(category) + (end - start) / 1000;
            coveredUntil = end;
        }

        summary.LongTasks.Sort(static (left, right) => right.DurationMs.CompareTo(left.DurationMs));
        summary.SlowestRequests.Sort(static (left, right) => right.DurationMs.CompareTo(left.DurationMs));
        if (summary.SlowestRequests.Count > 5) summary.SlowestRequests.RemoveRange(5, summary.SlowestRequests.Count - 5);
        return summary;
    }

    private static bool IsMainFrame(JsonElement item)
        => Data(item) is not { } data || !data.TryGetProperty("isMainFrame", out var main) || main.ValueKind == JsonValueKind.True;

    private static JsonElement? Data(JsonElement item)
        => item.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : null;

    private static string? Text(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long Integer(JsonElement item, string name) => (long)(Number(item, name) ?? 0);

    /// <summary>The summary as the text of a tool.</summary>
    public string ToText()
    {
        static string Ms(double value) => value.ToString("N0", CultureInfo.InvariantCulture) + " ms";
        var builder = new StringBuilder("## Summary of Performance trace findings:\n");
        builder.Append("URL: ").Append(Url).Append('\n');
        builder.Append("Trace duration: ").Append(Ms(DurationMs)).Append('\n');
        builder.Append("First Contentful Paint: ").Append(FirstContentfulPaintMs is { } fcp ? Ms(fcp) : "not found").Append('\n');
        builder.Append("Largest Contentful Paint: ").Append(LargestContentfulPaintMs is { } lcp ? Ms(lcp) : "not found").Append('\n');
        builder.Append("Cumulative Layout Shift: ").Append(LayoutShift.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
        if (DomContentLoadedMs is { } dcl) builder.Append("DOMContentLoaded: ").Append(Ms(dcl)).Append('\n');
        if (LoadMs is { } load) builder.Append("Load: ").Append(Ms(load)).Append('\n');
        builder.Append("Long tasks (over 50 ms): ").Append(LongTasks.Count).Append('\n');
        foreach (var (start, duration) in LongTasks.Take(5)) builder.Append("- ").Append(Ms(duration)).Append(" at ").Append(Ms(start)).Append('\n');
        if (MainThreadMs.Count != 0)
        {
            builder.Append("Main-thread time: ").Append(string.Join(", ", MainThreadMs.OrderByDescending(static entry => entry.Value).Select(entry => $"{entry.Key} {Ms(entry.Value)}"))).Append('\n');
        }

        if (SlowestRequests.Count != 0)
        {
            builder.Append("Slowest requests:\n");
            foreach (var (url, duration, bytes) in SlowestRequests)
            {
                builder.Append("- ").Append(Ms(duration)).Append(' ').Append((bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)).Append(" kB ")
                    .Append(url.Length > 200 ? url[..200] + "…" : url).Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }
}
