using System.Text.Json;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node.Tests;

/// <summary>
/// <c>sessions.read</c> and <c>sessions.act</c>: a device reads a session of a machine with the <c>alta</c> commands of that
/// machine, each built by the machine from checked values, and only for a session it may see; and the commands of a person
/// on another machine (<see cref="FleetMachineAlta"/>), which are those reads and the calls of the node API.
/// </summary>
[TestClass]
public sealed class FleetNodeReadTests
{
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AnEightMiBImage_CrossesTheFourMiBChannel_InBoundedPieces_AndReassemblesExactly()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        var machine = await fixture.PairViewerAsync(token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        fixture.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedNodeProvider.Answer("Done.")));
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Prompt: "Create the journal", Selection: new FleetModelSelection("scripted", ScriptedNodeProvider.Model)), "image-session", token);
        var alta = new FleetMachineAlta("test-node", _ => ValueTask.FromResult<FleetNodeClient?>(client));
        var store = fixture.Node.Host.SessionViewCatalog.JournalStore.CreateSessionStore();
        var attachments = await WaitForAsync(async () => await store.GetAttachmentDirectoryAsync(created.SessionId, token), token);
        Directory.CreateDirectory(attachments);
        var png = new byte[8 * 1024 * 1024];
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5c8AAAAASUVORK5CYII=").CopyTo(png, 0);
        await File.WriteAllBytesAsync(Path.Combine(attachments, "large.png"), png, token);

        var piece = await client.ReadSessionAsync(new FleetSessionReadParams("session.image", created.SessionId) { Arguments = ["large.png"] }, token);
        var record = piece.Records.Single(item => Text(item, "type") == "alta.session.image");
        Assert.IsLessThanOrEqualTo(FleetSessionReads.ImageChunk, record.GetProperty("base64").GetString()!.Length);
        Assert.IsGreaterThan(4 * 1024 * 1024, record.GetProperty("base64Length").GetInt32(), "It really needs more than one channel frame.");
        var whole = await alta.InvokeAsync(["session", "image", created.SessionId, "large.png"], options: new() { MaxOutputBytes = 16 * 1024 * 1024 }, cancellationToken: token);
        Assert.AreEqual(0, whole.ExitCode, whole.Error);
        var image = Records(whole).Single(item => Text(item, "type") == "alta.session.image");
        CollectionAssert.AreEqual(png, Convert.FromBase64String(image.GetProperty("base64").GetString()!));
    }

    [TestMethod]
    public async Task APerson_ReadsAndDrivesASessionOfAnotherMachine_WithTheCommandsOfThatMachine()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairViewerAsync(token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        CollectionAssert.Contains((await client.GetInfoAsync(token)).Features.ToArray(), FleetSessionReads.Feature, "The node says it answers reads.");
        var alta = new FleetMachineAlta("test-node", _ => ValueTask.FromResult<FleetNodeClient?>(client));

        // A new session, with what the phone chose for it, then its first prompt: the calls of the node API.
        var created = await alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted"], cancellationToken: token);
        Assert.AreEqual(0, created.ExitCode, created.Error);
        var sessionId = Records(created).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;
        fixture.Provider.Turns.Enqueue((_, _) => Task.FromResult(ScriptedNodeProvider.Answer("Hello from the node.")));
        var sent = await alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--queue-if-busy", "--no-wait"], "Hello node", cancellationToken: token);
        Assert.AreEqual(0, sent.ExitCode, sent.Error);

        // The timeline, as the machine itself writes it: the records of its alta, read by sequence number.
        var events = await WaitForAsync(async () =>
        {
            var read = await alta.InvokeAsync(["session", "events", sessionId, "--since", "0", "--limit", "400", "--include", "user,assistant", "--fields", "sequenceNumber,kind,content"],
                cancellationToken: token);
            Assert.AreEqual(0, read.ExitCode, read.Error);
            var records = Records(read).Where(static record => Text(record, "type") == "alta.session.event").ToArray();
            return records.Any(static record => record.GetRawText().Contains("Hello from the node.", StringComparison.Ordinal)) ? records : null;
        }, token);
        Assert.IsTrue(events.Any(static record => record.GetRawText().Contains("Hello node", StringComparison.Ordinal)), "The prompt is in the timeline.");
        Assert.IsTrue(events.All(static record => record.TryGetProperty("sequenceNumber", out _)));

        var status = await alta.InvokeAsync(["session", "status", sessionId], cancellationToken: token);
        Assert.AreEqual(0, status.ExitCode, status.Error);
        Assert.IsTrue(Records(status).Any(static record => Text(record, "type") == "alta.session.status"));
        var listed = await alta.InvokeAsync(["session", "list", "--limit", "20"], cancellationToken: token);
        Assert.IsTrue(Records(listed).Any(record => Text(record, "sessionId") == sessionId), "The session is in the list of the machine.");
        var choices = await alta.InvokeAsync(["session", "choices", sessionId], cancellationToken: token);
        Assert.AreEqual(0, choices.ExitCode, choices.Error);

        // What the machine has, for the choices of a new session there.
        foreach (var command in new[] { new[] { "provider", "list", "--detailed" }, ["model", "list", "--detailed"], ["project", "list", "--detailed", "--all"], ["space", "list"], ["prompt", "list"] })
        {
            var read = await alta.InvokeAsync(command, cancellationToken: token);
            Assert.AreEqual(0, read.ExitCode, string.Join(' ', command) + ": " + read.Error);
        }

        Assert.IsTrue(Records(await alta.InvokeAsync(["provider", "list", "--detailed"], cancellationToken: token)).Any(static record => record.GetRawText().Contains("scripted", StringComparison.Ordinal)));

        // A queued prompt is taken back, the session archived and restored: small actions, by sessions.act.
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedNodeProvider.WaitThenReply(gate.Task, "Done.", started));
        Assert.AreEqual(0, (await alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--no-wait"], "Work", cancellationToken: token)).ExitCode);
        await started.Task.WithinAsync(Patience, token);
        var queued = await alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--queue-if-busy", "--no-wait"], "Later", cancellationToken: token);
        var item = Records(queued).Single(static record => Text(record, "type") == "alta.session.queued").GetProperty("queueItemId").GetString()!;
        var waiting = await alta.InvokeAsync(["session", "queued", sessionId], cancellationToken: token);
        Assert.IsTrue(Records(waiting).Any(record => Text(record, "queueItemId") == item), "The machine lists the prompt that waits.");
        var unqueued = await alta.InvokeAsync(["session", "unqueue", sessionId, item], cancellationToken: token);
        Assert.AreEqual(0, unqueued.ExitCode, unqueued.Error);
        Assert.AreEqual(0, (await alta.InvokeAsync(["session", "abort", sessionId, "--reason", "Stopped from the phone"], cancellationToken: token)).ExitCode);
        gate.TrySetResult();

        // What the node API has no call for is refused here, and never sent as a command line.
        var unknown = await alta.InvokeAsync(["session", "delete", sessionId], cancellationToken: token);
        Assert.AreNotEqual(0, unknown.ExitCode);
        StringAssert.Contains(unknown.TranscriptJsonl, FleetMachineAlta.Unsupported);
        var files = await alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--attachments", "/tmp/files.json"], "With files", cancellationToken: token);
        StringAssert.Contains(files.Error, "Files and photos");
        var option = await alta.InvokeAsync(["session", "events", sessionId, "--raw"], cancellationToken: token);
        StringAssert.Contains(option.TranscriptJsonl, FleetMachineAlta.Unsupported, "An option the read does not declare is no read.");
    }

    [TestMethod]
    public async Task TheMachine_BuildsEachRead_AndRefusesWhatIsNotOfItsKind_OrNotTheCallersToSee()
    {
        await using var fixture = await NodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairViewerAsync(token);
        await using var client = await FleetNodeClient.ConnectAsync(machine, fixture.ViewerIdentity, token);
        var shared = (await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Shared"), "create-shared", token)).SessionId;
        var hidden = (await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Hidden"), "create-hidden", token)).SessionId;

        async Task RefusedAsync(FleetSessionReadParams request, string code, string why)
        {
            var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.ReadSessionAsync(request, token), why);
            Assert.AreEqual(code, refused.Error.Code, why);
        }

        await RefusedAsync(new("session.delete", shared), FleetErrorCodes.InvalidParams, "a read the list does not have");
        await RefusedAsync(new("session.events", shared) { Options = new Dictionary<string, string> { ["raw"] = "" } }, FleetErrorCodes.InvalidParams, "an option the read does not declare");
        await RefusedAsync(new("session.events", shared) { Options = new Dictionary<string, string> { ["since"] = "1 --all" } }, FleetErrorCodes.InvalidParams, "a number that is none");
        await RefusedAsync(new("session.events", shared) { Options = new Dictionary<string, string> { ["fields"] = "kind,--help" } }, FleetErrorCodes.InvalidParams, "ids that start an option");
        await RefusedAsync(new("session.tool", shared) { Arguments = ["--max-output"] }, FleetErrorCodes.InvalidParams, "a value that starts an option");
        await RefusedAsync(new("session.tool", shared), FleetErrorCodes.InvalidParams, "a value that is missing");
        await RefusedAsync(new("session.status", "../" + shared), FleetErrorCodes.InvalidParams, "a session that is no id");
        await RefusedAsync(new("project.list", shared), FleetErrorCodes.InvalidParams, "a read of the machine that names a session");
        await RefusedAsync(new("session.status"), FleetErrorCodes.InvalidParams, "a read of a session that names none");
        await RefusedAsync(new("job.status", shared) { Arguments = ["no-such-job"] }, FleetNodeErrorCodes.SessionNotFound, "a job that is not of the session");
        await RefusedAsync(new("session.status", "no-such-session"), FleetNodeErrorCodes.SessionNotFound, "a session the machine does not have");

        // A Private session is not shown to exist: not read, not acted on, not in the list.
        await FleetNodePrivateSessionTests.MarkPrivateAsync(fixture.Node, hidden, token);
        await RefusedAsync(new("session.events", hidden), FleetNodeErrorCodes.SessionNotFound, "the timeline of a Private session");
        await RefusedAsync(new("session.archive", hidden), FleetNodeErrorCodes.SessionNotFound, "an action on a Private session");
        var listed = await client.ReadSessionAsync(new FleetSessionReadParams("session.list"), token);
        Assert.IsTrue(listed.Records.Any(record => Text(record, "sessionId") == shared));
        Assert.IsFalse(listed.Records.Any(record => Text(record, "sessionId") == hidden), "A Private session is not listed to another machine.");

        // Nor is it named by the session it is a sub-agent of: the status of the parent lists the sessions the caller may see.
        var child = await fixture.Node.Alta.InvokeAsync(["session", "create", "--global", "--space", "default", "--provider", "scripted", "--parent", shared],
            caller: CodeAlta.LiveTool.AltaCallerIdentity.Cli, cancellationToken: token);
        Assert.AreEqual(0, child.ExitCode, child.Stdout + child.Error);
        var childId = Records(new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = child.Stdout }).Single(static record => Text(record, "type") == "alta.session.created").GetProperty("sessionId").GetString()!;
        JsonElement Status() => client.ReadSessionAsync(new FleetSessionReadParams("session.status", shared), token).GetAwaiter().GetResult()
            .Records.Single(static record => Text(record, "type") == "alta.session.status");
        CollectionAssert.Contains(Status().GetProperty("childSessionIds").EnumerateArray().Select(static id => id.GetString()).ToArray(), childId);
        await FleetNodePrivateSessionTests.MarkPrivateAsync(fixture.Node, childId, token);
        var parent = Status();
        Assert.AreEqual(0, parent.GetProperty("childSessionIds").GetArrayLength(), "A Private sub-agent is not named by its parent.");
        Assert.AreEqual(0, parent.GetProperty("childCount").GetInt32());

        // A device that only views reads, and changes nothing.
        fixture.Fleet.Service.TrustedDevices.Add("viewer", fixture.ViewerIdentity.Fingerprint, DevicePermissions.View);
        Assert.AreEqual(0, (await client.ReadSessionAsync(new FleetSessionReadParams("session.status", shared), token)).ExitCode);
        await RefusedAsync(new("session.archive", shared), FleetErrorCodes.Forbidden, "an action needs drive");

        // A device the node no longer trusts reads nothing.
        fixture.Fleet.Service.TrustedDevices.Add("viewer", fixture.ViewerIdentity.Fingerprint, DevicePermissions.None);
        await RefusedAsync(new("session.status", shared), FleetErrorCodes.Forbidden, "a read needs view");
    }

    [TestMethod]
    public void ACommandLine_ReadsBackIntoARequest_OnlyWhenItIsOfTheList()
    {
        Assert.IsTrue(FleetSessionReads.TryParse(["session", "tool", "s1", "a1", "--max-text", "12000", "--since", "4"], out var tool));
        Assert.AreEqual(("session.tool", "s1"), (tool!.Read, tool.SessionId));
        CollectionAssert.AreEqual(new[] { "a1" }, tool.Arguments!.ToArray());
        Assert.AreEqual("12000", tool.Options!["max-text"]);
        CollectionAssert.AreEqual(new[] { "session", "tool", "s1", "a1", "--max-text", "12000", "--since", "4" }, FleetSessionReads.Build(tool, out _), "the machine builds the command the client held");

        Assert.IsTrue(FleetSessionReads.TryParse(["job", "list", "--session", "s1"], out var jobs));
        Assert.AreEqual(("job.list", "s1"), (jobs!.Read, jobs.SessionId));
        Assert.IsTrue(FleetSessionReads.TryParse(["job", "output", "j1", "--lines", "200"], out var output));
        Assert.IsNull(output!.SessionId, "the caller names the session the job is of");
        Assert.IsTrue(FleetSessionReads.TryParse(["session", "tail", "s1", "--last", "20", "--no-tool-output", "--tool-images", "--include", "user,assistant", "--fields", "kind,content", "--before", "9"], out _));
        Assert.IsTrue(FleetSessionReads.IsAction(new FleetSessionReadParams("session.unqueue", "s1")));
        Assert.IsFalse(FleetSessionReads.IsAction(new FleetSessionReadParams("session.events", "s1")));

        // What starts work, or is no read of the list, is not read back: the node API has its own calls for it.
        foreach (var command in new[] { new[] { "session", "send", "s1", "--stdin" }, ["session", "create", "--global"], ["session", "delete", "s1"], ["secret", "list"], ["session"],
                     ["session", "events", "s1", "--raw"], ["session", "events"], ["session", "tool", "s1"], ["session", "status", "s1", "extra"] })
            Assert.IsFalse(FleetSessionReads.TryParse(command, out _), string.Join(' ', command));
    }

    private static IEnumerable<JsonElement> Records(PluginAltaCommandResult result)
    {
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static string? Text(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("The node never gave what was expected.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
