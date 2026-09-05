using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderPreferencePortTests
{
    [TestMethod]
    public void RememberSessionPreference_NormalizesBlankModelAndForwardsPersistFlag()
    {
        string? capturedSessionId = null;
        ModelProviderPreference? capturedPreference = null;
        bool? capturedPersist = null;
        var port = CreatePort(rememberSessionPreference: (sessionId, preference, persist) =>
        {
            capturedSessionId = sessionId;
            capturedPreference = preference;
            capturedPersist = persist;
        });

        port.RememberSessionPreference(
            "session-1",
            new ModelProviderPreference(new ModelProviderId("provider-1"), "   ", AgentReasoningEffort.Medium),
            persistNow: true);

        Assert.AreEqual("session-1", capturedSessionId);
        Assert.IsNotNull(capturedPreference);
        Assert.AreEqual("provider-1", capturedPreference.ModelProviderId.Value);
        Assert.IsNull(capturedPreference.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, capturedPreference.ReasoningEffort);
        Assert.IsTrue(capturedPersist);
    }

    [TestMethod]
    public void IsModelProviderReady_ForwardsValidatedModelProviderId()
    {
        var captured = default(ModelProviderId);
        var port = CreatePort(isModelProviderReady: modelProviderId =>
        {
            captured = modelProviderId;
            return true;
        });

        var isReady = port.IsModelProviderReady(new ModelProviderId("provider-1"));

        Assert.IsTrue(isReady);
        Assert.AreEqual("provider-1", captured.Value);
    }

    [TestMethod]
    public void GetPreferredModelProviderId_RejectsDefaultProjectId()
    {
        var port = CreatePort();

        Assert.ThrowsExactly<ArgumentException>(() => port.GetPreferredModelProviderId(default));
    }

    [TestMethod]
    public void ApplyDraftPreference_RequiresPreferenceModelProviderId()
    {
        var port = CreatePort();
        var binding = new PromptSessionBinding(
            new PromptSessionId("prompt-1"),
            ProjectId.NewVersion7(),
            new ShellSessionRef.Draft(new SessionDraftId("draft-1")),
            new ModelProviderId("provider-1"));

        Assert.ThrowsExactly<ArgumentException>(() => port.ApplyDraftPreference(binding, new ModelProviderPreference(default)));
    }

    private static DelegatingModelProviderPreferencePort CreatePort(
        Func<ProjectId, ModelProviderId>? getPreferredModelProviderId = null,
        Func<ModelProviderId, bool>? isModelProviderReady = null,
        Action<PromptSessionBinding, ModelProviderPreference>? applyDraftPreference = null,
        Action<string, ModelProviderPreference>? applySessionPreference = null,
        Action<ProjectId, ModelProviderPreference>? rememberProjectPreference = null,
        Action<string, ModelProviderPreference, bool>? rememberSessionPreference = null)
        => new(
            getPreferredModelProviderId ?? (_ => new ModelProviderId("provider-1")),
            isModelProviderReady ?? (_ => false),
            applyDraftPreference ?? ((_, _) => { }),
            applySessionPreference ?? ((_, _) => { }),
            rememberProjectPreference ?? ((_, _) => { }),
            rememberSessionPreference ?? ((_, _, _) => { }));
}
