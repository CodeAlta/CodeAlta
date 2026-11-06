using CodeAlta.Browser.Drivers.BiDi;
using WebDriverBiDi;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class BiDiStartTests
{
    [TestMethod]
    public async Task AStartWhoseSessionTimesOut_IsEndedAndStartedAgain_AndTheLastStartWaitsAsLongAsAnyCommand()
    {
        var started = 0;
        var ended = new List<int>();
        var patiences = new List<TimeSpan?>();
        var connection = await BiDiBrowserConnection.StartAgainWhenStalledAsync(
            () => Task.FromResult(++started),
            (start, patience) =>
            {
                patiences.Add(patience);
                return start < 3 ? Task.FromException<string>(new WebDriverBiDiTimeoutException("Timed out executing command session.new")) : Task.FromResult($"connection {start}");
            },
            start =>
            {
                ended.Add(start);
                return ValueTask.CompletedTask;
            },
            attempts: 3, patience: TimeSpan.FromSeconds(30));
        Assert.AreEqual("connection 3", connection);
        CollectionAssert.AreEqual(new[] { 1, 2 }, ended, "each start that stalled is ended before the next");
        CollectionAssert.AreEqual(new TimeSpan?[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), null }, patiences);
    }

    [TestMethod]
    public async Task TheLastStartThatTimesOut_FailsTheStart_AndAFailureOtherThanATimeoutIsNotTriedAgain()
    {
        var ended = 0;
        var starts = 0;
        await Assert.ThrowsExactlyAsync<WebDriverBiDiTimeoutException>(() => BiDiBrowserConnection.StartAgainWhenStalledAsync<int, string>(
            () => Task.FromResult(++starts), (_, _) => throw new WebDriverBiDiTimeoutException("stalled"), _ => { ended++; return ValueTask.CompletedTask; }, 2, TimeSpan.FromSeconds(1)));
        Assert.AreEqual(2, starts);
        Assert.AreEqual(2, ended, "the last start is ended too");

        starts = ended = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => BiDiBrowserConnection.StartAgainWhenStalledAsync<int, string>(
            () => Task.FromResult(++starts), (_, _) => throw new InvalidOperationException("refused"), _ => { ended++; return ValueTask.CompletedTask; }, 3, TimeSpan.FromSeconds(1)));
        Assert.AreEqual(1, starts);
        Assert.AreEqual(1, ended);
    }
}
