using System.Diagnostics;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class VaultCommandRunnerTests
{
    // More than any pipe holds: the write blocks while the program does not read.
    private static readonly string LargeInput = new('x', 8 * 1024 * 1024);

    [TestMethod]
    public async Task A_caller_that_stops_ends_a_program_that_does_not_read_its_input()
    {
        using var vault = new FakeVault("vaulttool");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new VaultCommandRunner().RunAsync(vault.ProgramPath, ["sleep", "60"],
            new VaultCommandOptions { Input = LargeInput, Timeout = TimeSpan.FromMinutes(2) }, stop.Token));
        Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed, "the program is ended while its input is written");
        AssertEnded(vault);
    }

    // The program the runner started is no longer running.
    private static void AssertEnded(FakeVault vault)
    {
        var file = Path.Combine(vault.Bin, "fake-vault.pid");
        if (!File.Exists(file)) return;
        try
        {
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(file), System.Globalization.CultureInfo.InvariantCulture));
            var ended = process.WaitForExit(TimeSpan.FromSeconds(5));
            if (!ended) process.Kill();
            Assert.IsTrue(ended, "the program still runs");
        }
        catch (ArgumentException)
        {
            // It ended.
        }
    }

    [TestMethod]
    public async Task A_program_that_does_not_read_its_input_runs_out_of_time()
    {
        using var vault = new FakeVault("vaulttool");
        var watch = Stopwatch.StartNew();
        var result = await new VaultCommandRunner().RunAsync(vault.ProgramPath, ["sleep", "60"], new VaultCommandOptions { Input = LargeInput, Timeout = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Assert.IsTrue(result.TimedOut);
        Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
        AssertEnded(vault);
    }

    [TestMethod]
    public void A_failure_is_summed_up_without_what_the_program_wrote()
    {
        var result = new VaultCommandResult(2, [], "Error:\n  0: printed sk-secret-123\n", TimedOut: false);
        Assert.AreEqual("it ended with code 2", result.Summary());
        Assert.AreEqual("printed sk-secret-123", result.ErrorDetail());
        Assert.IsNull(new VaultCommandResult(1, [], "  \n", TimedOut: false).ErrorDetail());
    }
}
