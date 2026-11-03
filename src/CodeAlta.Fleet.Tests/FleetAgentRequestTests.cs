using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The requests agents make of other machines (<c>doc/fleet/access-control.md</c>, "Agents and access"): signed by the
/// device that sends them, for one machine, one time and one nonce, bound to their content; and what a node keeps of the
/// sessions they start: the policy of its owner (off by default), their lineage, and the outbox of their answers.
/// </summary>
[TestClass]
public sealed class FleetAgentRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ARequestSignedByTheDeviceOfTheChannel_VerifiesForItsMachine_AndNothingElseDoes()
    {
        using var folder = new TestFolder();
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "laptop"));
        using var other = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "other"));
        var signed = FleetAgentSigning.Sign(laptop, Start("mac-mini-id"));
        var channel = laptop.Fingerprint.ToString();

        Assert.IsTrue(FleetAgentRequestDocuments.TryVerify(signed, FleetAgentRequestKinds.Start, "mac-mini-id", channel, Now.AddMinutes(3), out var request, out var reason), reason);
        Assert.AreEqual(laptop.DeviceId, request.SignerDeviceId);
        Assert.AreEqual("parent-1", request.ParentSessionId);
        Assert.AreEqual(1, request.Limits!.Depth);
        Assert.IsFalse(string.IsNullOrEmpty(request.Nonce), "Each signature has a nonce of its own.");
        Assert.AreNotEqual(request.Nonce, FleetAgentRequestDocuments.TryRead(FleetAgentSigning.Sign(laptop, Start("mac-mini-id")).Payload, out var second, out _) ? second.Nonce : null);

        Refused(null, channel, "not signed");
        Refused(signed with { Signature = "" }, channel, "not signed");
        Refused(signed, other.Fingerprint.ToString(), "another device than the one that sends it");
        Refused(signed, channel, "another machine", machine: "pc-id");
        Refused(signed, channel, "too long ago", now: Now.AddMinutes(30));
        Refused(signed, channel, "is a 'start', not a 'send'", kind: FleetAgentRequestKinds.Send);
        // A server that relays it cannot widen what it asks: the signature covers every byte.
        Refused(signed with { Payload = signed.Payload.Replace("\"permissionMode\":\"default\"", "\"permissionMode\":\"bypassPermissions\"", StringComparison.Ordinal) },
            channel, "does not verify");
        // Nor sign one itself with another key in the name of the device.
        var forged = FleetAgentSigning.Sign(other, Start("mac-mini-id"));
        Refused(forged, channel, "another device than the one that sends it");

        static void Refused(FleetSignedAgentRequest? signed, string channel, string why, string machine = "mac-mini-id", DateTimeOffset? now = null,
            string kind = FleetAgentRequestKinds.Start)
        {
            Assert.IsFalse(FleetAgentRequestDocuments.TryVerify(signed, kind, machine, channel, now ?? Now, out _, out var reason));
            StringAssert.Contains(reason, why);
        }
    }

    [TestMethod]
    public void TheRequestsReadOnlyWhenTheyHold()
    {
        using var folder = new TestFolder();
        using var laptop = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "laptop"));
        Assert.ThrowsExactly<ArgumentException>(() => FleetAgentSigning.Sign(laptop, Start("m") with { Limits = null }), "A start names its limits.");
        Assert.ThrowsExactly<ArgumentException>(() => FleetAgentSigning.Sign(laptop, Start("m") with
        {
            Limits = new FleetAgentLimits([], null, null, true, FleetAgentRequestDocuments.MaxDepth + 1),
        }), "A chain of agents across machines has a maximum depth.");
        Assert.ThrowsExactly<ArgumentException>(() => FleetAgentSigning.Sign(laptop, Start("m") with { Kind = FleetAgentRequestKinds.Send }),
            "A prompt names its session and its text, and no limits.");
        var send = Start("m") with { Kind = FleetAgentRequestKinds.Send, Limits = null, SessionId = "child-1", TextHash = FleetAgentRequestDocuments.HashText("Hello") };
        Assert.IsTrue(FleetAgentRequestDocuments.TryRead(FleetAgentSigning.Sign(laptop, send).Payload, out var read, out var reason), reason);
        Assert.AreEqual(FleetAgentRequestDocuments.HashText("Hello"), read.TextHash);
        Assert.AreNotEqual(FleetAgentRequestDocuments.HashText("Hello"), FleetAgentRequestDocuments.HashText("Hello!"));
    }

    [TestMethod]
    public async Task TheNodeKeepsItsPolicyOffByDefault_TheLineage_TheNonces_AndTheOutboxOfEachDevice()
    {
        using var folder = new TestFolder();
        var time = new ManualTimeProvider(Now);
        var sessions = new FleetAgentSessions(folder.Path, time);
        Assert.AreEqual(0, sessions.ReadPolicy().Spaces.Count, "No space accepts agents of other machines until the owner allows one.");
        Assert.IsFalse(sessions.ReadPolicy().AllowBypass);

        sessions.WritePolicy(new FleetAgentPolicyInfo([" work ", "Work", "default"], " strict "));
        var policy = new FleetAgentSessions(folder.Path, time).ReadPolicy();
        CollectionAssert.AreEqual(new[] { "work", "default" }, policy.Spaces.ToArray());
        Assert.AreEqual("strict", policy.Sandbox);

        sessions.Add(new FleetAgentChild("child-1", "parent-1", "laptop", "PC", "default", ["shell_command"], "nonce-1", Now));
        Assert.IsFalse(sessions.TryUseNonce("nonce-1"), "The nonce of a start is never accepted again, after a restart either.");
        Assert.IsTrue(sessions.TryUseNonce("nonce-2"));
        Assert.IsFalse(sessions.TryUseNonce("nonce-2"));

        // What the session says goes to the device that started it, for the parent it named, and to no other.
        await sessions.EnqueueAsync(new RemoteParentNotification("child-1", null, "parent-1", "laptop", "answer", "The tests pass.", "run-1", "content-1"));
        await sessions.EnqueueAsync(new RemoteParentNotification("child-1", null, "someone-else", "laptop", "answer", "Not for you.", "run-1", "content-2"));
        await sessions.EnqueueAsync(new RemoteParentNotification("unknown", null, "parent-1", "laptop", "answer", "No lineage.", "run-1", "content-3"));
        var reopened = new FleetAgentSessions(folder.Path, time);
        Assert.AreEqual(0, reopened.Take("phone", 10).Count);
        var taken = reopened.Take("laptop", 10);
        Assert.AreEqual("The tests pass.", taken.Single().Body, "The outbox survives a restart of the node.");
        Assert.AreEqual(1, reopened.Take("laptop", 10).Count, "Taking does not forget: an acknowledgement does.");
        Assert.AreEqual(0, reopened.Acknowledge("phone", [taken[0].Id]), "A device acknowledges only its own deliveries.");
        Assert.AreEqual(1, reopened.Acknowledge("laptop", [taken[0].Id]));
        Assert.AreEqual(0, reopened.Take("laptop", 10).Count);
        CollectionAssert.AreEqual(new[] { "shell_command" }, reopened.Find("CHILD-1")!.DisabledTools.ToArray());

        for (var index = 0; index < FleetAgentSessions.MaxStartsPerWindow; index++) Assert.IsTrue(reopened.TryAdmitStart("laptop"));
        Assert.IsFalse(reopened.TryAdmitStart("laptop"), "Agents of one device start a bounded number of sessions in a window.");
        Assert.IsTrue(reopened.TryAdmitStart("phone"));
        time.Advance(FleetAgentSessions.RateWindow + TimeSpan.FromSeconds(1));
        Assert.IsTrue(reopened.TryAdmitStart("laptop"));
    }

    private static FleetAgentRequest Start(string machine)
        => new(FleetAgentRequest.CurrentVersion, FleetAgentRequestKinds.Start, machine, "", "", Now, "", "parent-1", "PC", "default", null, null,
            FleetAgentRequestDocuments.HashText("Run the tests"), new FleetAgentLimits(["shell_command"], "default", null, true, 1));
}
