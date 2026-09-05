using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellStatusServiceTests
{
    [TestMethod]
    public void SetStatus_ForwardsMessageSpinnerAndTone()
    {
        (string Message, bool Spinner, StatusTone Tone)? observed = null;
        var service = new DelegatingShellStatusService((message, showSpinner, tone) => observed = (message, showSpinner, tone));

        service.SetStatus("Working", showSpinner: true, StatusTone.Warning);

        Assert.IsNotNull(observed);
        Assert.AreEqual("Working", observed.Value.Message);
        Assert.IsTrue(observed.Value.Spinner);
        Assert.AreEqual(StatusTone.Warning, observed.Value.Tone);
    }
}
