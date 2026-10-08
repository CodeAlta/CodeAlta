using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Source-audit gated real-host fixtures. No discovery initializer, shared runtime, event reader,
/// environment substitution, provider probe, or test-only production route.
/// </summary>
[TestClass]
public sealed class OwnedSessionCommandServiceTests
{
    [TestMethod]
    public Task ProjectReferencesReachProviderOnceAndReplayCannotChangeScope() => Fixture.RunAsync(async f =>
    {
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        var path = Path.Combine(f.ProjectRoot, "reference.txt");
        File.WriteAllText(path, "disposable\nreference");
        var scope = new OwnedProjectReferenceScope(project.Id, project.ProjectPath);
        var search = await f.Observe(f.Host.Commands.SearchReferencesAsync(scope, f.SessionId, "reference", CancellationToken.None));
        // The index is built in the background: a caller asks again while the folder is still being read.
        for (var attempt = 0; attempt < 100 && search.Status == "indexing"; attempt++)
        {
            await Task.Delay(50);
            search = await f.Observe(f.Host.Commands.SearchReferencesAsync(scope, f.SessionId, "reference", CancellationToken.None));
        }
        Assert.AreEqual("ok", search.Status);
        Assert.AreEqual("reference.txt", search.Items.Single().Path);
        Assert.AreEqual("resolved", (await f.Observe(f.Host.Commands.ObserveReferencesAsync(scope, f.SessionId, "@reference.txt:1-2", CancellationToken.None))).Items.Single().Status);
        Assert.AreEqual("resolved", (await f.Observe(f.Host.Commands.ObserveReferencesAsync(scope, null, "@reference.txt", CancellationToken.None))).Items.Single().Status);
        Assert.AreEqual("scope_missing", (await f.Observe(f.Host.Commands.ObserveReferencesAsync(scope with { ProjectPath = Path.GetTempPath() }, f.SessionId, "@reference.txt", CancellationToken.None))).Status);
        Assert.IsFalse(f.Provider.PreparationStarted.Task.IsCompleted, "Metadata search must not prepare a provider session.");
        Assert.AreEqual("scope_missing", (await f.Observe(f.Host.Commands.SearchReferencesAsync(
            scope with { ProjectPath = Path.GetTempPath() }, f.SessionId, "reference", CancellationToken.None))).Status);
        var request = new OwnedTextSendRequest("reference-send", f.SessionId, "Inspect @reference.txt:1-2 @@literal @missing") { References = scope };
        var admission = f.AdmitSend(request);
        Assert.IsNotNull(admission.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, admission.Receipt, "reference send");
        Assert.IsInstanceOfType<AgentInputItem.File>(f.Provider.FirstSendOptions!.Input.Items[1]);
        var file = (AgentInputItem.File)f.Provider.FirstSendOptions.Input.Items[1];
        Assert.AreEqual(path, file.Path);
        Assert.AreEqual(new AgentLineRange(1, 2), file.LineRange);
        File.Delete(path);
        Assert.AreSame(admission.Receipt, f.AdmitSend(request).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request with { References = null }).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(admission.Receipt.Completion)).Outcome);
        Assert.AreSame(admission.Receipt, f.AdmitSend(request).Receipt);
        project.Archived = true;
        await f.Observe(f.Host.ProjectCatalog.SaveAsync(project));
        Assert.AreEqual("scope_missing", (await f.Observe(f.Host.Commands.ObserveReferencesAsync(scope, f.SessionId, "@reference.txt", CancellationToken.None))).Status);
        Assert.AreEqual("scope_missing", (await f.Observe(f.Host.Commands.SearchReferencesAsync(scope, f.SessionId, "reference", CancellationToken.None))).Status);
        var archived = f.AdmitSend(request with { ClientRequestId = "archived-reference" });
        Assert.IsNotNull(archived.Receipt);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, (await f.Observe(archived.Receipt.Completion)).Outcome);
        Assert.IsNull(f.Provider.SecondSendOptions);
    });

    [TestMethod]
    public Task DeleteRefusesActiveOwnedRunWithoutLosingJournal() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        Assert.AreEqual("busy", await f.Observe(f.Host.Commands.DeleteCatalogSessionAsync(
            f.SessionId, project.Id, f.ProjectRoot, "Owned fixture")));
        var state = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        var queued = f.Host.Commands.AdmitQueue(new("retained-queue", f.SessionId,
            state.RuntimeInstanceId, state.Entry!.AttachmentGeneration, "queued input"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, queued.Kind);
        Assert.AreEqual("busy", await f.Observe(f.Host.Commands.DeleteCatalogSessionAsync(
            f.SessionId, project.Id, f.ProjectRoot, "Owned fixture")));
        Assert.IsNotNull(await f.Observe(f.Host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(f.SessionId)));
        var cancellation = f.Host.Commands.AdmitCancelQueue(new("cancel-retained", queued.Receipt!.OperationId));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, cancellation.Kind);
        await f.Observe(cancellation.Receipt!.Completion);
        await f.Observe(queued.Receipt.Completion);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(receipt.Completion)).Outcome);
    });

    [TestMethod]
    public Task SelectionWithoutModel_KeepsTheSessionModelInsteadOfSendingNone() => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        var choices = await f.Observe(f.Host.Commands.GetSelectionChoicesAsync(f.SessionId));
        Assert.IsNotNull(choices);
        // A prompt choice that names no model and no effort.
        var selection = new OwnedSessionSelection(choices.Current.ProviderKey, "plan", null, null);
        var admission = f.AdmitSend(new OwnedTextSendRequest("default-model-send", f.SessionId, "input") { Selection = selection });
        Assert.IsNotNull(admission.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, admission.Receipt, "default model send");
        Assert.AreEqual("fixture-model", f.Provider.Options!.Model);
        var runtime = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        Assert.AreEqual("plan", runtime.Entry!.AgentPromptId);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(admission.Receipt.Completion)).Outcome);
    });

    [TestMethod]
    public Task SessionWithoutModel_StartsWithAModelOfItsProvider() => Fixture.RunAsync(async f =>
    {
        // The provider is configured with a model that is not the first one it lists, and with an effort that model lacks.
        var configured = f.Provider.Descriptor with { DefaultModelId = "selected-model", DefaultReasoningEffort = AgentReasoningEffort.Low };
        f.Host.ModelProviderRegistry.RegisterOrReplace(configured, f.Provider.CreateRuntime);
        f.Provider.ExposeSelectionModels = true;
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        var created = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project,
            configured with { DefaultModelId = null, DefaultReasoningEffort = null }, "No model"));
        Assert.IsNull(created.ModelId);

        var choices = await f.Observe(f.Host.Commands.GetSelectionChoicesAsync(created.SessionId));
        Assert.IsNotNull(choices);
        Assert.AreEqual("selected-model", choices.Current.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, choices.Current.ReasoningEffort);
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, choices.Current));
        Assert.AreEqual(AgentReasoningEffort.High, choices.Models.Single(model => model.Id == "selected-model").StartEffort);
        Assert.IsNull(choices.Models.Single(model => model.Id == "fixture-model").StartEffort);

        // A send that selects nothing reaches the provider with what the session was shown to start with. The
        // session is attached again with that model, which ends the attachment its creation made.
        f.Provider.ReleaseAbort.TrySetResult();
        var admission = f.AdmitSend(new OwnedTextSendRequest("no-model-send", created.SessionId, "input"));
        Assert.IsNotNull(admission.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, admission.Receipt, "send without a model");
        Assert.AreEqual("selected-model", f.Provider.Options!.Model);
        Assert.AreEqual(AgentReasoningEffort.High, f.Provider.Options.ReasoningEffort);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(admission.Receipt.Completion)).Outcome);
    });

    [TestMethod]
    public Task SessionWithoutModel_TakesTheFirstListedModelWhenTheConfiguredOneIsNotListed() => Fixture.RunAsync(async f =>
    {
        var configured = f.Provider.Descriptor with { DefaultModelId = "retired-model" };
        f.Host.ModelProviderRegistry.RegisterOrReplace(configured, f.Provider.CreateRuntime);
        f.Provider.ExposeSelectionModels = true;
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        var created = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project, configured with { DefaultModelId = null }, "No model"));
        var choices = await f.Observe(f.Host.Commands.GetSelectionChoicesAsync(created.SessionId));
        Assert.IsNotNull(choices);
        Assert.AreEqual(choices.Models[0].Id, choices.Current.ModelId);
        Assert.IsNull(choices.Current.ReasoningEffort, "The first model reports no effort.");
    });

    [TestMethod]
    public Task SessionChoices_KeepASavedModelTheProviderDoesNotList() => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        var created = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project,
            f.Provider.Descriptor with { DefaultModelId = "retired-model", DefaultReasoningEffort = AgentReasoningEffort.Medium }, "Retired"));
        var choices = await f.Observe(f.Host.Commands.GetSelectionChoicesAsync(created.SessionId));
        Assert.IsNotNull(choices);
        // Shown as it is saved, and not a selection the provider offers.
        Assert.AreEqual("retired-model", choices.Current.ModelId);
        Assert.AreEqual(AgentReasoningEffort.Medium, choices.Current.ReasoningEffort);
        Assert.IsFalse(choices.Models.Any(model => model.Id == "retired-model"));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, choices.Current));
    });

    [TestMethod]
    public Task NamedSession_KeepsItsNameWhenASendAttachesItAgain() => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        // The attachment made at creation ends when a send attaches the session with another model.
        f.Provider.ReleaseAbort.TrySetResult();
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        var named = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project, f.Provider.Descriptor, "Nightly review"));
        var unnamed = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project, f.Provider.Descriptor, null));

        var first = f.AdmitSend(new OwnedTextSendRequest("named-send", named.SessionId, "input")
            { Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", "selected-model", AgentReasoningEffort.High) });
        Assert.IsNotNull(first.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, first.Receipt, "named send");
        Assert.AreEqual("selected-model", f.Provider.Options!.Model);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(first.Receipt.Completion)).Outcome);
        Assert.AreEqual("Nightly review", await TitleAsync(named.SessionId));

        // A rename is a name too, and it stays when the next send attaches the session again.
        Assert.IsTrue(await f.Observe(f.Host.Commands.RenameSessionAsync(unnamed.SessionId, project.Id, f.ProjectRoot, "Renamed")));
        var second = f.AdmitSend(new OwnedTextSendRequest("renamed-send", unnamed.SessionId, "input")
            { Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", "selected-model", AgentReasoningEffort.High) });
        Assert.IsNotNull(second.Receipt);
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, second.Receipt, "renamed send");
        f.Provider.ReleaseSecondSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(second.Receipt.Completion)).Outcome);
        Assert.AreEqual("Renamed", await TitleAsync(unnamed.SessionId));

        async Task<string?> TitleAsync(string sessionId)
        {
            var metadata = await f.Observe(f.Host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId));
            Assert.IsNotNull(metadata);
            return (metadata.Details as RawApiSessionMetadataDetails)?.Title;
        }
    });

    [TestMethod]
    public Task UnnamedSession_KeepsTheTitleItWasCreatedWithWhenASendAttachesItAgain() => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        f.Provider.ReleaseAbort.TrySetResult();
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        var unnamed = await f.Observe(f.Host.Commands.CreateDraftSessionAsync(project, f.Provider.Descriptor, null));
        Assert.AreEqual(project.DisplayName, unnamed.Title);
        var send = f.AdmitSend(new OwnedTextSendRequest("unnamed-send", unnamed.SessionId, "input")
            { Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", "selected-model", AgentReasoningEffort.High) });
        Assert.IsNotNull(send.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send.Receipt, "unnamed send");
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(send.Receipt.Completion)).Outcome);
        var metadata = await f.Observe(f.Host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(unnamed.SessionId));
        Assert.IsNotNull(metadata);
        // The first line of its summary is not written as its title: it would be taken for the name of the session.
        Assert.AreEqual(project.DisplayName, (metadata.Details as RawApiSessionMetadataDetails)?.Title);
    });

    [TestMethod]
    public Task SelectedConfiguration_ReachesProviderAndRetriesKeepExactSettings() => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        var choices = await f.Observe(f.Host.Commands.GetSelectionChoicesAsync(f.SessionId));
        Assert.IsNotNull(choices);
        Assert.IsTrue(choices.Prompts.Any(p => p.Id == "default"));
        Assert.IsTrue(choices.Models.Any(m => m.Id == "selected-model"));
        Assert.IsTrue(choices.Prompts.Any(p => p.Id == "plan"));
        var selection = new OwnedSessionSelection(choices.Current.ProviderKey, "plan", "selected-model", AgentReasoningEffort.High);
        var request = new OwnedTextSendRequest("selection-send", f.SessionId, "selected input") { Selection = selection };
        var admission = f.AdmitSend(request);
        Assert.IsNotNull(admission.Receipt);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, admission.Receipt, "selected send");
        Assert.AreEqual("selected-model", f.Provider.Options!.Model);
        Assert.AreEqual(AgentReasoningEffort.High, f.Provider.Options.ReasoningEffort);
        var runtime = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        Assert.AreEqual("plan", runtime.Entry!.AgentPromptId);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict,
            f.AdmitSend(request with { Selection = selection with { ReasoningEffort = null } }).Kind);
        Assert.AreSame(admission.Receipt, f.AdmitSend(request).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy,
            f.AdmitSend(request with { ClientRequestId = "concurrent-selection" }).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(admission.Receipt.Completion)).Outcome);
        await f.Observe(f.Host.RuntimeService.SetActiveSessionAgentPromptIdAsync(f.SessionId, "default"));
        var next = f.AdmitSend(request with { ClientRequestId = "next-selected-send" });
        Assert.IsNotNull(next.Receipt);
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, next.Receipt, "next selected send");
        f.Provider.ReleaseSecondSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(next.Receipt.Completion)).Outcome);
        runtime = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        Assert.AreEqual("plan", runtime.Entry!.AgentPromptId, "Explicit next-send selection takes precedence over an older pending prompt.");
        Assert.IsNull(runtime.Entry.PendingAgentPromptId);
    });

    [TestMethod]
    [DataRow("send", "describe")]
    [DataRow("abort", "describe")]
    [DataRow("shutdown", "describe")]
    [DataRow("send", "")]
    [DataRow("abort", "")]
    [DataRow("shutdown", "")]
    public Task ImageSend_FreezesBytesAndTitleAndReplaysWithoutSavingAgain(string completion, string text) => Fixture.RunAsync(async f =>
    {
        f.Provider.ExposeSelectionModels = true;
        await f.Observe(f.Host.ModelProviderInitializationService.RefreshProviderAsync(f.Provider.Descriptor.ProviderId));
        var bytes = OwnedPromptImageTests.Png(1, 1);
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        var images = new[] { new OwnedPromptImage("Exact title", "image/png", Convert.ToBase64String(bytes)),
            new OwnedPromptImage("Second title", "image/png", Convert.ToBase64String(OwnedPromptImageTests.Png(2, 1))) };
        var request = new OwnedTextSendRequest("image-original", f.SessionId, text)
        {
            Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", "image-model", null), Images = images,
            References = new(project.Id, project.ProjectPath),
        };
        Assert.ThrowsExactly<ArgumentException>(() => f.AdmitSend(request with { Text = " \r\n\t" }));
        Assert.ThrowsExactly<ArgumentException>(() => f.AdmitSend(request with { Text = "", Images = [] }));
        var admission = f.AdmitSend(request);
        Assert.IsNotNull(admission.Receipt);
        images[0] = images[0] with { Title = "changed after admission" };
        await f.ObserveReadiness(f.Provider.SendStarted.Task, admission.Receipt, "image send");
        var typedImages = f.Provider.Input!.Items.OfType<AgentInputItem.LocalImage>().ToArray();
        var typedText = f.Provider.Input.Items.OfType<AgentInputItem.Text>().ToArray();
        Assert.HasCount(text.Length == 0 ? 0 : 1, typedText, "Image-only inputs must not add dummy or empty text items.");
        Assert.HasCount(2, typedImages);
        Assert.AreEqual("Second title", typedImages[1].DisplayName);
        CollectionAssert.AreEqual(OwnedPromptImageTests.Png(2, 1), await File.ReadAllBytesAsync(typedImages[1].Path));
        var input = typedImages[0];
        Assert.AreEqual("Exact title", input.DisplayName);
        Assert.AreEqual("image/png", input.MediaType);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input.Path));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request).Kind);
        images[0] = images[0] with { Title = "Exact title" };
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request with { Images = images.Reverse().ToArray() }).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request with { Images = [images[0] with { Base64 = images[1].Base64 }, images[1]] }).Kind);
        Assert.AreSame(admission.Receipt, f.AdmitSend(request).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request with { Text = text.Length == 0 ? "describe" : "" }).Kind);
        Assert.HasCount(2, Directory.GetFiles(Path.GetDirectoryName(input.Path)!));
        var disposal = completion == "shutdown" ? f.BeginDisposal() : null;
        var abort = completion == "abort" ? f.Accept(f.AdmitAbort(new("abort-image-original", admission.Receipt.OperationId))) : null;
        if (completion != "send")
        {
            await f.ObserveReadiness(f.Provider.SendCancelled.Task, admission.Receipt, "image cancellation");
            Assert.IsFalse(admission.Receipt.Completion.IsCompleted);
            Assert.AreEqual(0, f.Provider.EarlyDisposals);
        }
        f.Provider.ReleaseAll();
        if (disposal is not null) await f.Observe(disposal);
        if (abort is not null) await f.Observe(abort.Completion);
        Assert.AreEqual(completion == "send" ? OwnedSessionCommandOutcome.Completed : OwnedSessionCommandOutcome.Cancelled,
            (await f.Observe(admission.Receipt.Completion)).Outcome);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input.Path), "Successfully saved copies stay session-owned after cancellation/shutdown.");
    });

    [TestMethod]
    [DataRow("fixture-model", true, "describe")]
    [DataRow("text-model", true, "describe")]
    [DataRow("image-model", false, "describe")]
    [DataRow("fixture-model", true, "")]
    [DataRow("text-model", true, "")]
    [DataRow("image-model", false, "")]
    public Task ImageSend_RefusesUnknownUnsupportedAndUnobservedModelsWithoutProbe(string model, bool observeModels, string text) => Fixture.RunAsync(async f =>
    {
        if (observeModels)
        {
            f.Provider.ExposeSelectionModels = true;
            await f.Observe(f.Host.ModelProviderInitializationService.RefreshProviderAsync(f.Provider.Descriptor.ProviderId));
        }
        var project = await f.Observe(f.Host.ProjectCatalog.GetByPathAsync(f.ProjectRoot));
        Assert.IsNotNull(project);
        var request = new OwnedTextSendRequest("image-refused", f.SessionId, text)
        {
            Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", model, null),
            References = new(project.Id, project.ProjectPath),
            Images = [new("Image 1", "image/png", Convert.ToBase64String(OwnedPromptImageTests.Png(1, 1)))],
        };
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Host.Commands.AdmitSend(request, canceled.Token));
        var admission = f.AdmitSend(request);
        Assert.IsNotNull(admission.Receipt);
        Assert.AreEqual("preparation_failed", (await f.Observe(admission.Receipt.Completion)).Code);
        Assert.IsNull(f.Provider.Input);
        Assert.AreEqual(observeModels, f.Provider.ProbeStarted.Task.IsCompleted);
        Assert.AreSame(admission.Receipt, f.AdmitSend(request).Receipt);
        Assert.HasCount(0, Directory.GetFiles(Directory.GetParent(f.ProjectRoot)!.FullName, "*.png", SearchOption.AllDirectories));
    });

    [TestMethod]
    public Task ImageReceipts_MakeRoomWithSettledOnesAndDoNotConsumeTextCapacity() => Fixture.RunAsync(async f =>
    {
        var request = new OwnedTextSendRequest("bounded-image-0", f.SessionId, "describe")
        {
            Selection = new(f.Provider.Descriptor.ProviderId.Value, "default", "unobserved", null),
            Images = [new("Image 1", "image/png", Convert.ToBase64String(OwnedPromptImageTests.Png(1, 1)))],
        };
        OwnedSessionCommandReceipt? first = null, last = null;
        for (var index = 0; index < OwnedSessionCommandService.MaximumImageReceipts; index++)
        {
            var admission = f.AdmitSend(request with { ClientRequestId = "bounded-image-" + index });
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            first ??= admission.Receipt;
            last = admission.Receipt;
            Assert.AreEqual("preparation_failed", (await f.Observe(admission.Receipt!.Completion)).Code);
        }

        // The images of a send stay with its receipt, and the owner keeps few of them: one more takes the place
        // of the oldest settled one, which is not run again when its key comes back.
        Assert.AreSame(first, f.AdmitSend(request).Receipt);
        var next = f.Accept(f.AdmitSend(request with { ClientRequestId = "one-more" }));
        Assert.AreEqual("preparation_failed", (await f.Observe(next.Completion)).Code);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSend(request).Kind);
        Assert.AreSame(last, f.AdmitSend(request with { ClientRequestId = "bounded-image-" + (OwnedSessionCommandService.MaximumImageReceipts - 1) }).Receipt);
        Assert.AreSame(next, f.AdmitSend(request with { ClientRequestId = "one-more" }).Receipt);
        var text = f.Accept(f.AdmitSend(new("text-after-image-capacity", f.SessionId, "text only")));
        f.Provider.ReleaseAll();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(text.Completion)).Outcome);
    });

    [TestMethod]
    public void SelectedConfiguration_RejectsUnknownProviderPromptModelAndEffort()
    {
        var selection = new OwnedSessionSelection("provider", "default", "model", AgentReasoningEffort.High);
        var choices = new OwnedSelectionChoices(selection, [new("default", "Default")],
            [new("model", "Model", [AgentReasoningEffort.High])]);
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selection));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selection with { ProviderKey = "other" }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selection with { AgentPromptId = "missing" }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selection with { ModelId = "missing" }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selection with { ReasoningEffort = AgentReasoningEffort.Low }));
        Assert.IsFalse(OwnedSessionCommandService.IsValidSelection(choices, selection with { ModelId = null }));
        Assert.IsTrue(OwnedSessionCommandService.IsValidSelection(choices, selection with { ModelId = null, ReasoningEffort = null }));
    }

    [TestMethod]
    public Task DifferentProviderSelection_IsAdmittedThenFailsPreparationWithoutReplacingSource() => Fixture.RunAsync(async f =>
    {
        // This helper completes a real owned Send and commits the fake idle event; it does not compact.
        await f.PrepareCompact();
        var before = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        Assert.IsNotNull(before.Entry);
        Assert.IsNull(before.Entry.ActiveRunId);
        var sourceCounts = (f.Provider.Creates, f.Provider.Resumes, f.Provider.Sends,
            f.Provider.Aborts, f.Provider.RuntimeStops, f.Provider.RuntimeDisposals, f.Provider.SessionDisposals);
        var target = new ControlledProvider("other-fixture");
        target.ReleaseAll(); // Even an unexpected target invocation must remain drainable on assertion failure.
        f.Host.ModelProviderRegistry.RegisterOrReplace(target.Descriptor, target.CreateRuntime);
        f.Provider.ExposeSelectionModels = true;
        var releaseChoices = f.NewGate();
        f.Provider.ProbeDependency = releaseChoices.Task;
        var selection = new OwnedSessionSelection(target.Descriptor.ProviderId.Value, "default", "fixture-model", null);
        var request = new OwnedTextSendRequest("different-provider", f.SessionId, "  immutable input\r\n ") { Selection = selection };
        var receipt = f.Accept(f.AdmitSend(request));
        await f.ObserveReadiness(f.Provider.ProbeStarted.Task, receipt, "current-provider choices");
        Assert.IsFalse(receipt.Completion.IsCompleted);

        using var waiterCancellation = new CancellationTokenSource();
        var waiter = CanceledWaiterAsync();
        waiterCancellation.Cancel();
        await f.Observe(waiter);
        Assert.IsFalse(receipt.Completion.IsCompleted, "Canceling observation does not settle preparation or release its slot.");
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy,
            f.AdmitSend(request with { ClientRequestId = "replacement-while-pending" }).Kind);
        CheckRetainedRequest();
        await CheckSourceAsync();

        releaseChoices.TrySetResult();
        var result = await f.Observe(receipt.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("preparation_failed", result.Code, "This is an admitted preparation failure, not a pre-admission refusal.");
        CheckRetainedRequest();
        await CheckSourceAsync();

        async Task CanceledWaiterAsync()
        {
            try { await receipt.Completion.WaitAsync(waiterCancellation.Token); Assert.Fail("Expected canceled observation."); }
            catch (OperationCanceledException) when (waiterCancellation.IsCancellationRequested) { }
        }

        void CheckRetainedRequest()
        {
            var replay = f.AdmitSend(request);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind);
            Assert.AreSame(receipt, replay.Receipt);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request with { Text = "changed input" }).Kind);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict,
                f.AdmitSend(request with { Selection = selection with { ProviderKey = f.Provider.Descriptor.ProviderId.Value } }).Kind);
            Assert.AreEqual("different-provider", request.ClientRequestId);
            Assert.AreEqual("  immutable input\r\n ", request.Text);
            Assert.AreEqual(selection, request.Selection);
        }

        async Task CheckSourceAsync()
        {
            var current = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
            Assert.AreEqual(before, current, "Runtime/attachment identity, transition state and source configuration must remain unchanged.");
            Assert.AreEqual(sourceCounts, (f.Provider.Creates, f.Provider.Resumes, f.Provider.Sends,
                f.Provider.Aborts, f.Provider.RuntimeStops, f.Provider.RuntimeDisposals, f.Provider.SessionDisposals));
            Assert.AreEqual((0, 0, 0, 0, 0, 0, 0, 0), (target.RuntimeStarts, target.Creates, target.Resumes, target.Sends,
                target.Aborts, target.RuntimeStops, target.RuntimeDisposals, target.SessionDisposals));
        }
    });

    [TestMethod]
    public void PluginEnvironment_UsesExplicitSnapshotWithoutAmbientFallback()
    {
        var options = new CodeAltaHostOptions
        {
            PluginEnvironment = FrozenDictionary<string, string?>.Empty,
        };
        var operationOptions = CodeAltaHost.CreatePluginOperationOptions(
            options,
            new CatalogOptions { GlobalRoot = @"Q:\owned-global" },
            new ProjectDescriptor { Id = "literal-project", ProjectPath = @"Q:\owned-project" });
        Assert.AreEqual(0, operationOptions.Environment.Count);
        Assert.AreEqual("literal-project", operationOptions.ProjectId);
        Assert.AreEqual(@"Q:\owned-project", operationOptions.ProjectPath);
    }

    [TestMethod]
    public void PluginEnvironment_CopiesUsingCaseInsensitiveKeys()
    {
        var supplied = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MiXeD"] = "before",
            ["NullValue"] = null,
        };
        var operationOptions = CodeAltaHost.CreatePluginOperationOptions(
            new CodeAltaHostOptions { PluginEnvironment = supplied },
            new CatalogOptions { GlobalRoot = @"Q:\owned-global" },
            new ProjectDescriptor { Id = "literal-project", ProjectPath = @"Q:\owned-project" });
        supplied["MiXeD"] = "after";
        supplied["added"] = "later";
        Assert.AreNotSame(supplied, operationOptions.Environment);
        Assert.AreEqual(2, operationOptions.Environment.Count);
        Assert.AreEqual("before", operationOptions.Environment["mixed"]);
        Assert.IsTrue(operationOptions.Environment.TryGetValue("nullvalue", out var nullValue));
        Assert.IsNull(nullValue);
        Assert.IsFalse(operationOptions.Environment.ContainsKey("added"));
    }

    [TestMethod]
    public void BuiltInRoot_RejectsNonAbsolutePaths()
    {
        Assert.Throws<ArgumentNullException>(() => new BuiltInCodeAltaSkillRootProvider(null!));
        foreach (var path in new[] { "", " ", "relative", "../relative", "C:relative", "\\rooted" })
            Assert.Throws<ArgumentException>(() => new BuiltInCodeAltaSkillRootProvider(path));
    }

    [TestMethod]
    public async Task BuiltInRoot_ReturnsExplicitRootWithUnchangedIdentityAndPrecedence()
    {
        var root = Path.DirectorySeparatorChar == '\\' ? @"Q:\owned-skills" : "/owned-skills";
        var provider = new BuiltInCodeAltaSkillRootProvider(root);
        var roots = await provider.GetRootsAsync(new SkillDiscoveryContext());
        Assert.HasCount(1, roots);
        Assert.AreEqual(Path.GetFullPath(root), roots[0].RootPath);
        Assert.AreEqual("builtin:codealta", roots[0].SourceId);
        Assert.AreEqual(SkillSourceKind.Builtin, roots[0].SourceKind);
        Assert.AreEqual(SkillScopeKind.Builtin, roots[0].Scope);
        Assert.AreEqual(4, roots[0].Precedence);
    }

    [TestMethod]
    public void BuiltInRoot_ValidatesContextAndCancellationWithoutDiscovery()
    {
        var provider = new BuiltInCodeAltaSkillRootProvider();
        Assert.Throws<ArgumentNullException>(() => provider.GetRootsAsync(null!));
        Assert.Throws<OperationCanceledException>(() => provider.GetRootsAsync(new SkillDiscoveryContext(), new CancellationToken(true)));
    }

    [TestMethod]
    public Task AdmitSend_UsesRealHostRuntimeAndRegisteredSessionProvider() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(f.SessionId, f.Provider.LastSessionId);
        Assert.AreEqual(1, f.Provider.RuntimeStarts);
        Assert.AreEqual(1, f.Provider.Sends);
        Assert.AreEqual(f.ProjectRoot, f.Provider.Options!.WorkingDirectory);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(receipt.Completion)).Outcome);
    });

    [TestMethod]
    public Task AdmitSend_ReturnsReceiptBeforeProviderCompletion() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.IsFalse(receipt.Completion.IsCompleted);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task DurableHeader_ResumesDuringOwnedPreparation() => Fixture.RunAsync(async f =>
    {
        Assert.AreEqual(0, f.Provider.Resumes);
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Resumes);
        Assert.AreEqual(0, f.Provider.Creates);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task ResumeNotFound_UsesExistingStartFallback() => Fixture.RunAsync(async f =>
    {
        f.Provider.ResumeNotFound = true;
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Resumes);
        Assert.AreEqual(1, f.Provider.Creates);
        Assert.AreEqual(f.SessionId, f.Provider.LastSessionId);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task AdmissionCancellation_DoesNotCancelAcceptedExecution() => Fixture.RunAsync(async f =>
    {
        using var caller = new CancellationTokenSource();
        var request = new OwnedTextSendRequest("send", f.SessionId, "text");
        var receipt = f.Accept(f.AdmitSend(request, caller.Token));
        var cancellation = f.Track(caller.CancelAsync());
        await f.Observe(cancellation);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.IsFalse(f.Provider.SendToken.IsCancellationRequested);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(receipt.Completion)).Outcome);
        Assert.Throws<OperationCanceledException>(() => f.AdmitSend(request with { ClientRequestId = "cancelled" }, caller.Token));
    });

    [TestMethod]
    public Task Retry_ReturnsSameReceiptAndRejectsConflictingPayload() => Fixture.RunAsync(async f =>
    {
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("leading", " " + f.SessionId, "text")));
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("trailing", f.SessionId + " ", "text")));
        var receipt = f.Send();
        var retry = f.AdmitSend(new("send", f.SessionId.ToUpperInvariant(), "text"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, retry.Kind);
        Assert.AreSame(receipt, retry.Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(new("send", f.SessionId, "different")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitAbort(new("send", receipt.OperationId)).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
        Assert.AreSame(receipt, f.AdmitSend(new("send", f.SessionId, "text")).Receipt);
    });

    [TestMethod]
    public Task ReceiptCapacity_MakesRoomWithSettledReceiptsAndNeverRunsAnExpiredKeyAgain() => Fixture.RunAsync(async f =>
    {
        Assert.AreEqual(2, f.Host.Commands.ReceiptCapacity);
        var first = f.Send("first");
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(first.Completion);
        var second = f.Send("second");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, second, "second send");
        Assert.IsTrue(f.Host.Commands.HasCapacity, "A settled receipt is room for a new command.");

        // A command that is refused for another reason takes nobody's place: the first receipt still answers its retry.
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitSend(new("busy", f.SessionId, "text")).Kind);
        Assert.AreSame(first, f.AdmitSend(new("first", f.SessionId, "text")).Receipt);

        // A new command takes the place of the oldest settled receipt. Its key stays known: it is not run again,
        // whatever comes with it.
        var late = f.Abort(first, "late");
        Assert.AreEqual("already_terminal", (await f.Observe(late.Completion)).Code);
        var expired = f.AdmitSend(new("first", f.SessionId, "text"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, expired.Kind);
        Assert.IsNull(expired.Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSend(new("first", f.SessionId, "other text")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitAbort(new("first", second.OperationId)).Kind);
        Assert.AreEqual(2, f.Provider.Sends);
        // Nothing of a send whose receipt made room is left to abort.
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.UnknownTarget, f.AdmitAbort(new("too-late", first.OperationId)).Kind);

        // A pending command is never forgotten: once every kept receipt is pending, a new command is refused.
        var abort = f.Abort(second);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.IsFalse(f.Host.Commands.HasCapacity);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Capacity, f.AdmitAbort(new("full", second.OperationId)).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Capacity, f.AdmitSend(new("full-send", f.SessionId, "text")).Kind);
        Assert.AreSame(second, f.AdmitSend(new("second", f.SessionId, "text")).Receipt);
        Assert.AreSame(abort, f.AdmitAbort(new("abort", second.OperationId)).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitAbort(new("late", first.OperationId)).Kind);

        // Once they settle, commands are accepted again, for as long as the host runs.
        f.Provider.ReleaseAll();
        await f.Observe(abort.Completion);
        await f.Observe(second.Completion);
        for (var index = 0; index < 6; index++)
        {
            var next = f.Send("next-" + index);
            Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(next.Completion)).Outcome);
        }

        Assert.AreEqual(8, f.Provider.Sends);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSend(new("second", f.SessionId, "text")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSend(new("next-0", f.SessionId, "text")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, f.AdmitSend(new("next-5", f.SessionId, "text")).Kind);
        Assert.AreEqual(8, f.Provider.Sends);
    }, capacity: 2);

    [TestMethod]
    public void ExpiredKeys_RememberTheMostRecentOnes()
    {
        var keys = new OwnedSessionCommandService.ExpiredKeys(3);
        foreach (var key in new[] { "a", "b", "c", "b" }) keys.Add(key);
        Assert.AreEqual(3, keys.Count);
        Assert.IsTrue(keys.Contains("a") && keys.Contains("b") && keys.Contains("c"));
        Assert.IsFalse(keys.Contains("A"), "Keys are compared as they are written.");

        // One more than it keeps: the oldest one is no longer known.
        keys.Add("d");
        Assert.AreEqual(3, keys.Count);
        Assert.IsFalse(keys.Contains("a"));
        Assert.IsTrue(keys.Contains("b") && keys.Contains("c") && keys.Contains("d"));
        Assert.ThrowsExactly<ArgumentNullException>(() => keys.Add(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OwnedSessionCommandService.ExpiredKeys(0));
    }

    [TestMethod]
    public Task ConcurrentAdmission_ReservesBeforeLookupAndAllowsOneActiveSend() => Fixture.RunAsync(async f =>
    {
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("alias", "\t" + f.SessionId + "\n", "text")));
        var launch = f.NewGate();
        async Task<OwnedSessionCommandAdmission> Admit(string id)
        {
            await launch.Task.ConfigureAwait(false);
            return f.AdmitSend(new(id, f.SessionId, "text"));
        }
        var first = f.Track(Admit("first"));
        var second = f.Track(Admit("second"));
        launch.TrySetResult();
        var a = await f.Observe(first);
        var b = await f.Observe(second);
        var accepted = a.Kind == OwnedSessionCommandAdmissionKind.Accepted ? a : b;
        var rejected = ReferenceEquals(accepted, a) ? b : a;
        var receipt = f.Accept(accepted);
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("active-alias", f.SessionId + " ", "text")));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, rejected.Kind);
        // The source fixture, not this provider barrier, proves reservation precedes lookup.
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Sends);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task CapturedRequest_DoesNotExposeMutableDescriptorOrOptions() => Fixture.RunAsync(async f =>
    {
        var request = new OwnedTextSendRequest("send", f.SessionId, "captured");
        var receipt = f.Accept(f.AdmitSend(request));
        request = request with { Text = "replacement", SessionId = "different" };
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(f.SessionId, receipt.SessionId);
        Assert.AreEqual("captured", f.Provider.Input!.Items.OfType<AgentInputItem.Text>().Single().Value);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request).Kind);
        Assert.AreEqual("fixture-model", f.Provider.Options!.Model);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task Abort_BeforeExecutionDoesNotStartProviderWork() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        var abort = f.Abort(send);
        f.Provider.ReleasePreparation.TrySetResult();
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(send.Completion)).Outcome);
        Assert.AreEqual(0, f.Provider.Sends);
    }, holdPreparation: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Abort_DuringCreationWaitsForAttachmentAndCallsActualAbort(bool reviewPermissions) => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        var abort = f.Abort(send);
        Assert.IsFalse(abort.Completion.IsCompleted);
        Assert.AreEqual(0, f.Provider.Aborts);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.AreEqual(1, f.Provider.Aborts);
        Assert.AreEqual(0, f.Provider.Sends);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(send.Completion);
    }, holdPreparation: true, reviewPermissions: reviewPermissions);

    [TestMethod]
    public Task Abort_DuringSendUsesReservedRuntimeRoute() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var abort = f.Abort(send);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.IsFalse(send.Completion.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitSend(new("next", f.SessionId, "text")).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(send.Completion)).Outcome);
    });

    [TestMethod]
    public Task Abort_RetriesShareControlAndCannotReachNextSend() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var abort = f.Abort(send);
        var alias = f.Abort(send, "alias");
        Assert.AreSame(abort, f.AdmitAbort(new("abort", send.OperationId)).Receipt);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.AreEqual(1, f.Provider.Aborts);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(alias.Completion);
        await f.Observe(send.Completion);
        var next = f.Send("next");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, next, "second send");
        var late = f.Abort(send, "late");
        Assert.AreEqual("already_terminal", (await f.Observe(late.Completion)).Code);
        Assert.AreEqual(1, f.Provider.Aborts);
        f.Provider.ReleaseSecondSend.TrySetResult();
        await f.Observe(next.Completion);
    });

    [TestMethod]
    public Task PreparationFailure_DoesNotCallMissingEntryAbort() => Fixture.RunAsync(async f =>
    {
        f.Provider.FailPreparation = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        var abort = f.Abort(send);
        f.Provider.ReleasePreparation.TrySetResult();
        Assert.AreEqual("not_attached", (await f.Observe(abort.Completion)).Code);
        Assert.AreEqual("preparation_failed", (await f.Observe(send.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Aborts);
    }, holdPreparation: true);

    [TestMethod]
    public Task Dispose_JoinsPreparationSendAndControlBeforeDependencies() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        var disposal = f.BeginDisposal();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitSend(new("closed", f.SessionId, "text")).Kind);
        Assert.AreEqual(0, f.Provider.RuntimeDisposals);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, send, "disposal abort");
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(0, f.Provider.RuntimeDisposals);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
        Assert.AreEqual(1, f.Provider.RuntimeDisposals);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    }, capacity: 1, holdPreparation: true);

    [TestMethod]
    public Task ProviderFault_IsObservedAndRetainedInReceipt() => Fixture.RunAsync(async f =>
    {
        f.Provider.FailSend = true;
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        f.Provider.ReleaseSend.TrySetResult();
        var result = await f.Observe(receipt.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("send_failed", result.Code);
        Assert.AreSame(receipt, f.AdmitSend(new("send", f.SessionId, "text")).Receipt);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Interactions_DenyPermissionAndCancelUserInput(bool reviewPermissions) => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestInteractions = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PermissionDecision);
        Assert.IsTrue(f.Provider.InputCancelled);
        Assert.IsTrue(f.Provider.Options!.Tools is null || f.Provider.Options.Tools.Count == 0);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    }, reviewPermissions: reviewPermissions);

    [TestMethod]
    public Task OwnedPermission_DefaultAndPreparationRemainDenied() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        Assert.IsNull(f.Provider.FirstSendOptions!.OnPermissionRequest);
        Assert.IsNull(f.Provider.FirstSendOptions.RunLifecycle);
        var denied = f.Permission(f.Provider.Options!.OnPermissionRequest);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(denied)).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    });

    [TestMethod]
    public Task OwnedPermission_OptInUsesActualPerSendCallbackWithNullRunAndNoneToken() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        f.Provider.RequestPerSendPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        Assert.IsNotNull(f.Provider.FirstSendOptions!.OnPermissionRequest);
        var pending = f.Track(f.Provider.SendPermission!);
        var handle = await f.PendingHandle();
        Assert.IsNull(handle.RunId);
        Assert.AreEqual(f.SessionId, handle.SessionId);
        Assert.IsFalse(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(handle, AgentPermissionDecisionKind.AllowForSession).AsTask()));
        Assert.IsTrue(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce).AsTask()));
        // Decision is inert data: this fake never executes a tool, shell or model turn.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(f.Provider.Options!.OnPermissionRequest))).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_PublicOwnedReviewUsesActualReceiptRuntimeAndAttachment() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPerSendPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Track(f.Provider.SendPermission!);
        var permissions = f.Host.RuntimeService.Permissions;
        var page = await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        var current = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId, CancellationToken.None));
        Assert.HasCount(1, page.Entries);
        Assert.IsFalse(page.HasMore);
        Assert.IsNotNull(current.Entry);
        var entry = page.Entries.Single();
        Assert.AreEqual(send.OperationId, entry.Handle.OperationId);
        Assert.AreEqual(current.RuntimeInstanceId, entry.Handle.RuntimeInstanceId);
        Assert.AreNotEqual(Guid.Empty, entry.Handle.RuntimeInstanceId);
        Assert.AreEqual(current.Entry.AttachmentGeneration, entry.Handle.AttachmentGeneration);
        Assert.IsTrue(entry.Handle.AttachmentGeneration > 0);
        Assert.AreEqual(f.SessionId, entry.Handle.Attempt.SessionId);
        Assert.IsNull(entry.Handle.Attempt.RunId); // Do not invent a run from the separate current-runtime observation.
        Assert.AreEqual("owned-permission", entry.Handle.Attempt.InteractionId);
        Assert.AreNotEqual(Guid.Empty, entry.Handle.Attempt.AttemptId);
        Assert.AreEqual(entry.Handle.Attempt, entry.Request.Handle);
        Assert.AreEqual(f.Provider.Descriptor.ProviderId, entry.Request.ProviderId);
        Assert.AreEqual("commandExecution", entry.Request.Kind);
        Assert.AreEqual("inert fixture command", entry.Request.Command);
        Assert.AreEqual(f.ProjectRoot, entry.Request.WorkingDirectory);
        Assert.AreEqual("fixture", entry.Request.Reason);
        Assert.IsNull(entry.Request.GrantRoot);
        Assert.IsFalse(pending.IsCompleted);
        Assert.IsTrue(await f.Observe(permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        // AllowOnce is inert decision data here: the unchanged fake cannot execute a tool or model turn.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(pending)).Kind);
        Assert.IsFalse(await f.Observe(permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(send.Completion)).Outcome);
        Assert.HasCount(0, (await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask())).Entries);
    }, reviewPermissions: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_SendReturnCancelsPendingAndOldDelegateCannotJoinReusedCoordinator(bool failSend) => Fixture.RunAsync(async f =>
    {
        f.Provider.FailSend = failSend;
        var first = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, first, "first send");
        var old = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(old);
        var oldHandle = await f.PendingHandle();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(first.Completion);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        f.Provider.FailSend = false;
        var second = f.Send("second");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, second, "second send");
        Assert.AreEqual(1, f.Provider.Resumes, "The actual coordinator must be reused.");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(old))).Kind);
        var current = f.Permission(f.Provider.SecondSendOptions!.OnPermissionRequest!);
        var handle = await f.PendingHandle();
        Assert.AreNotEqual(oldHandle.AttemptId, handle.AttemptId);
        Assert.IsFalse(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(oldHandle, AgentPermissionDecisionKind.AllowOnce).AsTask()));
        await f.Observe(f.Host.RuntimeService.Permissions.CancelAsync(handle).AsTask());
        await f.Observe(current);
        f.Provider.ReleaseSecondSend.TrySetResult();
        await f.Observe(second.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_AbortStartsWhileProviderCancellationCallbackIsHeld() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var entered = f.NewGate();
        var release = f.NewGate();
        var registration = f.Provider.SendToken.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Held provider cancellation expired.");
        });
        try
        {
            var abort = f.Abort(send);
            await f.Observe(entered.Task);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
            await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "independent abort");
            release.TrySetResult();
            f.Provider.ReleaseAbort.TrySetResult();
            f.Provider.ReleaseSend.TrySetResult();
            await f.Observe(abort.Completion);
            await f.Observe(send.Completion);
        }
        finally { release.TrySetResult(); registration.Dispose(); }
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_AbortCancelsBeforeDependentProviderJoin() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var abort = f.Abort(send);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort after permission");
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_DetachCancelsBeforeRetirementJoins() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var callback = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(callback);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var detach = f.Track(f.Host.RuntimeService.DetachRuntimeSessionAsync(f.SessionId));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(callback))).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(detach);
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_ShutdownCancelsBeforeDependentJoins(bool directRuntime) => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var callback = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(callback);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var disposal = directRuntime ? f.Track(f.Host.RuntimeService.DisposeAsync().AsTask()) : f.BeginDisposal();
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(callback))).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_CommandShutdownDoesNotDisposeTrustedTuiPermissions() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var disposal = f.Track(f.Host.Commands.DisposeAsync().AsTask());
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
        var tui = await f.Observe(f.Host.RuntimeService.Permissions.RegisterAsync(f.SessionId, f.Provider.CommandRequest(), false, CancellationToken.None));
        _ = f.Track(tui.Completion);
        Assert.IsTrue(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(tui.Snapshot.Handle, AgentPermissionDecisionKind.AllowForSession).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowForSession, (await f.Observe(tui.Completion)).Kind);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedSteer_AlongsideSendPreservesExactReplayAndIndependentSlot() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.SteerRequest();
        using var caller = new CancellationTokenSource();
        var steer = f.Accept(f.AdmitSteer(request, caller.Token));
        await f.ObserveReadiness(f.Provider.SteerStarted.Task, steer, "steer");
        caller.Cancel();
        Assert.IsFalse(f.Provider.SteerToken.IsCancellationRequested);
        Assert.IsFalse(send.Completion.IsCompleted);
        Assert.AreSame(steer, f.AdmitSteer(request).Receipt);
        foreach (var changed in new[] { request with { Text = "changed" }, request with { SessionId = "different-session" },
            request with { ExpectedRunId = "later" }, request with { ExpectedRuntimeInstanceId = Guid.NewGuid() },
            request with { ExpectedAttachmentGeneration = request.ExpectedAttachmentGeneration + 1 } })
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSteer(changed).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(new(request.ClientRequestId, f.SessionId, request.Text)).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitSteer(request with { ClientRequestId = "other" }).Kind);
        f.Provider.ReleaseSteer.TrySetResult();
        var result = await f.Observe(steer.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome);
        Assert.AreEqual(request.ExpectedRunId, result.RunId!.Value.Value);
        Assert.AreEqual(request.ExpectedRunId, f.Provider.SteerOptions!.ExpectedRunId!.Value.Value);
        Assert.IsNull(f.Provider.FirstSendOptions!.OnPermissionRequest);
    });

    [TestMethod]
    public Task OwnedSteer_ProviderBoundaryRejectsLaterRun_AndWrongReturnedRunFails() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.SteerRequest();
        var steer = f.Accept(f.AdmitSteer(request));
        await f.ObserveReadiness(f.Provider.SteerStarted.Task, steer, "captured attachment");
        // The fake's mutation-boundary check mirrors AgentSession's state-gate check, after capture.
        f.Provider.CurrentRun = "later-run";
        f.Provider.ReleaseSteer.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, (await f.Observe(steer.Completion)).Outcome);
        Assert.AreEqual(0, f.Provider.SteerDeliveries);
        f.Provider.CurrentRun = request.ExpectedRunId;
        f.Provider.WrongSteerResult = true;
        var wrong = f.Accept(f.AdmitSteer(request with { ClientRequestId = "wrong-return" }));
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, (await f.Observe(wrong.Completion)).Outcome);
    });

    [TestMethod]
    public Task OwnedSteer_CapacityAndPreCancellationDoNotDispatch() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.SteerRequest();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.AdmitSteer(request, cancelled.Token));
        var steer = f.Accept(f.AdmitSteer(request));
        await f.ObserveReadiness(f.Provider.SteerStarted.Task, steer, "steer");
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Capacity, f.AdmitSteer(request with { ClientRequestId = "over" }).Kind);
        Assert.AreSame(steer, f.AdmitSteer(request).Receipt);
        // Once it has settled, the steer makes room for the next one, and is not delivered a second time.
        f.Provider.ReleaseSteer.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(steer.Completion)).Outcome);
        var next = f.Accept(f.AdmitSteer(request with { ClientRequestId = "next" }));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(next.Completion)).Outcome);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSteer(request).Kind);
        Assert.AreSame(next, f.AdmitSteer(request with { ClientRequestId = "next" }).Receipt);
        Assert.AreEqual(2, f.Provider.SteerDeliveries);
    }, capacity: 2);

    [TestMethod]
    public Task OwnedSteer_ShutdownSignalsBeforeJoiningAndRetainsNoncooperativeWork() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.SteerRequest();
        var steer = f.Accept(f.AdmitSteer(request));
        await f.ObserveReadiness(f.Provider.SteerStarted.Task, steer, "steer");
        var disposal = f.BeginDisposal();
        var sendCancelled = f.ObserveReadiness(f.Provider.SendCancelled.Task, send, "send cancellation");
        var steerCancelled = f.ObserveReadiness(f.Provider.SteerCancelled.Task, steer, "steer cancellation");
        await sendCancelled;
        await steerCancelled;
        Assert.IsTrue(f.Provider.SendToken.IsCancellationRequested);
        Assert.IsTrue(f.Provider.SteerToken.IsCancellationRequested);
        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(steer.Completion.IsCompleted);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitSteer(request with { ClientRequestId = "closed" }).Kind);
        f.Provider.ReleaseAll();
        await f.Observe(disposal);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(steer.Completion)).Outcome);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    public Task OwnedCompact_ExactReplayCapacityCallerCancellationAndOutcomes() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportIdleCompaction = true;
        f.Provider.HoldCompact = true;
        var request = await f.PrepareCompact();
        using var caller = new CancellationTokenSource();
        var compact = f.Accept(f.AdmitCompact(request, caller.Token));
        await f.ObserveReadiness(f.Provider.CompactStarted.Task, compact, "compaction");
        caller.Cancel();
        Assert.IsFalse(f.Provider.CompactToken.IsCancellationRequested);
        Assert.AreSame(compact, f.AdmitCompact(request).Receipt);
        foreach (var changed in new[] { request with { SessionId = "different" }, request with { ExpectedRuntimeInstanceId = Guid.NewGuid() },
            request with { ExpectedAttachmentGeneration = request.ExpectedAttachmentGeneration + 1 } })
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitCompact(changed).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(new(request.ClientRequestId, request.SessionId, "text")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitCompact(request with { ClientRequestId = "busy" }).Kind);
        f.Provider.ReleaseCompact.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(compact.Completion)).Outcome);
        f.Provider.CompactOutcome = new(false, "private provider message");
        var unsuccessful = f.Accept(f.AdmitCompact(request with { ClientRequestId = "unsuccessful" }));
        var result = await f.Observe(unsuccessful.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("compact_unsuccessful", result.Code);
        f.Provider.FailCompact = true;
        var failed = f.Accept(f.AdmitCompact(request with { ClientRequestId = "failure" }));
        Assert.AreEqual("compact_failed", (await f.Observe(failed.Completion)).Code);
        // The owner is full of settled receipts: the oldest one, the send that prepared the session, makes room.
        var again = f.Accept(f.AdmitCompact(request with { ClientRequestId = "again" }));
        Assert.AreEqual("compact_failed", (await f.Observe(again.Completion)).Code);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitSend(new("send", f.SessionId, "text")).Kind);
        Assert.AreSame(compact, f.AdmitCompact(request).Receipt);
        // Then the first compaction, which is not attempted a second time.
        var compactions = f.Provider.Compactions;
        var once = f.Accept(f.AdmitCompact(request with { ClientRequestId = "once-more" }));
        Assert.AreEqual("compact_failed", (await f.Observe(once.Completion)).Code);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitCompact(request).Kind);
        Assert.AreEqual(compactions + 1, f.Provider.Compactions);
    }, capacity: 4);

    [TestMethod]
    public Task OwnedCompact_ActualHubGateRefusesWhileSendIsHeld() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportIdleCompaction = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        // This fake has not reported its run yet: runtime null is eligibility, not provider idleness.
        var state = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId));
        var compact = f.Accept(f.AdmitCompact(new("compact", f.SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration)));
        var result = await f.Observe(compact.Completion);
        Assert.AreEqual("compact_busy", result.Code);
        Assert.AreEqual(0, f.Provider.Compactions);
        Assert.IsFalse(f.Provider.CompactEntered.Task.IsCompleted);
        Assert.IsFalse(send.Completion.IsCompleted);
    });

    [TestMethod]
    public Task OwnedCompact_ProviderBoundaryRefusesRunStartedAfterCapture() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportIdleCompaction = true;
        f.Provider.HoldCompactAdmission = true;
        var request = await f.PrepareCompact();
        var compact = f.Accept(f.AdmitCompact(request));
        await f.ObserveReadiness(f.Provider.CompactEntered.Task, compact, "provider idle check");
        f.Provider.RecordRun("later-run", Guid.NewGuid().ToString("N"));
        f.Provider.ReleaseCompactAdmission.TrySetResult();
        Assert.AreEqual("compact_busy", (await f.Observe(compact.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Compactions);
    });

    [TestMethod]
    public Task OwnedCompact_UnsupportedNeverFallsBack() => Fixture.RunAsync(async f =>
    {
        var request = await f.PrepareCompact(); // Default fake deliberately does not implement the capability.
        var compact = f.Accept(f.AdmitCompact(request));
        Assert.AreEqual("compact_unsupported", (await f.Observe(compact.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Compactions);
    });

    [TestMethod]
    public Task OwnedCompact_ShutdownSignalsAndJoinsOriginalWork() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportIdleCompaction = true;
        f.Provider.HoldCompact = true;
        var request = await f.PrepareCompact();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.AdmitCompact(request, cancelled.Token));
        var compact = f.Accept(f.AdmitCompact(request));
        await f.ObserveReadiness(f.Provider.CompactStarted.Task, compact, "compaction");
        var disposal = f.BeginDisposal();
        await f.ObserveReadiness(f.Provider.CompactCancelled.Task, compact, "compact cancellation");
        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(compact.Completion.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitCompact(request with { ClientRequestId = "closed" }).Kind);
        f.Provider.ReleaseAll();
        await f.Observe(disposal);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(compact.Completion)).Outcome);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedRunLifecycle_ActualSendWiringCancelsMatchingAndNullRunWithoutTouchingTrustedOrLater(bool includeRun) => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportRunLifecycle = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "bound send");
        var options = f.Provider.FirstSendOptions!;
        Assert.IsNotNull(options.RunLifecycle);
        Assert.AreEqual(1, f.Provider.RunBindings);
        var permissions = f.Host.RuntimeService.Permissions;
        var request = f.Provider.CommandRequest() with { RunId = includeRun ? new AgentRunId("owned-run-1") : null };
        var pending = f.Track(options.OnPermissionRequest!(request, CancellationToken.None));
        var page = await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        var handle = page.Entries.Single().Handle;
        var mismatch = f.Track(options.OnPermissionRequest!(request with { RunId = new("different-run") }, CancellationToken.None));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(mismatch)).Kind);
        var trusted = await f.Observe(permissions.RegisterAsync(f.SessionId, request with { InteractionId = "trusted" }, false, CancellationToken.None));
        _ = f.Track(trusted.Completion);
        var cancellation = f.Track(f.Provider.CancelBoundRun());
        Assert.IsTrue(f.Provider.BoundRunToken.IsCancellationRequested);
        Assert.IsFalse(await f.Observe(permissions.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.HasCount(0, (await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask())).Entries);
        await f.Observe(cancellation);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        Assert.IsFalse(trusted.Completion.IsCompleted);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
        Assert.AreEqual(1, f.Provider.RunClosures);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(options.OnPermissionRequest!))).Kind);
        var later = f.Send("later");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, later, "later binding");
        var current = f.Permission(f.Provider.SecondSendOptions!.OnPermissionRequest!);
        var currentPage = await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        Assert.IsTrue(await f.Observe(permissions.ResolveOwnedCommandAsync(currentPage.Entries.Single().Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(current)).Kind);
        // Already accepted decision is not revoked by subsequent run cancellation.
        await f.Observe(f.Track(f.Provider.CancelBoundRun()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(current)).Kind);
        Assert.IsTrue(await f.Observe(permissions.CancelAsync(trusted.Snapshot.Handle).AsTask()));
        await f.Observe(trusted.Completion);
        f.Provider.ReleaseAll();
        await f.Observe(later.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task AbortRun_ReplayConflictsIndependentSlotAndSharedCapacity() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportExactAbort = true;
        f.Provider.HoldExactAbort = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "exact send");
        var steerRequest = await f.SteerRequest();
        var steer = f.Accept(f.AdmitSteer(steerRequest));
        await f.ObserveReadiness(f.Provider.SteerStarted.Task, steer, "independent steer");
        var request = await f.AbortRunRequest();
        using var caller = new CancellationTokenSource();
        var abort = f.Accept(f.AdmitAbortRun(request, caller.Token));
        await f.ObserveReadiness(f.Provider.ExactAbortStarted.Task, abort, "exact cancellation");
        var cancellation = f.Track(caller.CancelAsync());
        await f.Observe(cancellation);
        Assert.IsFalse(abort.Completion.IsCompleted);
        Assert.IsFalse(f.Provider.ExactCallerCancelled.Task.IsCompleted, "Accepted caller cancellation must not cancel the owned execution.");
        Assert.AreSame(abort, f.AdmitAbortRun(request).Receipt);
        foreach (var changed in new[] { request with { SessionId = "other" }, request with { ExpectedRuntimeInstanceId = Guid.NewGuid() },
            request with { ExpectedAttachmentGeneration = request.ExpectedAttachmentGeneration + 1 }, request with { ExpectedRunId = "other" } })
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitAbortRun(changed).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitAbort(new(request.ClientRequestId, send.OperationId)).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(new(request.ClientRequestId, f.SessionId, "other kind")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSteer(steerRequest with { ClientRequestId = request.ClientRequestId }).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitCompact(new(request.ClientRequestId, f.SessionId,
            request.ExpectedRuntimeInstanceId, request.ExpectedAttachmentGeneration)).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitAbortRun(request with { ClientRequestId = "busy" }).Kind);
        f.Provider.ReleaseExactAbort.TrySetResult();
        Assert.AreEqual("cancellation_signalled", (await f.Observe(abort.Completion)).Code);
        var stale = f.Accept(f.AdmitAbortRun(request with { ClientRequestId = "stale", ExpectedRunId = "not-active" }));
        Assert.AreEqual("abort_run_not_active", (await f.Observe(stale.Completion)).Code);
        // The send and the steer are pending, the two cancellations have settled: the older one makes room.
        var more = f.Accept(f.AdmitAbortRun(request with { ClientRequestId = "more", ExpectedRunId = "not-active" }));
        Assert.AreEqual("abort_run_not_active", (await f.Observe(more.Completion)).Code);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitAbortRun(request).Kind);
        Assert.AreSame(stale, f.AdmitAbortRun(request with { ClientRequestId = "stale", ExpectedRunId = "not-active" }).Receipt);
        Assert.AreEqual(1, f.Provider.ExactTraversals);
        Assert.AreEqual(1, f.Provider.ExactAdmissions);
        f.Provider.ReleaseAll();
        await f.Observe(send.Completion);
        await f.Observe(steer.Completion);
        await f.Observe(f.BeginDisposal());
        // A closed owner still answers the keys it knows: the kept ones with their receipt, the others as expired.
        Assert.AreSame(more, f.AdmitAbortRun(request with { ClientRequestId = "more", ExpectedRunId = "not-active" }).Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Expired, f.AdmitAbortRun(request).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitAbortRun(request with { ClientRequestId = "closed" }).Kind);
    }, capacity: 4);

    [TestMethod]
    public Task AbortRun_ValidationPreCancellationAndUnsupportedDoNotInvalidate() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportRunLifecycle = true; // Legacy session deliberately lacks the exact capability.
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "legacy send");
        var request = await f.AbortRunRequest();
        foreach (var invalid in new[] { request with { ClientRequestId = "\ud800" }, request with { SessionId = " x" },
            request with { ExpectedRunId = "\udfff" }, request with { ExpectedRunId = new string('r', 257) },
            request with { ExpectedRuntimeInstanceId = Guid.Empty }, request with { ExpectedAttachmentGeneration = 0 } })
            Assert.Throws<ArgumentException>(() => f.AdmitAbortRun(invalid));
        using var caller = new CancellationTokenSource();
        await f.Observe(f.Track(caller.CancelAsync()));
        Assert.Throws<OperationCanceledException>(() => f.AdmitAbortRun(request, caller.Token));
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        var page = await f.Observe(f.Host.RuntimeService.Permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        var handle = page.Entries.Single().Handle;
        var abort = f.Accept(f.AdmitAbortRun(request));
        Assert.AreEqual("abort_run_unsupported", (await f.Observe(abort.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Aborts);
        Assert.IsFalse(f.Provider.BoundRunToken.IsCancellationRequested);
        Assert.IsTrue(await f.Observe(f.Host.RuntimeService.Permissions.ResolveOwnedCommandAsync(handle,
            AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(pending)).Kind);
    }, reviewPermissions: true);

    [TestMethod]
    public Task AbortRun_StaleALeavesBReviewsAndMatchingBCancelsOnlyBoundAuthority() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportExactAbort = true;
        var a = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, a, "run A");
        var requestA = await f.AbortRunRequest();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(a.Completion);
        var b = f.Send("send-b");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, b, "run B");
        var permissions = f.Host.RuntimeService.Permissions;
        var handler = f.Provider.SecondSendOptions!.OnPermissionRequest!;
        var command = f.Provider.CommandRequest();
        var matching = f.Track(handler(command with { RunId = new("owned-run-2"), InteractionId = "matching" }, CancellationToken.None));
        var nullRun = f.Track(handler(command with { InteractionId = "null-run" }, CancellationToken.None));
        var accepted = f.Track(handler(command with { InteractionId = "already-accepted" }, CancellationToken.None));
        var trusted = await f.Observe(permissions.RegisterAsync(f.SessionId, command with { InteractionId = "trusted-abort-run" }, false, CancellationToken.None));
        _ = f.Track(trusted.Completion);
        var stale = f.Accept(f.AdmitAbortRun(requestA));
        Assert.AreEqual("abort_run_not_active", (await f.Observe(stale.Completion)).Code);
        Assert.IsFalse(matching.IsCompleted);
        Assert.IsFalse(nullRun.IsCompleted);
        var page = await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        Assert.HasCount(3, page.Entries);
        var acceptedHandle = page.Entries.Single(entry => entry.Handle.Attempt.InteractionId == "already-accepted").Handle;
        Assert.IsTrue(await f.Observe(permissions.ResolveOwnedCommandAsync(acceptedHandle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(accepted)).Kind);
        var abort = f.Accept(f.AdmitAbortRun(requestA with { ClientRequestId = "cancel-b", ExpectedRunId = "owned-run-2" }));
        Assert.AreEqual("cancellation_signalled", (await f.Observe(abort.Completion)).Code);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(matching)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(nullRun)).Kind);
        foreach (var entry in page.Entries)
            Assert.IsFalse(await f.Observe(permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(accepted)).Kind);
        Assert.IsFalse(trusted.Completion.IsCompleted);
        Assert.IsTrue(await f.Observe(permissions.CancelAsync(trusted.Snapshot.Handle).AsTask()));
        await f.Observe(trusted.Completion);
        Assert.AreEqual(1, f.Provider.ExactTraversals);
    }, reviewPermissions: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AbortRun_CapturedTraversalSurvivesRetirementOrOwnerShutdown(bool shutdown) => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportExactAbort = true;
        f.Provider.HoldExactAbort = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.AbortRunRequest();
        var abort = f.Accept(f.AdmitAbortRun(request));
        await f.ObserveReadiness(f.Provider.ExactAbortStarted.Task, abort, "traversal");
        var closing = f.Track(shutdown ? f.Host.Commands.DisposeAsync().AsTask()
            : f.Host.RuntimeService.DetachRuntimeSessionAsync(f.SessionId));
        await f.Observe(f.Provider.ExactCallerCancelled.Task);
        Assert.IsFalse(abort.Completion.IsCompleted);
        Assert.IsFalse(closing.IsCompleted);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        if (shutdown) Assert.AreSame(closing, f.Host.Commands.DisposeAsync().AsTask());
        else
        {
            var refused = f.Track(f.Host.RuntimeService.AbortRunOwnedCommandAsync(request, CancellationToken.None));
            Assert.IsNull(await f.Observe(refused));
        }
        f.Provider.ReleaseAll();
        Assert.AreEqual("cancellation_signalled", (await f.Observe(abort.Completion)).Code);
        await f.Observe(closing);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        Assert.AreEqual(1, f.Provider.Resumes);
    });

    [TestMethod]
    public Task AbortRun_FailureAfterSignallingIsBoundedAndReplayDoesNotRepeat() => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportExactAbort = true;
        f.Provider.FailExactAbort = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.AbortRunRequest();
        var abort = f.Accept(f.AdmitAbortRun(request));
        var result = await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("abort_run_failed", result.Code);
        Assert.IsTrue(f.Provider.BoundRunToken.IsCancellationRequested);
        Assert.AreSame(abort, f.AdmitAbortRun(request).Receipt);
        Assert.AreEqual(1, f.Provider.ExactTraversals);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AbortRun_RetirementOrShutdownAfterCaptureBeforeProviderAdmissionNeverRecaptures(bool shutdown) => Fixture.RunAsync(async f =>
    {
        f.Provider.SupportExactAbort = true;
        f.Provider.HoldExactAdmission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var request = await f.AbortRunRequest();
        var abort = f.Accept(f.AdmitAbortRun(request));
        await f.ObserveReadiness(f.Provider.ExactEntered.Task, abort, "captured provider entry");
        var closing = f.Track(shutdown ? f.Host.Commands.DisposeAsync().AsTask()
            : f.Host.RuntimeService.DetachRuntimeSessionAsync(f.SessionId));
        await f.Observe(f.Provider.ExactCallerCancelled.Task);
        Assert.IsFalse(closing.IsCompleted);
        Assert.IsFalse(abort.Completion.IsCompleted);
        Assert.AreEqual(0, f.Provider.ExactAdmissions);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        Assert.AreSame(abort, f.AdmitAbortRun(request).Receipt);
        if (shutdown)
        {
            Assert.AreSame(closing, f.Host.Commands.DisposeAsync().AsTask());
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitAbortRun(request with { ClientRequestId = "closed-before-admission" }).Kind);
        }
        else
            Assert.IsNull(await f.Observe(f.Track(f.Host.RuntimeService.AbortRunOwnedCommandAsync(request, CancellationToken.None))));
        f.Provider.ReleaseAll();
        var result = await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("abort_run_failed", result.Code);
        await f.Observe(closing);
        Assert.AreSame(abort, f.AdmitAbortRun(request).Receipt);
        Assert.AreEqual(0, f.Provider.ExactAdmissions);
        Assert.AreEqual(1, f.Provider.Resumes);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    // Constructed only inside a selected real-route test. All gates and tasks are instance-owned.
    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<Task> _observers = [];
        private readonly List<Exception> _observedFaults = [];
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Exception> _failures = [];
        private readonly List<Task> _cleanupWaits = [];
        private readonly List<Task<bool>> _cleanupWorkers = [];
        private Task? _setup;
        private Task? _body;
        private Task? _disposal;
        private volatile CodeAltaHost? _host;
        private volatile bool _cleaning;
        private bool _ownsRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-" + Guid.NewGuid().ToString("N"));
        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal string ProjectRoot => Path.Combine(_root, "project");
        internal ControlledProvider Provider { get; } = new();
        internal CodeAltaHost Host => _host ?? throw new InvalidOperationException("Host setup has not completed.");

        internal static async Task RunAsync(Func<Fixture, Task> body, int capacity = 256, bool holdPreparation = false, bool reviewPermissions = false)
        {
            var fixture = new Fixture();
            try
            {
                fixture._setup = fixture.Track(fixture.SetupAsync(capacity, holdPreparation, reviewPermissions));
                await fixture.Observe(fixture._setup);
                var launch = fixture.NewGate();
                fixture._body = fixture.Track(fixture.RunBodyAsync(body, launch.Task));
                launch.TrySetResult();
                await fixture.Observe(fixture._body);
            }
            catch (Exception ex)
            {
                fixture._failures.Add(ex);
            }
            finally
            {
                await fixture.CleanupAsync();
            }
            if (fixture._failures.Count > 0)
            {
                var failure = new AggregateException("Owned fixture failures; roots retained at " + fixture._root, fixture._failures);
                failure.Data["RetainedFixture"] = fixture;
                throw failure;
            }
        }

        private async Task RunBodyAsync(Func<Fixture, Task> body, Task launch)
        {
            await launch.ConfigureAwait(false);
            await Track(body(this)).ConfigureAwait(false);
        }

        private async Task SetupAsync(int capacity, bool holdPreparation, bool reviewPermissions)
        {
            // Parent must admit these runtime I/O routes only after auditing this complete fixture.
            // Check existing ancestry before side effects; this is not a reparse-race sandbox.
            RejectReparseAncestors(Path.GetDirectoryName(_root)!);
            if (Directory.Exists(_root) || File.Exists(_root))
                throw new InvalidOperationException("The new fixture root already exists.");
            Directory.CreateDirectory(_root);
            _ownsRoot = true;
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var directory in new[] { home, global, ProjectRoot, builtin }) Directory.CreateDirectory(directory);
            // Only builtin exists as a skill root; other configured skill directories remain absent.
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            var skill = Path.Combine(builtin, "fixture-builtin");
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: fixture-builtin\ndescription: Owned fixture skill.\n---\nFixture text only.\n");
            var catalogOptions = new CatalogOptions { GlobalRoot = global };
            var projectTask = Track(new ProjectCatalog(catalogOptions).UpsertFromPathAsync(ProjectRoot, CancellationToken.None));
            var project = await projectTask.ConfigureAwait(false);
            var session = new SessionViewDescriptor
            {
                SessionId = SessionId,
                Kind = SessionViewKind.ProjectSession,
                ProjectRef = project.Id,
                ProviderId = Provider.Descriptor.ProviderId.Value,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = ProjectRoot,
                Title = "Owned fixture",
                ModelId = "fixture-model",
                AgentPromptId = "default",
                CreatedAt = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
            };
            var journal = new SessionViewJournalStore(catalogOptions);
            var header = Track(journal.EnsureHeaderAsync(session, CancellationToken.None));
            await header.ConfigureAwait(false);
            // Header/state cache writes only update rows; a genuine summary creates the row.
            var store = journal.CreateSessionStore();
            var summary = Track(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId,
                ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = session.WorkingDirectory,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await summary.ConfigureAwait(false);
            var state = Track(journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await state.ConfigureAwait(false);
            var readback = Track(store.GetSessionAsync(SessionId, CancellationToken.None));
            var metadata = await readback.ConfigureAwait(false);
            Assert.IsNotNull(metadata);
            Assert.AreEqual(session.SessionId, metadata.SessionId);
            Assert.AreEqual(session.WorkingDirectory, metadata.WorkspacePath);
            Assert.AreEqual(session.CreatedAt, metadata.CreatedAt);
            Assert.AreEqual(session.ProviderKey, metadata.ProviderKey);
            Assert.AreEqual(session.ModelId, metadata.ModelId);
            Assert.AreEqual(session.ReasoningEffort, metadata.ReasoningEffort);
            Assert.AreEqual(session.AgentPromptId, metadata.AgentPromptId);
            Assert.IsNotNull(metadata.ViewState);
            Assert.AreEqual(session.ProviderKey, metadata.ViewState.ProviderKey);
            Assert.AreEqual(session.ModelId, metadata.ViewState.ModelId);
            Assert.AreEqual(session.ReasoningEffort, metadata.ViewState.ReasoningEffort);
            Assert.AreEqual(session.AgentPromptId, metadata.ViewState.AgentPromptId);
            if (!holdPreparation) Provider.ReleasePreparation.TrySetResult();
            var creation = Track(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global,
                CurrentProjectPath = ProjectRoot,
                DiscoveryScope = new SessionDiscoveryScope(home, _root),
                BuiltInSkillRoot = builtin,
                OwnedCommandReceiptCapacity = capacity,
                ReviewOwnedCommandPermissions = reviewPermissions,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false,
                OwnsLogging = false,
                IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime),
            }, CancellationToken.None));
            _host = await creation.ConfigureAwait(false);
            // A setup observer may already have timed out. Never publish an unowned late host.
            if (_cleaning) await BeginDisposal().ConfigureAwait(false);
        }

        private static void RejectReparseAncestors(string root)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture ancestry contains a reparse node.");
        }

        internal OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken = default)
            => RetainAdmission(Host.Commands.AdmitSend(request, cancellationToken));

        internal OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request)
            => RetainAdmission(Host.Commands.AdmitAbort(request));

        internal OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken = default)
            => RetainAdmission(Host.Commands.AdmitSteer(request, cancellationToken));

        internal OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken = default)
            => RetainAdmission(Host.Commands.AdmitCompact(request, cancellationToken));

        internal OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken = default)
            => RetainAdmission(Host.Commands.AdmitAbortRun(request, cancellationToken));

        internal async Task<OwnedAbortRunRequest> AbortRunRequest()
        {
            var state = await Observe(Host.RuntimeService.GetCurrentStateAsync(SessionId));
            return new("abort-run", SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration, "owned-run-1");
        }

        internal async Task<OwnedCompactRequest> PrepareCompact()
        {
            var send = Send();
            await ObserveReadiness(Provider.SendStarted.Task, send, "send");
            Provider.ReleaseSend.TrySetResult();
            await Observe(send.Completion);
            var marker = Guid.NewGuid().ToString("N");
            Provider.RecordIdle(marker);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Track(Committed());
            var state = await Observe(Host.RuntimeService.GetCurrentStateAsync(SessionId));
            Assert.IsNull(state.Entry!.ActiveRunId);
            return new("compact", SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration);
            async Task Committed()
            {
                await foreach (var value in Host.RuntimeService.StreamEventsAsync(timeout.Token))
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent update }
                        && update.Kind == AgentSessionUpdateKind.Idle && update.Message == marker) return;
                Assert.Fail("Runtime closed before the exact fake idle event committed.");
            }
        }

        internal async Task<OwnedTextSteerRequest> SteerRequest()
        {
            var marker = Guid.NewGuid().ToString("N");
            Provider.RecordRun("owned-run-1", marker);
            // Display is only a commit-readiness signal, never target authority. Join the complete
            // observation (including iterator disposal) before releasing its timeout source.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Track(Committed());
            var state = await Observe(Host.RuntimeService.GetCurrentStateAsync(SessionId));
            return new("steer", SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration, "owned-run-1", "exact steer text");
            async Task Committed()
            {
                await foreach (var replacement in Host.RuntimeService.Display.ObserveAsync(timeout.Token))
                {
                    if (replacement.Snapshot.Sessions.Any(session => session.SessionId == SessionId
                        && session.Text.Any(text => text.ContentId == marker && text.Text == "inert steering readiness"))) return;
                }
                Assert.Fail("Display closed before the fake run event committed.");
            }
        }

        private OwnedSessionCommandAdmission RetainAdmission(OwnedSessionCommandAdmission admission)
        {
            // Retain even an unexpectedly accepted receipt before any rejection/replay assertion.
            if (admission.Receipt is not null) Track(admission.Receipt.Completion);
            return admission;
        }

        internal OwnedSessionCommandReceipt Send(string id = "send") => Accept(AdmitSend(new(id, SessionId, "text")));
        internal Task<AgentPermissionDecision> Permission(AgentPermissionRequestHandler handler)
            => Track(handler(Provider.CommandRequest(), CancellationToken.None));

        internal async Task<SessionPermissionHandle> PendingHandle()
        {
            var pending = await Observe(Host.RuntimeService.Permissions.ListAsync().AsTask());
            return pending.Single().Handle;
        }
        internal OwnedSessionCommandReceipt Abort(OwnedSessionCommandReceipt send, string id = "abort")
            => Accept(AdmitAbort(new(id, send.OperationId)));

        internal OwnedSessionCommandReceipt Accept(OwnedSessionCommandAdmission admission)
        {
            if (admission.Receipt is not null) Track(admission.Receipt.Completion);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            Assert.IsNotNull(admission.Receipt);
            return admission.Receipt;
        }

        internal TaskCompletionSource NewGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _gates.Add(gate);
                if (_cleaning) gate.TrySetResult();
            }
            return gate;
        }

        internal Task Track(Task task)
        {
            lock (_gate)
            {
                _tasks.Add(task);
                _observers.Add(ObserveFaultAsync(task));
            }
            return task;
        }

        internal Task<T> Track<T>(Task<T> task)
        {
            Track((Task)task);
            return task;
        }

        private async Task ObserveFaultAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _observedFaults.Add(ex); }
        }

        internal Task Observe(Task task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Observe<T>(Task<T> task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));

        internal Task ObserveReadiness(Task readiness, OwnedSessionCommandReceipt receipt, string stage)
            => Track(ObserveReadinessAsync(readiness, receipt, stage));

        private async Task ObserveReadinessAsync(Task readiness, OwnedSessionCommandReceipt receipt, string stage)
        {
            // Notifications are not executable work: failed preparation may never signal them.
            // Retain the actual race and bounded observer, but not the bare notification.
            var completion = Track(receipt.Completion);
            var race = Track(Task.WhenAny(readiness, completion));
            var wait = Track(race.WaitAsync(TimeSpan.FromSeconds(5)));
            try { await wait.ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException($"Provider readiness timed out at {stage}."); }
            if (readiness.IsCompletedSuccessfully) return;
            // Receipt contracts expose stable results, never raw owner exception details.
            Assert.IsTrue(completion.IsCompletedSuccessfully, $"No successful readiness or receipt result at {stage}.");
            var result = await completion.ConfigureAwait(false);
            Assert.Fail($"Receipt completed before provider readiness at {stage}: Outcome={result.Outcome}; Code={result.Code}.");
        }

        internal Task BeginDisposal()
        {
            lock (_gate) return _disposal ??= Track(Host.DisposeAsync().AsTask());
        }

        private async Task CleanupAsync()
        {
            _cleaning = true;
            Provider.ReleaseAll();
            lock (_gate) foreach (var gate in _gates) gate.TrySetResult();
            var confirmed = true;
            if (_setup is not null) confirmed &= await DrainBatchAsync([_setup]).ConfigureAwait(false);
            var finalWork = new List<Task>();
            if (_host is not null) finalWork.Add(BeginDisposal());
            if (_body is not null) finalWork.Add(_body);
            confirmed &= await DrainBatchAsync(finalWork).ConfigureAwait(false);
            Task[] tasks;
            lock (_gate) tasks = [.. _tasks, .. _observers];
            confirmed &= await DrainBatchAsync(tasks).ConfigureAwait(false);
            lock (_gate)
                foreach (var fault in _observedFaults)
                    if (!_failures.Contains(fault)) _failures.Add(fault);
            if (confirmed && _failures.Count == 0 && _ownsRoot)
            {
                try
                {
                    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                }
                catch (Exception ex) { _failures.Add(ex); }
            }
        }

        private async Task<bool> DrainBatchAsync(IEnumerable<Task> tasks)
        {
            // Start independent observations together; cleanup does not grow _observers.
            var workers = tasks.Distinct().Select(DrainAsync).ToArray();
            _cleanupWorkers.AddRange(workers);
            var confirmed = true;
            foreach (var worker in workers) confirmed &= await worker.ConfigureAwait(false);
            return confirmed;
        }

        private async Task<bool> DrainAsync(Task task)
        {
            // Each task has its own timeout, not one already-expired shared cleanup token.
            var observer = task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (_gate) _cleanupWaits.Add(observer);
            try { await observer.ConfigureAwait(false); return true; }
            catch (Exception ex)
            {
                lock (_gate) _failures.Add(ex);
                // A faulted disposal does not prove that dependencies terminated all their work.
                return false;
            }
        }
    }

    private sealed class ControlledProvider
    {
        private int _runtimeStarts, _resumes, _creates, _sends, _aborts, _runtimeDisposals, _earlyDisposals, _active;
        private int _runtimeStops, _sessionDisposals;
        internal ControlledProvider(string providerId = "owned-fixture")
            => Descriptor = new(new ModelProviderId(providerId), "Owned fixture") { DefaultModelId = "fixture-model" };
        internal bool ResumeNotFound { get; set; }
        internal bool FailPreparation { get; set; }
        internal bool FailSend { get; set; }
        internal bool RequestInteractions { get; set; }
        internal bool RequestPreparationPermission { get; set; }
        internal bool RequestPerSendPermission { get; set; }
        internal string? CurrentRun { get; set; }
        internal bool WrongSteerResult { get; set; }
        internal bool SupportIdleCompaction { get; set; }
        internal bool HoldCompactAdmission { get; set; }
        internal bool HoldCompact { get; set; }
        internal bool FailCompact { get; set; }
        internal AgentCompactionOutcome CompactOutcome { get; set; } = new(true, "inert completed compaction");
        internal CancellationToken CompactToken { get; private set; }
        private int _compactions;
        internal int Compactions => Volatile.Read(ref _compactions);
        internal TaskCompletionSource CompactEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCompactAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CompactStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CompactCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCompact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AgentSteerOptions? SteerOptions { get; private set; }
        internal CancellationToken SteerToken { get; private set; }
        internal int SteerDeliveries { get; private set; }
        private Action<AgentEvent>? _handler;
        internal void RecordRun(string run, string marker)
        {
            CurrentRun = run;
            _handler!(new AgentSessionUpdateEvent(Descriptor.ProviderId, LastSessionId!, DateTimeOffset.UtcNow,
                new AgentRunId(run), AgentSessionUpdateKind.Warning, "inert recorded run"));
            _handler!(new AgentContentCompletedEvent(Descriptor.ProviderId, LastSessionId!, DateTimeOffset.UtcNow,
                new AgentRunId(run), AgentContentKind.Notice, marker, null, "inert steering readiness"));
        }
        internal void RecordIdle(string marker)
        {
            CurrentRun = null;
            _handler!(new AgentSessionUpdateEvent(Descriptor.ProviderId, LastSessionId!, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, marker));
        }
        internal TaskCompletionSource SteerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SteerCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SendCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSteer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<AgentPermissionDecision>? SendPermission { get; private set; }
        internal bool SupportRunLifecycle { get; set; }
        internal bool SupportExactAbort { get; set; }
        internal bool HoldExactAbort { get; set; }
        internal bool HoldExactAdmission { get; set; }
        internal bool FailExactAbort { get; set; }
        internal int ExactTraversals;
        internal int ExactAdmissions;
        internal TaskCompletionSource ExactEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseExactAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExactAbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExactCallerCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseExactAbort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RunBindings { get; private set; }
        internal int RunClosures { get; private set; }
        internal CancellationToken BoundRunToken { get; private set; }
        private CancellationTokenSource? _boundRunSource;
        private Task _boundRunCancellation = Task.CompletedTask;
        internal Task CancelBoundRun() => _boundRunCancellation = _boundRunSource!.CancelAsync();
        internal AgentPermissionDecisionKind? PreparationDecision { get; private set; }
        internal AgentSendOptions? FirstSendOptions { get; private set; }
        internal AgentSendOptions? SecondSendOptions { get; private set; }
        internal Task? AbortDependency { get; set; }
        internal AgentCommandPermissionRequest CommandRequest() => new(Descriptor.ProviderId, LastSessionId!, DateTimeOffset.UtcNow,
            null, "owned-permission", null, "inert fixture command", Options!.WorkingDirectory, null, "fixture", null, null, null);
        internal TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecondSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAbort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RuntimeStarts => Volatile.Read(ref _runtimeStarts);
        internal int Resumes => Volatile.Read(ref _resumes);
        internal int Creates => Volatile.Read(ref _creates);
        internal int Sends => Volatile.Read(ref _sends);
        internal int Aborts => Volatile.Read(ref _aborts);
        internal int RuntimeDisposals => Volatile.Read(ref _runtimeDisposals);
        internal int RuntimeStops => Volatile.Read(ref _runtimeStops);
        internal int SessionDisposals => Volatile.Read(ref _sessionDisposals);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal string? LastSessionId { get; private set; }
        internal AgentSessionCreateOptions? Options { get; private set; }
        internal AgentInput? Input { get; private set; }
        internal CancellationToken SendToken { get; private set; }
        internal AgentPermissionDecisionKind? PermissionDecision { get; private set; }
        internal bool InputCancelled { get; private set; }
        public ModelProviderDescriptor Descriptor { get; }
        public Task StartAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _runtimeStarts); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _runtimeStops); return Task.CompletedTask; }
        internal bool ExposeSelectionModels { get; set; }
        internal Task? ProbeDependency { get; set; }
        internal TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            if (!ExposeSelectionModels) throw new InvalidOperationException("Unexpected provider probe.");
            ProbeStarted.TrySetResult();
            if (ProbeDependency is { } dependency) await dependency.ConfigureAwait(false);
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId,
                Models = [new("fixture-model"), new("selected-model", SupportedReasoningEfforts: [AgentReasoningEffort.High]),
                    new("image-model", Capabilities: new Dictionary<string, object?> { ["supportsImageInput"] = true }),
                    new("text-model", Capabilities: new Dictionary<string, object?> { ["supportsImageInput"] = false })] };
        }
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Unexpected turn-executor route.");
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _creates);
            return PrepareAsync(options.SessionId ?? throw new InvalidOperationException("Missing preserved session ID."), options);
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resumes);
            if (ResumeNotFound) throw new KeyNotFoundException("Controlled resume miss.");
            return PrepareAsync(sessionId, options);
        }

        private async Task<IAgentSession> PrepareAsync(string sessionId, AgentSessionCreateOptions options)
        {
            Interlocked.Increment(ref _active);
            try
            {
                LastSessionId = sessionId;
                Options = options;
                if (RequestPreparationPermission)
                    PreparationDecision = (await options.OnPermissionRequest(CommandRequest(), CancellationToken.None).ConfigureAwait(false)).Kind;
                PreparationStarted.TrySetResult();
                await ReleasePreparation.Task.ConfigureAwait(false);
                if (FailPreparation) throw new InvalidOperationException("Controlled preparation failure.");
                return SupportExactAbort ? new ExactSession(this, sessionId)
                    : SupportIdleCompaction ? new CompactSession(this, sessionId) : new Session(this, sessionId);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _runtimeDisposals);
            if (Volatile.Read(ref _active) != 0) Interlocked.Increment(ref _earlyDisposals);
            return ValueTask.CompletedTask;
        }

        internal void ReleaseAll()
        {
            ReleasePreparation.TrySetResult();
            ReleaseSend.TrySetResult();
            ReleaseSecondSend.TrySetResult();
            ReleaseAbort.TrySetResult();
            ReleaseSteer.TrySetResult();
            ReleaseCompactAdmission.TrySetResult();
            ReleaseCompact.TrySetResult();
            ReleaseExactAbort.TrySetResult();
            ReleaseExactAdmission.TrySetResult();
        }

        // A fresh runtime for every real registry factory call, including resume-fallback creation.
        private sealed class Runtime(ControlledProvider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => owner.StartAsync(cancellationToken);
            public Task StopAsync(CancellationToken cancellationToken = default) => owner.StopAsync(cancellationToken);
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => owner.ProbeAsync(cancellationToken);
            public IModelProviderTurnExecutor CreateTurnExecutor() => owner.CreateTurnExecutor();
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
                => owner.CreateSessionAsync(options, cancellationToken);
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
                => owner.ResumeSessionAsync(sessionId, options, cancellationToken);
            public ValueTask DisposeAsync() => owner.DisposeAsync();
        }

        private class Session(ControlledProvider owner, string sessionId) : IAgentSession
        {
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => sessionId;
            public string? WorkspacePath => owner.Options?.WorkingDirectory;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }
            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                owner._handler = handler;
                return new Subscription(() => owner._handler = null);
            }
            public virtual async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._active);
                CancellationTokenSource? runSource = null;
                AgentRunId? boundRun = null;
                try
                {
                    var send = Interlocked.Increment(ref owner._sends);
                    owner.Input = options.Input;
                    owner.SendToken = cancellationToken;
                    using var cancellationReadiness = cancellationToken.Register(() => owner.SendCancelled.TrySetResult());
                    if (send == 1) owner.FirstSendOptions = options;
                    else owner.SecondSendOptions = options;
                    if (owner.SupportRunLifecycle)
                    {
                        runSource = new CancellationTokenSource();
                        owner._boundRunSource = runSource;
                        owner._boundRunCancellation = Task.CompletedTask;
                        owner.BoundRunToken = runSource.Token;
                        boundRun = new AgentRunId("owned-run-" + send);
                        await options.RunLifecycle!.StartedAsync(boundRun.Value, runSource.Token).ConfigureAwait(false);
                        owner.RunBindings++;
                    }
                    if (owner.RequestInteractions)
                    {
                        var permission = owner.Options!.OnPermissionRequest(new AgentGenericPermissionRequest(ProviderId, SessionId, DateTimeOffset.UtcNow, null, "permission", "fixture", default), cancellationToken);
                        owner.PermissionDecision = (await permission.ConfigureAwait(false)).Kind;
                        var input = owner.Options.OnUserInputRequest!(new AgentUserInputRequest(ProviderId, SessionId, DateTimeOffset.UtcNow, null, "input", new AgentUserInputForm([])), cancellationToken);
                        try { await input.ConfigureAwait(false); }
                        catch (OperationCanceledException) { owner.InputCancelled = true; }
                    }
                    if (owner.RequestPerSendPermission)
                        owner.SendPermission = (options.OnPermissionRequest ?? owner.Options!.OnPermissionRequest)(owner.CommandRequest(), CancellationToken.None);
                    (send == 1 ? owner.SendStarted : owner.SecondSendStarted).TrySetResult();
                    if (owner.SendPermission is { } sendPermission)
                        owner.PermissionDecision = (await sendPermission.ConfigureAwait(false)).Kind;
                    await (send == 1 ? owner.ReleaseSend.Task : owner.ReleaseSecondSend.Task).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (owner.FailSend) throw new InvalidOperationException("Controlled provider fault.");
                    return new AgentRunId("owned-run-" + send);
                }
                finally
                {
                    try
                    {
                        if (boundRun is { } run)
                        {
                            try
                            {
                                await options.RunLifecycle!.ClosingAsync(run).ConfigureAwait(false);
                                owner.RunClosures++;
                            }
                            finally { await owner._boundRunCancellation.ConfigureAwait(false); }
                        }
                    }
                    finally { runSource?.Dispose(); Interlocked.Decrement(ref owner._active); }
                }
            }
            public virtual async Task AbortAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._active);
                try
                {
                    Interlocked.Increment(ref owner._aborts);
                    if (owner.AbortDependency is { } dependency) await dependency.ConfigureAwait(false);
                    owner.AbortStarted.TrySetResult();
                    await owner.ReleaseAbort.Task.ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public async Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._active);
                try
                {
                    owner.SteerOptions = options;
                    owner.SteerToken = cancellationToken;
                    using var cancellationReadiness = cancellationToken.Register(() => owner.SteerCancelled.TrySetResult());
                    owner.SteerStarted.TrySetResult();
                    await owner.ReleaseSteer.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.ExpectedRunId?.Value != owner.CurrentRun || owner.CurrentRun is null)
                        throw new InvalidOperationException("Exact run no longer active.");
                    owner.SteerDeliveries++;
                    return new AgentRunId(owner.WrongSteerResult ? "wrong-returned-run" : owner.CurrentRun);
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected compaction.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._sessionDisposals);
                if (Volatile.Read(ref owner._active) != 0) Interlocked.Increment(ref owner._earlyDisposals);
                return ValueTask.CompletedTask;
            }
        }

        // Each send owns its original source/worker. No mutable latest-CTS cleanup or real tools.
        private sealed class ExactSession : Session, IAgentTargetedAbortProvider
        {
            private readonly object _gate = new();
            private readonly ControlledProvider _owner;
            private ExactRun? _run;
            internal ExactSession(ControlledProvider owner, string sessionId) : base(owner, sessionId) => _owner = owner;
            public override async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                var owner = _owner;
                Interlocked.Increment(ref owner._active);
                var number = Interlocked.Increment(ref owner._sends);
                var run = new ExactRun(owner, new("owned-run-" + number));
                lock (_gate) _run = run;
                owner.BoundRunToken = run.Source.Token;
                if (number == 1) owner.FirstSendOptions = options; else owner.SecondSendOptions = options;
                var forwarding = cancellationToken.Register(() =>
                {
                    owner.SendCancelled.TrySetResult();
                    run.Signal();
                });
                try
                {
                    if (options.RunLifecycle is { } lifecycle)
                    {
                        await lifecycle.StartedAsync(run.Id, run.Source.Token);
                        owner.RunBindings++;
                    }
                    (number == 1 ? owner.SendStarted : owner.SecondSendStarted).TrySetResult();
                    await (number == 1 ? owner.ReleaseSend.Task : owner.ReleaseSecondSend.Task);
                    return run.Id;
                }
                finally
                {
                    lock (_gate) run.Closing = true;
                    var closing = options.RunLifecycle?.ClosingAsync(run.Id) ?? Task.CompletedTask;
                    var registrationDisposal = forwarding.DisposeAsync().AsTask();
                    try { await Task.WhenAll(closing, registrationDisposal); }
                    finally
                    {
                        owner.RunClosures++;
                        lock (_gate) { run.SignalGate.TrySetResult(false); _run = null; }
                        try { await run.Work; }
                        finally
                        {
                            await run.Registration.DisposeAsync();
                            run.Source.Dispose();
                            Interlocked.Decrement(ref owner._active);
                        }
                    }
                }
            }
            public async Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentRunId expectedRunId, CancellationToken cancellationToken = default)
            {
                var owner = _owner;
                Interlocked.Increment(ref owner._active);
                try
                {
                    await using var registration = cancellationToken.Register(() => owner.ExactCallerCancelled.TrySetResult());
                    owner.ExactEntered.TrySetResult();
                    if (owner.HoldExactAdmission) await owner.ReleaseExactAdmission.Task;
                    ExactRun run;
                    lock (_gate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_run is not { Closing: false } current || current.Id != expectedRunId)
                            return AgentTargetedAbortOutcome.TargetNotActive;
                        run = current;
                        owner.ExactAdmissions++;
                        run.Signal();
                    }
                    await run.Work;
                    if (owner.FailExactAbort) throw new InvalidOperationException("Private failure after signalling.");
                    return AgentTargetedAbortOutcome.CancellationSignalled;
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public override async Task AbortAsync(CancellationToken cancellationToken = default)
            {
                var owner = _owner;
                ExactRun? run;
                lock (_gate) { run = _run; run?.Signal(); }
                owner.AbortStarted.TrySetResult();
                if (run is not null) await run.Work;
            }
            private sealed class ExactRun
            {
                internal ExactRun(ControlledProvider owner, AgentRunId id)
                {
                    Id = id;
                    if (owner.HoldExactAbort)
                        Registration = Source.Token.Register(() =>
                        {
                            owner.ExactAbortStarted.TrySetResult();
                            owner.ReleaseExactAbort.Task.GetAwaiter().GetResult();
                        });
                    Work = CancelAsync(owner);
                }
                internal AgentRunId Id { get; }
                internal CancellationTokenSource Source { get; } = new();
                internal TaskCompletionSource<bool> SignalGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
                internal Task Work { get; }
                internal CancellationTokenRegistration Registration { get; }
                internal bool Closing { get; set; }
                internal void Signal() => SignalGate.TrySetResult(true);
                private async Task CancelAsync(ControlledProvider owner)
                {
                    if (!await SignalGate.Task) return;
                    Interlocked.Increment(ref owner.ExactTraversals);
                    var traversal = Source.CancelAsync();
                    if (!owner.HoldExactAbort) owner.ExactAbortStarted.TrySetResult();
                    await traversal;
                }
            }
        }

        private sealed class CompactSession : Session, IAgentIdleCompactionProvider
        {
            private readonly ControlledProvider _owner;
            internal CompactSession(ControlledProvider owner, string sessionId) : base(owner, sessionId) => _owner = owner;

            public async Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
            {
                var owner = _owner;
                Interlocked.Increment(ref owner._active);
                try
                {
                    owner.CompactToken = cancellationToken;
                    using var readiness = cancellationToken.Register(() => owner.CompactCancelled.TrySetResult());
                    owner.CompactEntered.TrySetResult();
                    if (owner.HoldCompactAdmission) await owner.ReleaseCompactAdmission.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (owner.CurrentRun is not null) return null;
                    Interlocked.Increment(ref owner._compactions);
                    owner.CompactStarted.TrySetResult();
                    if (owner.HoldCompact) await owner.ReleaseCompact.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (owner.FailCompact) throw new InvalidOperationException("Private fixture provider failure.");
                    return owner.CompactOutcome;
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
