using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ContextSelectionTests
{
    [TestMethod]
    public void Selection_RequiresExactVerifiedCapacity_DefaultAlwaysPasses()
    {
        var selected = new OwnedSessionSelection("provider", "default", "model", null);
        var choices = new OwnedSelectionChoices(selected, [new("default", "Default")],
            [new("model", "Model", []) { ContextSizes = new(200000, [200000, 1000000]) }, new("other", "Other", [])]);
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selected with { Context = new(null) }));
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selected with { Context = new(1000000) }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selected with { Context = new(500000) }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selected with { ModelId = "other", Context = new(1000000) }));
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selected with { ModelId = "other", Context = new(null) }));
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selected with { ModelId = null, Context = new(1000000) }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selected with { ModelId = null, Context = new(500000) }));
    }

    [TestMethod]
    public void Policy_KeepsExactIdentity_ResetsOnModelOrProviderChange()
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "session", Kind = SessionViewKind.GlobalSession, ProviderId = "provider", ProviderKey = "provider",
            ModelId = "model", ContextWindowTokens = 1000000, WorkingDirectory = Path.GetTempPath(),
        };
        var same = SessionExecutionPolicy.CaptureSession(session, null, Path.GetTempPath(), default, "model", null, null);
        Assert.AreEqual(1000000L, same.Context?.Tokens);
        var model = SessionExecutionPolicy.CaptureSession(session, null, Path.GetTempPath(), default, "Model", null, null);
        Assert.IsNotNull(model.Context);
        Assert.IsNull(model.Context.Tokens);
        var provider = SessionExecutionPolicy.CaptureSession(session, null, Path.GetTempPath(), new("other"), "model", null, null);
        Assert.IsNull(provider.Context?.Tokens);
    }
}
