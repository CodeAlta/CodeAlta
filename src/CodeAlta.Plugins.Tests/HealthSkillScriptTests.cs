using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugins.Tests;

/// <summary>
/// The report script of the built-in skill <c>improve-codebase-health</c>: it builds the example of the skill, and
/// refuses a report that breaks the contract. It is here, with the plugin builds, because it compiles a C# file with the
/// SDK as they do.
/// </summary>
[TestClass]
public sealed class HealthSkillScriptTests
{
    private static readonly string SkillRoot = Path.Combine(PluginTestPaths.SourceRoot, "CodeAlta.Catalog", "BuiltinSkills", "improve-codebase-health");

    [TestMethod]
    public async Task TheReportScript_BuildsTheExample_AndRefusesABrokenReport()
    {
        using var temp = new TestTempDirectory();
        var example = File.ReadAllText(Path.Combine(SkillRoot, "assets", "example", "report.json"));
        var reportPath = Path.Combine(temp.Path, "report.json");
        File.WriteAllText(reportPath, example);

        var built = await RunScriptAsync("build_report.cs", temp.Path);

        Assert.AreEqual(0, built.ExitCode, built.Output);
        var html = File.ReadAllText(Path.Combine(temp.Path, "index.html"));
        Assert.IsFalse(html.Contains("__REPORT_DATA__", StringComparison.Ordinal), "The data replaces the marker of the template.");
        StringAssert.Contains(html, "Health review of Parcel");
        StringAssert.Contains(File.ReadAllText(Path.Combine(temp.Path, "report.md")), "#### F01 ");

        // A speculative finding in the order of work is one of the rules the script holds a report to.
        var broken = JsonNode.Parse(example)!;
        broken["roadmap"]![0]!["items"]!.AsArray().Add(new JsonObject { ["finding"] = "F06" });
        File.WriteAllText(reportPath, broken.ToJsonString());

        var refused = await RunScriptAsync("build_report.cs", temp.Path, "--check");

        Assert.AreEqual(1, refused.ExitCode, refused.Output);
        StringAssert.Contains(refused.Output, "F06 is speculative");

        // A review that keeps no finding is a valid report.
        var empty = JsonNode.Parse(example)!.AsObject();
        empty["findings"] = new JsonArray();
        empty.Remove("themes");
        empty.Remove("roadmap");
        File.WriteAllText(reportPath, empty.ToJsonString());

        var nothingKept = await RunScriptAsync("build_report.cs", temp.Path, "--check");

        Assert.AreEqual(0, nothingKept.ExitCode, nothingKept.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string script, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            // As the skill says: from the folder of the scripts, so that no global.json of a reviewed project applies.
            WorkingDirectory = Path.Combine(SkillRoot, "scripts"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        PluginBuildService.RemoveInheritedSdk(info.Environment);
        // A build node that stayed for the next build would keep the pipes of this one open.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet did not start.");
        // The first run compiles the script: a deadline for a machine under load, not a wait.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output + await error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"`dotnet run {script}` did not end within five minutes.");
            throw;
        }
    }
}
