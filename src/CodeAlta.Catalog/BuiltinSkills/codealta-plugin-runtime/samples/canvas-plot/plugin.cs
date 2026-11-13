using CodeAlta.Plugins.Abstractions;

// A dashboard of test runs drawn by a script of the package folder: ui/runs.js reads the runs from this plugin with `alta.rpc` and draws
// them with Observable Plot (`@observablehq/plot`), which the application lends to plugin scripts, in the font and the colors of the
// window; one card draws the same runs from a Vega-Lite spec with the `VegaLite` component of `codealta`. The runs are made here, the
// same at each start, so that the canvas looks the same each time: a real plugin reads them from its own data.
[Plugin("canvas-plot", DisplayName = "Canvas plot", Description = "A dashboard of test runs drawn with Observable Plot, whose data lives in the plugin.")]
public sealed class CanvasPlotPlugin : PluginBase
{
    private static readonly string[] Suites = ["Catalog", "Orchestration", "Desktop", "Frontend"];

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "test-runs", Title = "Test runs", Description = "The duration and the failures of thirty days of test runs, drawn with Observable Plot.", Icon = "chart-column",
            Open = (canvas, _) =>
            {
                // The call the script makes: registered before the view is returned. The result is written as JSON in camelCase.
                canvas.Rpc.Handle<GetRuns, IReadOnlyList<TestRun>>("runs.get", (_, _) => ValueTask.FromResult(Runs()));
                return ValueTask.FromResult(PluginCanvasView.Html("<p class=\"alta-muted\">Loading the runs…</p>") with { Script = PluginScript.File("ui/runs.js"), Title = "Test runs" });
            },
            Describe = (_, _) => ValueTask.FromResult<string?>($"Thirty days of runs of {string.Join(", ", Suites)}: their duration in seconds and their failed tests."),
        };
    }

    // Thirty days of runs of four suites, one a day each: a duration that waves around the usual one, a Desktop suite that gets slower in the
    // last days, and a few failed tests.
    private static IReadOnlyList<TestRun> Runs()
    {
        var usual = new Dictionary<string, double>(StringComparer.Ordinal) { ["Catalog"] = 38, ["Orchestration"] = 61, ["Desktop"] = 176, ["Frontend"] = 128 };
        var runs = new List<TestRun>(30 * Suites.Length);
        var first = new DateOnly(2026, 10, 14);
        for (var day = 0; day < 30; day++)
        {
            for (var index = 0; index < Suites.Length; index++)
            {
                var suite = Suites[index];
                var wave = Math.Sin((day + index * 3) / 4.0) * 0.12 + Math.Cos(day * 1.7 + index) * 0.06;
                var slower = suite == "Desktop" && day > 18 ? 0.22 : 0;
                var failed = (day * 7 + index * 5) % 11 == 0 ? 1 + (day + index) % 3 : 0;
                runs.Add(new TestRun(first.AddDays(day).ToString("yyyy-MM-dd"), suite, (int)Math.Round(usual[suite] * (1 + wave + slower)), failed));
            }
        }

        return runs;
    }

    /// <summary>The request of `runs.get`: nothing to say.</summary>
    public sealed record GetRuns;

    /// <summary>A run of a suite: its day (`yyyy-MM-dd`), its suite, how long it took and how many of its tests failed.</summary>
    public sealed record TestRun(string Date, string Suite, int Seconds, int Failed);
}
