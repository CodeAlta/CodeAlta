using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session whose run fails on its provider goes on with the next target of its tier: the real host, runtime and
/// command owner, with two scripted providers and a parent session.
/// </summary>
[TestClass]
public sealed class SessionFallbackTests
{
    // Each wait starts a host, a run or a fallback: a full solution run makes it several times slower than the second
    // it takes alone, so the bound tells only a hang (see TestDeadlines).
    private static readonly TimeSpan Timeout = TestDeadlines.Hang;

    private const string Tiers = """
        [models.tiers]
        standard = ["prov-a:model-a@high,medium", "prov-b:model-b@high"]

        [models.fallback]
        enabled = true
        """;

    [TestMethod]
    public async Task ExhaustedQuota_GoesOnWithTheNextProvider_AndTheParentIsNotToldOfTheReplacedError()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, DateTimeOffset.UtcNow.AddHours(2))));

        f.Send("do the work");
        var first = await f.NextSendAsync(f.A);
        Assert.AreEqual("do the work", first.Text);
        Assert.IsNull(first.Provenance, "The prompt of the user has no provenance.");
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        // The prompt that continues is the host's: the timelines name it, not "You".
        Assert.AreEqual(AgentPromptProvenance.ModelFallback, continuation.Provenance);
        // A run that stopped before any answer is answered, and nothing more; a tool call that was running is checked.
        StringAssert.Contains(continuation.Text, "If it stopped before any answer, answer the prompt before this message, and do nothing more.");
        StringAssert.Contains(continuation.Text, "check its result");
        Assert.AreEqual("model-b", continuation.Options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, continuation.Options.ReasoningEffort);
        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.ProviderFallback);
        StringAssert.Contains(card.Message, "quota reached until");
        StringAssert.Contains(card.Message, "continuing on PROV-B model-b high");
        var state = await f.ChildStateAsync();
        Assert.AreEqual("prov-b", state.ProviderKey);
        Assert.AreEqual("standard", state.Tier, "A model found in a tier makes the session run on that tier.");

        // The run that continues answers: the parent hears the answer, never the error that was replaced.
        var told = await f.WaitForParentAsync(static prompts => prompts.Count > 0);
        Assert.IsFalse(told.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)), string.Join("\n", told.Select(static p => p.Prompt)));
        Assert.AreEqual(1, f.Host.Commands.LastFallbacks.Count);
    }

    [TestMethod]
    public async Task SessionOfASpace_FallsBackAlongTheTiersOfItsSpace()
    {
        // The global tier has A only; the space lists B after it. Its file sets no fallback, as in 1.9.0: the global one is on.
        await using var f = await Fixture.CreateAsync("""
            [models.tiers]
            standard = ["prov-a:model-a@high"]

            [models.fallback]
            enabled = true
            """, childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            Assert.AreEqual(SpaceChangeStatus.Ok, (await spaces.UpdateSettingsAsync(created.Id, _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers))))).Status);
            return created.Id;
        });
        var scope = await f.Host.RuntimeService.LoadModelTierScopeAsync(f.Child.SpaceId, f.Child.ProjectRef);
        Assert.IsTrue(scope.FromSpace, $"space {f.Child.SpaceId} {scope.SpaceId} {scope.Tiers.Standard.Count}");
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-b", continuation.Options.Model);
    }

    [TestMethod]
    public async Task SpaceWithItsOwnFallback_FallsBackToItsOwnProvider_WhenTheGlobalFallbackIsOff()
    {
        // The global fallback is off; Business turns its own on, and its tier goes on with a provider of Business alone.
        await using var f = await Fixture.CreateAsync(Tiers.Replace("enabled = true", "enabled = false", StringComparison.Ordinal), childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            var on = new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: false);
            Assert.AreEqual(SpaceChangeStatus.Ok, (await spaces.UpdateSettingsAsync(created.Id,
                _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers), on)))).Status);
            return created.Id;
        }, providerSpaces: "[providers.prov-b]\ntype = \"openai-chat\"\napi_url = \"http://127.0.0.1:9/v1\"\napi_key = \"fixture\"\nspaces = [\"business\"]\n");
        var scope = await f.Host.RuntimeService.LoadModelTierScopeAsync(f.Child.SpaceId, f.Child.ProjectRef);
        Assert.IsTrue(scope.FallbackFromSpace);
        Assert.IsTrue(scope.IsProviderAvailable("prov-b"));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-b", continuation.Options.Model);
        Assert.AreEqual("prov-b", (await f.ChildStateAsync()).ProviderKey);
    }

    [TestMethod]
    public async Task SpaceWithItsOwnFallbackOff_LetsTheFailureThrough_WhenTheGlobalFallbackIsOn()
    {
        await using var f = await Fixture.CreateAsync(Tiers, childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            await spaces.UpdateSettingsAsync(created.Id, _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers), ModelFallbackSettings.Default)));
            return created.Id;
        });
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(0, f.B.SendCount);
        Assert.AreEqual(0, f.Host.Commands.LastFallbacks.Count);
    }

    [TestMethod]
    public async Task SessionOfASpace_NeverFallsBackToAProviderTheSpaceDoesNotHave()
    {
        // B is in the tier, but the space of the session has not got it: the failure goes through.
        await using var f = await Fixture.CreateAsync(Tiers, childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            await spaces.UpdateSettingsAsync(created.Id, _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers))));
            return created.Id;
        }, providerSpaces: "[providers.prov-b]\ntype = \"openai-chat\"\nenabled = false\nspaces = [\"personal\"]\n");
        var scope = await f.Host.RuntimeService.LoadModelTierScopeAsync(f.Child.SpaceId, f.Child.ProjectRef);
        Assert.IsTrue(scope.FromSpace);
        Assert.IsTrue(scope.IsProviderAvailable("prov-a"));
        Assert.IsFalse(scope.IsProviderAvailable("prov-b"));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task UnavailableProvider_TriesALowerReasoningOfTheSameModelFirst()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.Unavailable)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.A);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-a", continuation.Options.Model);
        Assert.AreEqual(AgentReasoningEffort.Medium, continuation.Options.ReasoningEffort);
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task ModelTheAccountCannotUse_GoesOnWithAnotherModelOfTheSameProvider_NotTheSameModelAtLowerReasoning()
    {
        await using var f = await Fixture.CreateAsync("""
            [models.tiers]
            standard = ["prov-a:model-a@high,medium", "prov-a:model-c@high", "prov-b:model-b@high"]

            [models.fallback]
            enabled = true
            """);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.ModelUnavailable)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.A);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-c", continuation.Options.Model, "The provider is not held: another of its models is taken.");
        Assert.AreEqual(AgentReasoningEffort.High, continuation.Options.ReasoningEffort);
        Assert.AreEqual(0, f.B.SendCount);
        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.ProviderFallback);
        StringAssert.Contains(card.Message, "has no access to model-a");
    }

    [TestMethod]
    public async Task AuthFailure_DoesNotSwitch_AndTheParentIsTold()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.Auth)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var told = await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(1, told.Count);
        Assert.AreEqual(0, f.B.SendCount);
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
        Assert.AreEqual(0, f.Host.Commands.LastFallbacks.Count);
    }

    [TestMethod]
    public async Task FallbackThatIsOff_LetsAnExhaustedQuotaThrough()
    {
        await using var f = await Fixture.CreateAsync(Tiers.Replace("enabled = true", "enabled = false", StringComparison.Ordinal));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task ExhaustedChain_LetsTheFailureThroughWithACardThatSaysWhatToDo()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        var told = await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(1, told.Count(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)),
            "Only the last failure is told: the first one was replaced.");
        Assert.IsFalse((card.Message ?? string.Empty).Contains("Resumes", StringComparison.Ordinal), "The resume is off unless the settings turn it on.");
        Assert.AreEqual(0, f.Host.Commands.PendingResumes.Count);
    }

    private const string ResumingTiers = """
        [models.tiers]
        standard = ["prov-a:model-a@high", "prov-b:model-b@high"]

        [models.fallback]
        enabled = true
        resume = true
        """;

    [TestMethod]
    public async Task ExhaustedTier_ResumesAtTheEarliestReset_AndTheParentHearsTheAnswer()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        var start = clock.GetUtcNow();
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(2))));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(1))));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);

        // The earliest reset of the tier is B's: the card says when, and with which provider.
        var resume = await f.WaitForResumeAsync();
        Assert.AreEqual("prov-b", resume.ProviderKey);
        Assert.AreEqual(start.AddHours(1) + SessionFallbackCoordinator.ResumeMargin, resume.ResumeAt);
        Assert.AreEqual(1, resume.Attempt);
        var card = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.Warning && (card.Message ?? string.Empty).Contains("Resumes", StringComparison.Ordinal), "resume");
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over. Resumes ");
        StringAssert.Contains(card.Message, " with PROV-B.");
        Assert.AreEqual(resume.ResumeId, card.Details!.Value.GetProperty("resumeId").GetString());
        Assert.AreEqual("scheduled", card.Details!.Value.GetProperty("resume").GetString());
        await clock.WaitForTimersAsync(1);

        // Before its time, nothing runs.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.AreEqual(1, f.B.SendCount);

        clock.Advance(TimeSpan.FromMinutes(31));
        var continuation = await f.NextSendAsync(f.B);
        StringAssert.Contains(continuation.Text, "[CodeAlta resume]");
        StringAssert.Contains(continuation.Text, "The limit has reset");
        StringAssert.Contains(continuation.Text, "check its result");
        Assert.AreEqual(AgentPromptProvenance.ModelFallback, continuation.Provenance);
        var resumed = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.ProviderFallback && (card.Message ?? string.Empty).Contains("has reset", StringComparison.Ordinal), "resumed");
        StringAssert.Contains(resumed.Message, "The limit of PROV-B has reset: resuming on PROV-B model-b high.");
        Assert.AreEqual("automatic", resumed.Details!.Value.GetProperty("resume").GetString());
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));

        // The parent waited as for a fallback: it hears the answer of the resumed run, never the failure.
        var told = await f.WaitForParentAsync(static prompts => prompts.Count > 0);
        Assert.IsFalse(told.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)), string.Join("\n", told.Select(static p => p.Prompt)));
    }

    [TestMethod]
    public async Task LimitReachedAgain_SchedulesTheNextResume_ThenTheTierIsTakenInOrder()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        var start = clock.GetUtcNow();
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(2))));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(1))));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(5))));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        var first = await f.WaitForResumeAsync();
        await clock.WaitForTimersAsync(1);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await f.NextSendAsync(f.B);

        // B reached its limit again: the next reset of the tier is A's, the second resume in a row.
        var second = await f.WaitForResumeAsync(pending => pending.ResumeId != first.ResumeId);
        Assert.AreEqual(("prov-a", 2), (second.ProviderKey, second.Attempt));
        await clock.WaitForTimersAsync(1);
        clock.Advance(TimeSpan.FromHours(1));

        // A comes first in the tier: the session goes back to it.
        var continuation = await f.NextSendAsync(f.A);
        StringAssert.Contains(continuation.Text, "[CodeAlta resume]");
        Assert.AreEqual("model-a", continuation.Options.Model);
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
        var told = await f.WaitForParentAsync(static prompts => prompts.Count > 0);
        Assert.IsFalse(told.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancelledResume_TellsTheParentOfTheFailure_AndNothingRunsLater()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        var resume = await f.WaitForResumeAsync();
        // Without a reset time, a provider is held 15 minutes.
        Assert.AreEqual(clock.GetUtcNow() + TimeSpan.FromMinutes(15) + SessionFallbackCoordinator.ResumeMargin, resume.ResumeAt);
        await clock.WaitForTimersAsync(1);

        Assert.AreEqual("unknown", await f.Host.Commands.CancelResumeAsync(f.Child.SessionId, "another-resume"));
        Assert.AreEqual("ok", await f.Host.Commands.CancelResumeAsync(f.Child.SessionId, resume.ResumeId));
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));
        Assert.AreEqual("unknown", f.Host.Commands.ResumeNow(f.Child.SessionId, resume.ResumeId));
        var card = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.Info, "cancelled");
        Assert.AreEqual("Automatic resume cancelled.", card.Message);
        await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, clock.PendingTimerCount);
        Assert.AreEqual((1, 1), (f.A.SendCount, f.B.SendCount));
    }

    [TestMethod]
    public async Task ResumeNow_RunsTheTierInOrderAtOnce_WhateverTheKnownLimits()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        var start = clock.GetUtcNow();
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(3))));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(3))));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        var resume = await f.WaitForResumeAsync();

        Assert.AreEqual("ok", f.Host.Commands.ResumeNow(f.Child.SessionId, resume.ResumeId));
        var continuation = await f.NextSendAsync(f.A);
        StringAssert.Contains(continuation.Text, "The user asked CodeAlta to resume it");
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
        var resumed = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.ProviderFallback && (card.Message ?? string.Empty).Contains("as asked", StringComparison.Ordinal), "resumed");
        Assert.AreEqual("manual", resumed.Details!.Value.GetProperty("resume").GetString());
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));
    }

    [TestMethod]
    public async Task ResumesInARow_StopAtTheMaximum_AndThenTheParentIsTold()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers + "\nmax_resumes = 1\n", clock: clock);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        await f.WaitForResumeAsync();
        await clock.WaitForTimersAsync(1);
        clock.Advance(TimeSpan.FromMinutes(16));
        // Both holds ended: the resume takes A, which falls back to B, which stops on its limit again.
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);

        var card = await f.WaitForCardAsync(static card => (card.Message ?? string.Empty).Contains("in a row", StringComparison.Ordinal), "maximum");
        StringAssert.Contains(card.Message, "It resumed by itself once in a row: choose another model, or send again after the reset.");
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));
        await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PromptSentWhileAResumeWaits_CancelsTheResume()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        await f.WaitForResumeAsync();
        await clock.WaitForTimersAsync(1);

        f.Send("something else", "second");
        var sent = await f.NextSendAsync(f.B);
        Assert.AreEqual("something else", sent.Text);
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));
        var card = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.Info, "cancelled");
        StringAssert.Contains(card.Message, "the session was given a prompt");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, clock.PendingTimerCount);
    }

    [TestMethod]
    public async Task ResumeThatWaits_OutlivesARestartOfTheHost()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var f = await Fixture.CreateAsync(ResumingTiers, clock: clock);
        var start = clock.GetUtcNow();
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(2))));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, start.AddHours(1))));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);
        var resume = await f.WaitForResumeAsync();
        await clock.WaitForTimersAsync(1);

        await f.RestartAsync();
        var restored = await f.WaitForResumeAsync();
        Assert.AreEqual((resume.ResumeId, resume.ResumeAt, resume.ProviderKey, resume.Attempt), (restored.ResumeId, restored.ResumeAt, restored.ProviderKey, restored.Attempt));
        await clock.WaitForTimersAsync(1);

        // The new host knows no limit of the old one: the tier is taken in order once the time comes.
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        var continuation = await f.NextSendAsync(f.A);
        StringAssert.Contains(continuation.Text, "[CodeAlta resume]");
        Assert.IsNull(f.Host.Commands.GetPendingResume(f.Child.SessionId));
        Assert.AreEqual(0, (await new SessionResumeStore(f.Host.ApplicationDatabase).ReadAllAsync()).Count, "A resume that ran leaves the store.");
    }

    [TestMethod]
    public async Task ProviderOfAnotherSpace_IsNotATargetOfTheFallback()
    {
        // The session is in the default space; the next target of its tier is a provider of Business alone.
        await using var f = await Fixture.CreateAsync(Tiers + """

            [providers.prov-b]
            type = "openai-chat"
            api_url = "http://127.0.0.1:9/v1"
            api_key = "fixture"
            spaces = ["business"]
            """);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        Assert.AreEqual(0, f.B.SendCount);
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
    }

    [TestMethod]
    public async Task SandboxedSession_GoesOnOnlyWithAProviderWhoseToolsItsSandboxHolds()
    {
        // B has no type CodeAlta drives: it would run its own tools, outside the sandbox.
        await using var f = await Fixture.CreateAsync(Tiers + "\n\n[sandbox]\nall_sessions = \"workspace\"\n");
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task SandboxedSession_FallsBackToAProviderThatCodeAltaDrives()
    {
        await using var f = await Fixture.CreateAsync(Tiers + "\n\n[sandbox]\nall_sessions = \"workspace\"\n", bType: "openai-chat");
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("prov-b", (await f.ChildStateAsync()).ProviderKey);
    }

    [TestMethod]
    public async Task Switch_KeepsOrNarrowsThePermissionMode_NeverWidensIt()
    {
        await using var f = await Fixture.CreateAsync(Tiers, childPermissionMode: SessionPermissionModes.AcceptEdits,
            bModes: [SessionPermissionModes.Ask, SessionPermissionModes.Bypass]);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        // B has no "accept edits": the session gets B's narrower mode, not "bypass permissions".
        Assert.AreEqual(SessionPermissionModes.Ask, continuation.Options.PermissionMode);
        Assert.AreEqual(SessionPermissionModes.Ask, (await f.ChildStateAsync()).PermissionMode);
    }

    [TestMethod]
    public async Task SessionTier_IsRecorded_AndOutlivesAProviderSwitch()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        Assert.IsNull(await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsTrue(await f.Host.RuntimeService.SetSessionTierAsync(f.Child.SessionId, ModelTier.Quick));
        Assert.AreEqual(ModelTier.Quick, await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));

        var context = await f.Host.Commands.GetProviderSelectionAsync(f.Child.SessionId);
        Assert.IsNotNull(context);
        Assert.AreEqual("ok", await f.Host.Commands.SelectProviderAsync(context, "prov-b"));
        Assert.AreEqual(ModelTier.Quick, await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsNull((await f.ChildStateAsync()).PermissionMode, "A switch by hand still takes the mode of the provider.");

        Assert.IsTrue(await f.Host.RuntimeService.SetSessionTierAsync(f.Child.SessionId, null));
        Assert.IsNull(await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsFalse(await f.Host.RuntimeService.SetSessionTierAsync("missing-session", ModelTier.Quick));
    }

    // What a prompt that failed leaves in the queue of a session: a record of work that ended.
    private static SessionViewQueuedPrompt FailedQueueItem() => new()
        { QueueItemId = "ended", Kind = "send", Prompt = "Ended work", State = "failed", LastError = "The operation was canceled.", CreatedAt = DateTimeOffset.UtcNow };

    [TestMethod]
    public async Task QueueThatHoldsOnlyFailedWork_DoesNotHoldTheFallback()
    {
        await using var f = await Fixture.CreateAsync(Tiers, childQueue: [FailedQueueItem()]);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        var state = await f.ChildStateAsync();
        Assert.AreEqual("prov-b", state.ProviderKey);
        Assert.IsTrue(state.QueuedPrompts.Any(static prompt => prompt is { QueueItemId: "ended", State: "failed" }), "The record of the failed prompt is kept.");
    }

    [TestMethod]
    public async Task ProviderSwitchByHand_TakesASessionWithFailedQueueItems()
    {
        await using var f = await Fixture.CreateAsync(Tiers, childQueue: [FailedQueueItem()]);

        var context = await f.Host.Commands.GetProviderSelectionAsync(f.Child.SessionId);
        Assert.IsNotNull(context);
        Assert.AreEqual("ok", await f.Host.Commands.SelectProviderAsync(context, "prov-b"));
        Assert.AreEqual("prov-b", (await f.ChildStateAsync()).ProviderKey);
    }

    [TestMethod]
    public void ShortRateLimit_IsWaitedOut_NotLeft()
    {
        var on = new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: true);
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited, RetryAfterSeconds: 20)));
        Assert.IsTrue(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited, RetryAfterSeconds: 600)));
        Assert.IsTrue(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.Auth)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.Other)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on with { OnUnavailable = false }, new AgentFailureInfo(AgentFailureKind.Unavailable)));
        Assert.IsTrue(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.ModelUnavailable)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on with { OnUnavailable = false }, new AgentFailureInfo(AgentFailureKind.ModelUnavailable)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on with { OnQuota = false }, new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
    }

    [TestMethod]
    public void PermissionRank_OrdersTheModesFromTheNarrowest()
    {
        string?[] modes = ["dontAsk", "default", "acceptEdits", "auto", "bypassPermissions"];
        for (var index = 1; index < modes.Length; index++)
            Assert.IsTrue(SessionFallbackCoordinator.PermissionRank(modes[index - 1]) < SessionFallbackCoordinator.PermissionRank(modes[index]), modes[index]);
        Assert.AreEqual(SessionFallbackCoordinator.PermissionRank("default"), SessionFallbackCoordinator.PermissionRank(null));
    }

    private sealed record Outcome(AgentFailureInfo? Failure)
    {
        internal static Outcome Fail(AgentFailureInfo failure) => new(failure);
    }

    private sealed record SendRecord(string Text, AgentSessionCreateOptions Options, string? Provenance = null);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-fallback-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<AgentSessionUpdateEvent> _cards = new();
        private CancellationTokenSource _closing = new();
        private Task _collector = Task.CompletedTask;
        private Func<Task<CodeAltaHost>> _createHost = null!;
        private readonly List<OwnedSessionCommandReceipt> _receipts = [];

        internal ScriptedProvider A { get; } = new("prov-a", "model-a", [SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits, SessionPermissionModes.Bypass]);
        internal ScriptedProvider B { get; private set; } = null!;
        internal CodeAltaHost Host { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionViewDescriptor Parent { get; private set; } = null!;
        internal SessionViewDescriptor Child { get; private set; } = null!;

        /// <summary>The clock of the host, when the test moves the time itself.</summary>
        internal ManualClock? Clock { get; private set; }

        internal static async Task<Fixture> CreateAsync(string config, string? childPermissionMode = null, IReadOnlyList<string>? bModes = null,
            Func<SpaceCatalog, Task<string>>? childSpace = null, string? providerSpaces = null, IReadOnlyList<SessionViewQueuedPrompt>? childQueue = null, string? bType = null,
            ManualClock? clock = null)
        {
            var f = new Fixture { Clock = clock };
            f.B = new ScriptedProvider("prov-b", "model-b", bModes ?? [SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits, SessionPermissionModes.Bypass], bType);
            await f.SetupAsync(providerSpaces is null ? config : config + "\n" + providerSpaces, childPermissionMode, childSpace, childQueue);
            return f;
        }

        /// <summary>Closes the host and starts another one on the same profile, as CodeAlta does when it starts again.</summary>
        internal async Task RestartAsync()
        {
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WaitAsync(Timeout);
            _receipts.Clear();
            await _closing.CancelAsync();
            await Host.DisposeAsync();
            await _collector;
            _closing = new CancellationTokenSource();
            Host = await _createHost();
            _collector = CollectAsync();
        }

        private async Task SetupAsync(string config, string? childPermissionMode, Func<SpaceCatalog, Task<string>>? childSpace,
            IReadOnlyList<SessionViewQueuedPrompt>? childQueue)
        {
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var directory in new[] { home, global, project, builtin }) Directory.CreateDirectory(directory);
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            File.WriteAllText(Path.Combine(global, "config.toml"), config);
            var catalog = new CatalogOptions { GlobalRoot = global };
            var projectId = (await new ProjectCatalog(catalog).UpsertFromPathAsync(project, CancellationToken.None)).Id;
            Journal = new SessionViewJournalStore(catalog);
            Parent = await PersistAsync(projectId, project, "Parent", parentSessionId: null, permissionMode: null);
            // The space of the child, set up before the host reads it.
            var spaceId = childSpace is null ? null : await childSpace(new SpaceCatalog(new ProjectCatalog(catalog)));
            Child = await PersistAsync(projectId, project, "Child", Parent.SessionId, childPermissionMode, spaceId, childQueue);
            _createHost = () => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                TimeProvider = Clock,
                GlobalRoot = global,
                CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, _root),
                BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false,
                OwnsLogging = false,
                IsHeadless = true,
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(A.Descriptor, A.CreateRuntime);
                    registry.RegisterOrReplace(B.Descriptor, B.CreateRuntime);
                },
            }, CancellationToken.None);
            Host = await _createHost();
            _collector = CollectAsync();
        }

        private async Task<SessionViewDescriptor> PersistAsync(string projectId, string project, string title, string? parentSessionId, string? permissionMode,
            string? spaceId = null, IReadOnlyList<SessionViewQueuedPrompt>? queue = null)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = Guid.CreateVersion7().ToString(),
                Kind = SessionViewKind.ProjectSession,
                ProjectRef = projectId,
                ParentSessionId = parentSessionId,
                ProviderId = A.Descriptor.ProviderId.Value,
                ProviderKey = A.Descriptor.ProviderId.Value,
                WorkingDirectory = project,
                Title = title,
                ModelId = "model-a",
                ReasoningEffort = AgentReasoningEffort.High,
                AgentPromptId = "default",
                CreatedAt = DateTimeOffset.UtcNow,
                SpaceId = spaceId,
            };
            await Journal.EnsureHeaderAsync(session, CancellationToken.None);
            await Journal.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId,
                ProviderId = A.Descriptor.ProviderId,
                ProviderKey = A.Descriptor.ProviderId.Value,
                WorkingDirectory = session.WorkingDirectory,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None);
            // What a session of a provider writes once it ran: the fallback switches a session that ran.
            await Journal.CreateSessionStore().UpsertStateAsync(new AgentSessionState
            {
                SessionId = session.SessionId,
                ProviderKey = A.Descriptor.ProviderId.Value,
                UpdatedAt = session.CreatedAt,
            }, CancellationToken.None);
            await Journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
                PermissionMode = permissionMode,
                ParentSessionId = parentSessionId,
                QueuedPrompts = [.. queue ?? []],
            }, CancellationToken.None);
            return session;
        }

        private async Task CollectAsync()
        {
            try
            {
                await foreach (var value in Host.RuntimeService.StreamEventsAsync(_closing.Token))
                {
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.ProviderFallback or AgentSessionUpdateKind.Warning or AgentSessionUpdateKind.Info } update }
                        && update.SessionId == Child.SessionId)
                        _cards.Enqueue(update);
                }
            }
            catch (OperationCanceledException) { }
        }

        internal void Send(string text, string requestId = "first")
        {
            var admission = Host.Commands.AdmitSend(new OwnedTextSendRequest(requestId, Child.SessionId, text));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            _receipts.Add(admission.Receipt!);
        }

        internal async Task<AgentSessionUpdateEvent> WaitForCardAsync(AgentSessionUpdateKind kind)
            => await WaitForCardAsync(card => card.Kind == kind, kind.ToString());

        internal async Task<AgentSessionUpdateEvent> WaitForCardAsync(Func<AgentSessionUpdateEvent, bool> match, string what)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_cards.FirstOrDefault(match) is { } card) return card;
                await Task.Delay(20);
            }

            Assert.Fail($"No {what} card. Cards: " + string.Join(" | ", _cards.Select(static card => $"{card.Kind}: {card.Message}")));
            return null!;
        }

        /// <summary>Waits for the resume a session waits for, as the coordinator lists it once its card is written.</summary>
        internal async Task<SessionResumeSnapshot> WaitForResumeAsync(Func<SessionResumeSnapshot, bool>? match = null)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Host.Commands.GetPendingResume(Child.SessionId) is { } pending && (match?.Invoke(pending) ?? true)) return pending;
                await Task.Delay(20);
            }

            Assert.Fail("No resume waits. Cards: " + string.Join(" | ", _cards.Select(static card => $"{card.Kind}: {card.Message}")));
            return null!;
        }

        internal IReadOnlyList<AgentSessionUpdateEvent> Cards => [.. _cards];

        internal async Task<SendRecord> NextSendAsync(ScriptedProvider provider)
        {
            try { return await provider.NextSendAsync(); }
            catch (TimeoutException)
            {
                Assert.Fail("No send on " + provider.Descriptor.ProviderId.Value + ". Cards: " + string.Join(" | ", _cards.Select(static card => $"{card.Kind}: {card.Message}")));
                throw;
            }
        }

        internal async Task<SessionViewLocalState> ChildStateAsync()
            => (await Journal.ReadLatestStateAsync(Child.SessionId, Child.CreatedAt, CancellationToken.None))!;

        internal async Task<IReadOnlyList<SessionViewQueuedPrompt>> WaitForParentAsync(Func<IReadOnlyList<SessionViewQueuedPrompt>, bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (true)
            {
                var prompts = (await Journal.ReadLatestStateAsync(Parent.SessionId, Parent.CreatedAt, CancellationToken.None))?.QueuedPrompts
                    .Where(static prompt => prompt.Kind == "parent-notify").ToArray() ?? [];
                if (condition(prompts)) return prompts;
                if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("The parent was not told: " + string.Join("\n", prompts.Select(static p => p.Prompt)));
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WaitAsync(Timeout);
            await _closing.CancelAsync();
            await Host.DisposeAsync();
            await _collector;
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    // One-shot timers on a time the test moves; the callbacks run on the thread that moves it, outside the clock's gate.
    internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = start;

        public int PendingTimerCount
        {
            get { lock (_gate) return _timers.Count; }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(global::System.Threading.Timeout.InfiniteTimeSpan, period);
            lock (_gate)
            {
                var timer = new Timer(this, callback, state, _now + dueTime);
                _timers.Add(timer);
                return timer;
            }
        }

        // The timers are armed once the cards of a resume are written: the test waits for them before it moves the time.
        public async Task WaitForTimersAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (PendingTimerCount < count)
            {
                if (DateTimeOffset.UtcNow >= deadline) Assert.Fail($"{count} timer(s) expected, {PendingTimerCount} armed.");
                await Task.Delay(10);
            }
        }

        public void Advance(TimeSpan duration)
        {
            Timer[] due;
            lock (_gate)
            {
                _now += duration;
                due = [.. _timers.Where(timer => timer.DueAt <= _now)];
                foreach (var timer in due) _timers.Remove(timer);
            }

            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            private int _disposed;
            public DateTimeOffset DueAt { get; } = dueAt;

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("Only one-shot timers are expected.");

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                lock (owner._gate) owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ScriptedProvider
    {
        private readonly Channel<SendRecord> _sends = Channel.CreateUnbounded<SendRecord>();
        private int _sendCount;

        internal ScriptedProvider(string id, string model, IReadOnlyList<string> permissionModes, string? type = null)
        {
            Model = model;
            Descriptor = new(new ModelProviderId(id), id.ToUpperInvariant(), type) { DefaultModelId = model, PermissionModes = permissionModes };
        }

        internal string Model { get; }
        internal ModelProviderDescriptor Descriptor { get; }
        internal ConcurrentQueue<Outcome> Outcomes { get; } = new();
        internal int SendCount => Volatile.Read(ref _sendCount);

        internal async Task<SendRecord> NextSendAsync() => await _sends.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);

        private sealed class Runtime(ScriptedProvider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ModelProviderProbeResult
                {
                    ProviderId = owner.Descriptor.ProviderId,
                    // A second model, which a session can go on with when the account cannot use the first.
                    Models =
                    [
                        new AgentModelInfo(owner.Model, SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]),
                        new AgentModelInfo("model-c", SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]),
                    ],
                });
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Unexpected turn-executor route.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, options.SessionId!, options));
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, sessionId, options));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Session(ScriptedProvider owner, string sessionId, AgentSessionCreateOptions options) : IAgentSession
        {
            private Action<AgentEvent>? _handler;

            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => sessionId;
            public string? WorkspacePath => options.WorkingDirectory;

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                _handler = handler;
                return new Subscription(() => _handler = null);
            }

            public async Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                var number = Interlocked.Increment(ref owner._sendCount);
                var run = new AgentRunId($"{owner.Descriptor.ProviderId.Value}-run-{number}");
                var text = string.Concat(send.Input.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value));
                await owner._sends.Writer.WriteAsync(new SendRecord(text, options, send.Provenance), cancellationToken);
                if (owner.Outcomes.TryDequeue(out var outcome) && outcome.Failure is { } failure)
                {
                    _handler?.Invoke(new AgentErrorEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, $"{failure.Kind} on {ProviderId.Value}", exception: null, runId: run)
                    {
                        Failure = failure,
                    });
                    throw new InvalidOperationException($"{failure.Kind} on {ProviderId.Value}");
                }

                _handler?.Invoke(new AgentContentCompletedEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.Assistant, "answer-" + number, null, "Done."));
                _handler?.Invoke(new AgentSessionUpdateEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentSessionUpdateKind.Idle, null));
                return run;
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No steer.");
            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
