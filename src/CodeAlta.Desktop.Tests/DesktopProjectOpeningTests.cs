using CodeAlta.Desktop;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopProjectOpeningTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"Q:\code" : "/code";
    private static string Folder(string name) => Path.Combine(Root, name);

    private static bool TryParse(string[] args, out DesktopLaunchOptions? options, out string? error)
        => DesktopCommandLine.TryParse(args, path => path.StartsWith(Root, StringComparison.Ordinal), _ => false, out options, out error);

    [TestMethod]
    public void AltaWithFolders_AddsThemAsProjects_AndStartsOnTheFirstOne()
    {
        Assert.IsTrue(TryParse([Folder("a"), Folder("b"), Folder("a") + Path.DirectorySeparatorChar, "--space", "work", "--space", "home"], out var options, out var error), error);
        CollectionAssert.AreEqual(new[] { Folder("a"), Folder("b") }, options!.Projects.ToArray(), "Each folder once, as a full path.");
        CollectionAssert.AreEqual(new[] { "work", "home" }, options.Spaces.ToArray());
        Assert.AreEqual(Folder("a"), options.Owned!.Project);
        Assert.IsFalse(options.Developer);

        Assert.IsTrue(TryParse(["--dev", "--", Folder("-dash")], out options, out error), error);
        Assert.IsTrue(options!.Developer);
        CollectionAssert.AreEqual(new[] { Folder("-dash") }, options.Projects.ToArray());

        // A relative folder is read from the folder the start runs in.
        Assert.IsTrue(DesktopCommandLine.TryParse(["."], _ => true, _ => false, out options, out error), error);
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(".")), options!.Projects.Single());

        // Without folders, a start is what it was.
        Assert.IsTrue(TryParse([], out options, out _));
        Assert.AreEqual(0, options!.Projects.Count);
        Assert.AreEqual(Path.GetFullPath(Environment.CurrentDirectory), options.Owned!.Project);
    }

    [TestMethod]
    public void AltaWithFolders_RefusesAFolderThatDoesNotExist_AndASpaceWithoutFolder()
    {
        Assert.IsFalse(TryParse([Path.Combine(OperatingSystem.IsWindows() ? @"Z:\" : "/elsewhere", "missing")], out _, out var error));
        StringAssert.Contains(error, "does not exist");
        Assert.IsFalse(TryParse(["--space", "work"], out _, out error));
        StringAssert.Contains(error, "--space");
        Assert.IsFalse(TryParse([Folder("a"), "--space"], out _, out _));
        Assert.IsFalse(TryParse([Folder("a"), "--exit"], out _, out _));
        Assert.IsFalse(TryParse([Folder("a"), "--unknown"], out _, out _));
        Assert.IsFalse(TryParse([.. Enumerable.Range(0, DesktopCommandLine.MaximumProjects + 1).Select(index => Folder($"p{index}"))], out _, out error));
        StringAssert.Contains(error, "at most");
    }

    [TestMethod]
    public void TheFolders_ArePassedOnToTheApplicationOfATerminalStart_AndToTheRunningOne()
    {
        Assert.IsTrue(TryParse(["--dev", Folder("a"), "--space", "work"], out var options, out _));
        var handedOver = DesktopTerminalStart.Arguments(options!);
        CollectionAssert.AreEqual(new[] { "--dev", "--space", "work", "--", Folder("a") }, handedOver.ToArray());
        Assert.IsTrue(TryParse([.. handedOver], out var again, out _));
        CollectionAssert.AreEqual(options!.Projects.ToArray(), again!.Projects.ToArray());
        CollectionAssert.AreEqual(options.Spaces.ToArray(), again.Spaces.ToArray());

        var forwarded = DesktopProjectOpening.Arguments([Folder("a"), Folder("b")], ["work"]);
        Assert.IsTrue(DesktopProjectOpening.TryRead(forwarded, out var folders, out var spaces));
        CollectionAssert.AreEqual(new[] { Folder("a"), Folder("b") }, folders.ToArray());
        CollectionAssert.AreEqual(new[] { "work" }, spaces.ToArray());
        // The arguments of any other start are not folders.
        Assert.IsFalse(DesktopProjectOpening.TryRead([DesktopCommandLine.ExitOption], out _, out _));
        Assert.IsFalse(DesktopProjectOpening.TryRead([], out _, out _));
        Assert.IsFalse(DesktopProjectOpening.TryRead([DesktopProjectOpening.ProjectArgument, "relative"], out _, out _));
        Assert.IsFalse(DesktopProjectOpening.TryRead([DesktopCommandLine.SpaceOption, "work"], out _, out _));
    }

    [TestMethod]
    public async Task TheFoldersRequested_AreAddedOnceTheCommandsRun_InOrder_AndTheFirstOneIsShown()
    {
        var shown = new List<(string Project, string? Space)>();
        var warnings = new List<string>();
        var commands = new List<string>();
        var opening = new DesktopProjectOpening((project, space) => shown.Add((project, space)), warnings.Add);
        opening.Request([Folder("a"), Folder("missing"), Folder("b")], ["work"]);
        opening.Request([Folder("c")], []);
        Assert.AreEqual(0, shown.Count, "Nothing runs before the commands can.");

        opening.Attach(arguments =>
        {
            commands.Add(string.Join(' ', arguments));
            var folder = arguments[2];
            return Task.FromResult(folder == Folder("missing")
                ? new AltaCommandResult { Duration = TimeSpan.Zero, CorrelationId = "c", ExitCode = 2, Stdout = string.Empty, Stderr = "{\"type\":\"alta.error\",\"message\":\"Project path does not exist.\"}" }
                : new AltaCommandResult
                {
                    Duration = TimeSpan.Zero, CorrelationId = "c",
                    ExitCode = 0,
                    Stdout = "{\"type\":\"alta.result\"}\n{\"type\":\"alta.project.added\",\"projectId\":\"id-" + Path.GetFileName(folder) + "\",\"spaces\":[\"space-work\"]}",
                    Stderr = string.Empty,
                });
        });
        await opening.Completion;

        CollectionAssert.AreEqual(new[]
        {
            $"project add {Folder("a")} --space work", $"project add {Folder("missing")} --space work", $"project add {Folder("b")} --space work", $"project add {Folder("c")}",
        }, commands);
        CollectionAssert.AreEqual(new[] { ("id-a", (string?)"space-work"), ("id-c", (string?)null) }, shown);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "missing");
    }
}
