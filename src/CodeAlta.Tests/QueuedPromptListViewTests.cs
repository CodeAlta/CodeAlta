using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class QueuedPromptListViewTests
{
    [TestMethod]
    public void QueuedPromptCountState_DoesNotNotifyDuringConstruction()
    {
        var changeCount = 0;
        var lastValue = 0;

        var state = new QueuedPromptCountState(
            3,
            value =>
            {
                changeCount++;
                lastValue = value;
            });

        Assert.AreEqual(0, changeCount);
        Assert.AreEqual(0, lastValue);
        Assert.AreEqual(3, state.Value);
    }

    [TestMethod]
    public void QueuedPromptCountState_NotifiesAfterConstruction()
    {
        var changeCount = 0;
        var lastValue = 0;
        var state = new QueuedPromptCountState(
            2,
            value =>
            {
                changeCount++;
                lastValue = value;
            });

        state.Value = 4;

        Assert.AreEqual(1, changeCount);
        Assert.AreEqual(4, lastValue);
        Assert.AreEqual(4, state.Value);
    }
}
