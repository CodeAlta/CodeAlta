using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>What a plugin said of a Send or a Steer it answered itself travels on the receipt, within the bounds of the page.</summary>
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
    public void ASteerAPluginAnsweredCarriesItsCodeAndMessage()
    {
        var receipt = new OwnedSessionCommandReceipt("key", OwnedSessionCommandKind.Steer, "session");
        receipt.Complete(new(OwnedSessionCommandOutcome.Cancelled, Code: OwnedSessionCommandService.PluginCancelledCode) { Message = "Secrets are not sent." });
        OwnedTextSteerRequest? steered = null;
        var service = new SessionOperationsService("epoch", _ => throw new AssertFailedException("Unexpected send"),
            _ => throw new AssertFailedException("Unexpected abort"), request => { steered = request; return new(OwnedSessionCommandAdmissionKind.Accepted, receipt); });

        var admission = service.Steer(new("epoch", "key", "session", Guid.NewGuid().ToString("D"), "1", "run", "my password"), CancellationToken.None);

        Assert.AreEqual("accepted", admission.Status);
        Assert.AreEqual("my password", steered!.Text);
        Assert.AreEqual("Steer", admission.Receipt!.Kind);
        Assert.AreEqual("terminal", admission.Receipt.State);
        Assert.AreEqual("Cancelled", admission.Receipt.Outcome);
        Assert.AreEqual(OwnedSessionCommandService.PluginCancelledCode, admission.Receipt.Code);
        Assert.AreEqual("Secrets are not sent.", admission.Receipt.Message);
        Assert.IsNull(admission.Receipt.RunId);
    }

    [TestMethod]
    public void ASendOfTheWindow_HasNoProvenance_SoAQueuedPromptGoesThroughThePromptHooks()
    {
        // The composer keeps the prompts queued while a turn runs and sends each one as a Send of the window when the
        // session is idle: a prompt without provenance is one the user typed, which the prompt-submit hooks see.
        OwnedTextSendRequest? sent = null;
        var service = new SessionOperationsService("epoch", request => { sent = request; return new(OwnedSessionCommandAdmissionKind.Busy); },
            _ => throw new AssertFailedException("Unexpected abort"));

        Assert.AreEqual("busy", service.Send(new("epoch", "key", "session", "queued prompt"), CancellationToken.None).Status);
        Assert.AreEqual("queued prompt", sent!.Text);
        Assert.IsNull(sent.Provenance);
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
