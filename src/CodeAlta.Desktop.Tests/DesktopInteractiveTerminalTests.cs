using CodeAlta.Agent;
using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The terminal a program that refuses to run without one gets (<c>claude --cloud</c>): it runs with no tab, wide, its
/// tab is shown when it asks something or stays still, it is gone once it did its work, and kept after a failure.
/// </summary>
[TestClass]
public sealed class DesktopInteractiveTerminalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("codealta-interactive-");

        public Fixture()
        {
            var profiles = new TerminalProfiles(static _ => null, static _ => false, static () => [], static _ => [], windows: false, mac: false);
            Terminals = new DesktopTerminals("1.2.3", profiles, start =>
            {
                Starts.Add(start);
                var program = new FakeTerminalProgram();
                Programs.Add(program);
                Started.TrySetResult(program);
                return program;
            }, TimeProvider.System);
        }

        public DesktopTerminals Terminals { get; }
        public List<FakeTerminalProgram> Programs { get; } = [];
        public List<PseudoTerminalStart> Starts { get; } = [];
        public TaskCompletionSource<FakeTerminalProgram> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Folder => _folder.FullName;

        public AgentInteractiveProgram Program()
            => new("/bin/claude", ["--cloud=Fix it"], Folder, new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "/accounts/max", ["CLAUDECODE"] = null }, "Claude Code: Repo");

        public void Dispose()
        {
            Terminals.CloseAsync().GetAwaiter().GetResult();
            _folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task AProgramThatDoesItsWork_RunsWideWithNoTab_AndItsTerminalIsGone()
    {
        using var fixture = new Fixture();
        var shown = 0;
        var run = new DesktopInteractiveTerminal(fixture.Terminals, "project").RunAsync(fixture.Program(), static _ => false, () => shown++, CancellationToken.None);
        var program = await fixture.Started.Task.WaitAsync(Patience);
        program.Show("Created cloud session: Fix it\r\nView: https://claude.ai/code/session_01ABCDEFGHJK\r\n");
        program.End(0);
        var outcome = await run.WaitAsync(Patience);

        Assert.AreEqual((0, false, (string?)null, false), (outcome.ExitCode, outcome.Canceled, outcome.TerminalId, outcome.Shown));
        Assert.IsTrue(outcome.Rows.Any(static row => row.StartsWith("Created cloud session: Fix it", StringComparison.Ordinal)));
        Assert.AreEqual(0, shown);
        var start = fixture.Starts.Single();
        Assert.AreEqual(("/bin/claude", DesktopInteractiveTerminal.Columns), (start.FileName, start.Columns), "The program as it is, on a screen wide enough for an address.");
        CollectionAssert.AreEqual(new[] { "--cloud=Fix it" }, start.Arguments.ToArray());
        Assert.AreEqual("/accounts/max", start.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.IsFalse(start.Environment.ContainsKey("CLAUDECODE"), "A variable the program is given as null is removed.");
        await Until(() => fixture.Terminals.List().Count == 0, "The terminal that did its work goes away");
    }

    [TestMethod]
    public async Task AProgramThatAsks_HasItsTabShown_AndOneThatFails_IsKeptToBeLookedAt()
    {
        using var fixture = new Fixture();
        var shown = 0;
        var run = new DesktopInteractiveTerminal(fixture.Terminals, "project").RunAsync(fixture.Program(),
            static rows => rows.Any(static row => row.Contains("Enter to confirm", StringComparison.Ordinal)), () => shown++, CancellationToken.None);
        var program = await fixture.Started.Task.WaitAsync(Patience);
        program.Show("Do you trust this folder?\r\n Enter to confirm\r\n");
        await Until(() => shown == 1, "The tab is shown when the program asks");
        program.Show("Error: Cloud sessions need a claude.ai sign-in.\r\n");
        program.End(1);
        var outcome = await run.WaitAsync(Patience);

        Assert.AreEqual((1, true), (outcome.ExitCode, outcome.Shown));
        Assert.AreEqual(1, shown, "Shown once.");
        Assert.IsNotNull(outcome.TerminalId);
        Assert.AreEqual(outcome.TerminalId, fixture.Terminals.List().Single().Id, "Kept after a failure, with its screen.");
    }

    [TestMethod]
    public async Task AProgramThatStaysStill_HasItsTabShown_AndACanceledOneIsEnded()
    {
        using var fixture = new Fixture();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var run = new DesktopInteractiveTerminal(fixture.Terminals, "project", quietBeforeShown: TimeSpan.FromMilliseconds(200))
            .RunAsync(fixture.Program(), static _ => false, () => shown.TrySetResult(), cancel.Token);
        var program = await fixture.Started.Task.WaitAsync(Patience);
        program.Show("Waiting…\r\n");
        await shown.Task.WaitAsync(Patience);

        cancel.Cancel();
        var outcome = await run.WaitAsync(Patience);
        Assert.IsTrue(outcome.Canceled);
        Assert.IsNull(outcome.ExitCode);
        Assert.IsNull(outcome.TerminalId, "A canceled program is ended, and its terminal with it.");
        Assert.IsTrue(program.Killed || program.Exited.IsCompleted);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail(what + " did not happen.");
            await Task.Delay(10);
        }
    }
}
