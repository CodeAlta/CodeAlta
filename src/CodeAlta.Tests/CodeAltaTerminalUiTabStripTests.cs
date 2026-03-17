using System.Reflection;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaTerminalUiTabStripTests
{
    [TestMethod]
    public void ResolveOpenTabIndicatorKind_PrefersRunningAndMapsTone()
    {
        Assert.AreEqual(
            CodeAltaTerminalUi.OpenTabIndicatorKind.Running,
            CodeAltaTerminalUi.ResolveOpenTabIndicatorKind(isBusy: true, CodeAltaTerminalUi.StatusTone.Ready));
        Assert.AreEqual(
            CodeAltaTerminalUi.OpenTabIndicatorKind.Ready,
            CodeAltaTerminalUi.ResolveOpenTabIndicatorKind(isBusy: false, CodeAltaTerminalUi.StatusTone.Ready));
        Assert.AreEqual(
            CodeAltaTerminalUi.OpenTabIndicatorKind.Warning,
            CodeAltaTerminalUi.ResolveOpenTabIndicatorKind(isBusy: false, CodeAltaTerminalUi.StatusTone.Warning));
        Assert.AreEqual(
            CodeAltaTerminalUi.OpenTabIndicatorKind.Error,
            CodeAltaTerminalUi.ResolveOpenTabIndicatorKind(isBusy: false, CodeAltaTerminalUi.StatusTone.Error));
    }

    [TestMethod]
    public void CompactTabTitle_DoesNotChangeForSelectionState()
    {
        var method = typeof(CodeAltaTerminalUi).GetMethod("CompactTabTitle", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var title = (string?)method.Invoke(null, ["Review startup"]);

        Assert.AreEqual("Review startup", title);
    }
}
