using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Explicit checkout-only source checks; no host or provider construction.</summary>
[TestClass]
public sealed class DesktopAskSourceTests
{
    [TestMethod]
    public void CurrentWiring_IsOwnedOnlyAndRetainsRunEvidenceBeforeCleanup()
    {
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        StringAssert.Contains(app, "EnableOwnedAsks = true");
        StringAssert.Contains(app, "builder.AddSessionAsksService(asks)");
        var legacy = app[app.IndexOf("    private async ValueTask RunAsync(", StringComparison.Ordinal)..];
        Assert.IsFalse(legacy.Contains("AddSessionAsksService", StringComparison.Ordinal));
        var runtime = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs");
        var returned = runtime.IndexOf("askSubmission?.RecordRunReturned(runId)", StringComparison.Ordinal);
        Assert.IsTrue(returned > runtime.IndexOf("runId = await RunCapturedAsync", StringComparison.Ordinal));
        Assert.IsTrue(returned < runtime.IndexOf("await PublishRunSubmittedIfStillInFlightAsync", returned, StringComparison.Ordinal));
        var command = Read("CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs");
        StringAssert.Contains(command, "SameAskContext(previous.Ask, askSubmission)");
        StringAssert.Contains(command, "await Asks.DrainAsync()");
        var producer = Read("CodeAlta.Orchestration/Runtime/OwnedSessionAskTool.cs");
        Assert.IsFalse(producer.Contains("AltaCommandRegistry", StringComparison.Ordinal));
        Assert.IsFalse(producer.Contains("AltaCommandDispatcher", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Relocation_PreservesOriginalBodiesAndPublicForwarders()
    {
        var contracts = Read("CodeAlta.Orchestration/Runtime/Prompts/AltaAskContracts.cs");
        var metadata = Read("CodeAlta.LiveTool/AltaAskContracts.cs");
        var original = contracts.Replace("using System.Text;\n", "using System.Text;\nusing System.Text.Json.Serialization;\n", StringComparison.Ordinal)
            + metadata[metadata.IndexOf("[JsonSourceGenerationOptions(", StringComparison.Ordinal)..];
        Assert.AreEqual("7f7a2cb86fea0bcef4659d1bf35f438060460adf", OwnedSessionAskSourceInverse.GitObjectId(original));
        Assert.AreEqual("103a1cde2320123661713bd1ca5184883ef61c43", OwnedSessionAskSourceInverse.GitObjectId(
            Read("CodeAlta.Orchestration/Runtime/Prompts/AltaAskResponses.cs")));
        const string prefix = "namespace CodeAlta.LiveTool;\n\n";
        var caller = Read("CodeAlta.Orchestration/Runtime/Prompts/AltaCallerIdentity.cs");
        StringAssert.StartsWith(caller, prefix);
        var callerOriginal = Read("CodeAlta.LiveTool/AltaContracts.cs").Replace(prefix, prefix + caller[prefix.Length..] + "\n", StringComparison.Ordinal);
        Assert.AreEqual("35ce7a91859758fce6761094711ef51cba6f900c", OwnedSessionAskSourceInverse.GitObjectId(callerOriginal));
        var forwarders = Read("CodeAlta.LiveTool/Properties/AskTypeForwarders.cs");
        foreach (var name in new[] { "AltaCallerIdentity", "AltaAskRequest", "AltaAskQuestion", "AltaAskChoice", "AltaAskFreeform",
            "AltaAskFile", "AltaAskAnswer", "AltaAskFileReview", "AltaAskFileComment", "AltaQueuedAsk", "AltaAskQueueResult",
            "AltaAskRemovalResult", "IAltaAskService", "AltaAskQueueChangedEventArgs", "AltaAskService", "AltaAskValidator",
            "AltaAskAnswerMarkdownFormatter", "AltaAskResponseState", "AltaAskResponseHandle", "AltaAskResponseResult" })
            StringAssert.Contains(forwarders, "[assembly: TypeForwardedTo(typeof(" + name + "))]");
    }

    [TestMethod]
    public void WholeOriginals_RestoreBeforeHistoricalTransformations()
    {
        foreach (var path in OwnedSessionAskSourceInverse.Paths)
        {
            var restored = OwnedSessionAskSourceInverse.Restore(path, Read(path));
            Assert.AreEqual(OwnedSessionAskSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
        }
    }

    private static string Read(string path) => SourceTestText.DecodeSource(
        File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));

    [TestMethod]
    public void AskReads_ProcessEpochEvidenceBeforeObsoletePresentation()
    {
        var panel = Read("CodeAlta/frontend/src/AskPanel.tsx");
        var observer = panel[panel.IndexOf("const observer = original.then(value =>", StringComparison.Ordinal)..];
        observer = observer[..observer.IndexOf("}).catch(", StringComparison.Ordinal)];
        StringAssert.Contains(observer, "actions.readPage(value, epoch, sessionId,");
        StringAssert.Contains(observer, "() => !controller.signal.aborted");
        StringAssert.Contains(observer, "capability.observe({ status: \"stale_epoch\", epoch })");
        Assert.IsFalse(observer.Contains("if (controller.signal.aborted) return", StringComparison.Ordinal));
        var helper = Read("CodeAlta/frontend/src/sessionAsks.ts");
        var read = helper[helper.IndexOf("    readPage(", StringComparison.Ordinal)..];
        var identity = read.IndexOf("observeEpoch(value, epoch, invalidateCapability)", StringComparison.Ordinal);
        var fence = read.IndexOf("if (!isCurrent()) return undefined", StringComparison.Ordinal);
        Assert.IsTrue(identity >= 0 && fence > identity);
        Assert.IsTrue(read.IndexOf("return parseAskPage(value, epoch, session);", StringComparison.Ordinal) > fence);
        StringAssert.Contains(panel, "askWireHandle(request.action.handle)");
        StringAssert.Contains(panel, "}, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: \"stale_epoch\", epoch }); }");
    }
}
