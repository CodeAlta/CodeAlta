using System.Text.Json;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>What a plugin said of a Send it answered itself travels on the receipt, within the bounds of the page.</summary>
[TestClass]
public sealed class SessionReceiptMessageTests
{
    [TestMethod]
    public void AMessageTakesThePlaceOfARunIdOnTheWire()
    {
        var escaped = new string('\u0001', 256);
        var code = new string('\u0001', 64);
        var id = "abcdefab-1234-5678-9abc-abcdefabcdef";
        var handled = new SessionReceiptView(escaped, escaped, id, null, "Send", "terminal", "Completed", code, null) { Message = escaped };

        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [handled], null).Status);
        Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", [handled with { RunId = "run" }], null).Status);
        Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", [handled with { Message = escaped + "x" }], null).Status);
        Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", [handled with { Message = " padded" }], null).Status);

        // The largest row with a message is within the bound of a row with a run id.
        var page = SessionOperationsService.ProjectPage(code, Enumerable.Repeat(handled, 64).ToArray(), 64);
        Assert.AreEqual("ok", page.Status);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.SessionReceiptPage).Length + 8192 <= 448 * 1024);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(handled, DesktopJsonContext.Default.SessionReceiptView).Length <= 6400);
        // A row without a message leaves it out.
        var json = JsonSerializer.Serialize(handled with { Message = null }, DesktopJsonContext.Default.SessionReceiptView);
        Assert.IsFalse(json.Contains("\"message\"", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void APluginMessageIsTrimmedCutAndMadeWellFormed()
    {
        Assert.AreEqual("Done.", SessionOperationsService.ReceiptMessage("  Done.  "));
        Assert.IsNull(SessionOperationsService.ReceiptMessage("   "));
        Assert.AreEqual("a�b", SessionOperationsService.ReceiptMessage("a\uD800b"));

        var cut = SessionOperationsService.ReceiptMessage(new string('x', 254) + "😀" + "tail");
        Assert.IsNotNull(cut);
        Assert.IsTrue(cut.Length <= 256);
        Assert.IsTrue(cut.EndsWith('…'));
        Assert.IsFalse(char.IsHighSurrogate(cut[^2]), "A pair is never split.");
    }
}
