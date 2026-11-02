using System.Text.Json;

namespace CodeAlta.Fleet.Protocol.Tests;

[TestClass]
public sealed class FleetMessageSerializerTests
{
    [TestMethod]
    public void EnvelopesAreWrittenWithTheShortNamesOfTheProtocol()
    {
        var request = new FleetRequest("01JBX", "sessions.send", Json.Parse("""{"text":"hi"}"""), "rk-1", "00-4bf9-01");
        Assert.AreEqual("""{"t":"req","id":"01JBX","m":"sessions.send","p":{"text":"hi"},"idem":"rk-1","tp":"00-4bf9-01"}""", request.Text());

        Assert.AreEqual("""{"t":"res","id":"01JBX","ok":true,"r":{"n":1}}""", FleetResponse.Success("01JBX", Json.Parse("""{"n":1}""")).Text());
        Assert.AreEqual("""{"t":"res","id":"01JBX","ok":false,"err":{"code":"session.stale_run","hint":"refresh"}}""",
            FleetResponse.Failure("01JBX", new FleetError("session.stale_run", FleetErrorHints.Refresh)).Text());
        Assert.AreEqual("""{"t":"evt","e":"session.display","p":{},"ch":7,"seq":4211}""", new FleetEvent("session.display", Json.Parse("{}"), 7, 4211).Text());
        Assert.AreEqual("""{"t":"req","id":"2","m":"node.info"}""", new FleetRequest("2", "node.info").Text());
    }

    [TestMethod]
    public void HandshakeFramesRoundTrip()
    {
        var time = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var challenge = new FleetHelloChallenge("n0nce", time, new FleetProtocolRange(1, 2), new FleetConnectionPolicy(1024, 25_000));
        Assert.AreEqual("""{"t":"hello.challenge","nonce":"n0nce","time":"2026-11-02T10:00:00+00:00","protocol":{"min":1,"max":2},"policy":{"maxFrame":1024,"heartbeat":25000}}""",
            challenge.Text());

        var hello = new FleetHello(new FleetProtocolRange(1, 3), new FleetClientInfo("desktop", "4.0.0", "windows"), new FleetDeviceProof("dev-1", "sig"),
            new FleetResume(Json.Parse("""{"deliveries":12}""")));
        var read = (FleetHello)FleetMessageSerializer.Deserialize(FleetMessageSerializer.Serialize(hello))!;
        Assert.AreEqual(hello.Protocol, read.Protocol);
        Assert.AreEqual(hello.Client, read.Client);
        Assert.AreEqual(hello.Device, read.Device);
        Assert.AreEqual(12, read.Resume!.Cursors!.Value.GetProperty("deliveries").GetInt32());

        var welcome = (FleetWelcome)FleetMessageSerializer.Deserialize(new FleetWelcome(2, ["relay"], new FleetConnectionPolicy(1024, 20_000, 8)).Text().Utf8())!;
        Assert.AreEqual(2, welcome.Protocol);
        CollectionAssert.AreEqual(new[] { "relay" }, welcome.Features.ToArray());
        Assert.AreEqual(8, welcome.Policy.MaxChannels);

        var error = (FleetHandshakeError)FleetMessageSerializer.Deserialize(new FleetHandshakeError(new FleetError(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry, null, 30)).Text().Utf8())!;
        Assert.AreEqual(new FleetError(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry, null, 30), error.Error);
    }

    [TestMethod]
    public void FramesReadWhereverTheKindIsAndIgnoreUnknownFields()
    {
        var message = FleetMessageSerializer.Deserialize("""{"id":"7","future":{"a":[1,2]},"m":"node.info","t":"req","p":[1]}""".Utf8());
        var request = (FleetRequest)message!;
        Assert.AreEqual("7", request.Id);
        Assert.AreEqual("node.info", request.Method);
        Assert.AreEqual(JsonValueKind.Array, request.Params!.Value.ValueKind);
        Assert.IsNull(request.IdempotencyKey);
    }

    [TestMethod]
    public void AnUnknownKindReadsAsNullAndAFrameWithoutKindFails()
    {
        Assert.IsNull(FleetMessageSerializer.Deserialize("""{"t":"stream.credit","n":3}""".Utf8()));
        Assert.ThrowsExactly<JsonException>(() => FleetMessageSerializer.Deserialize("""{"id":"1"}""".Utf8()));
        Assert.ThrowsExactly<JsonException>(() => FleetMessageSerializer.Deserialize("""[1]""".Utf8()));
        Assert.ThrowsExactly<JsonException>(() => FleetMessageSerializer.Deserialize("""{"t":1}""".Utf8()));
        Assert.Throws<JsonException>(() => FleetMessageSerializer.Deserialize("not json"u8));
        Assert.Throws<JsonException>(() => FleetMessageSerializer.Deserialize("{\"t\":\"req\",\"id\":\"1\""u8));
    }

    [TestMethod]
    public void TheHighestCommonVersionIsNegotiated()
    {
        Assert.IsTrue(FleetProtocolRange.TryNegotiate(new FleetProtocolRange(1, 3), new FleetProtocolRange(2, 5), out var version));
        Assert.AreEqual(3, version);
        Assert.IsTrue(FleetProtocolRange.TryNegotiate(new FleetProtocolRange(2, 2), new FleetProtocolRange(1, 2), out version));
        Assert.AreEqual(2, version);
        Assert.IsFalse(FleetProtocolRange.TryNegotiate(new FleetProtocolRange(3, 4), new FleetProtocolRange(1, 2), out version));
        Assert.AreEqual(0, version);
        Assert.IsFalse(FleetProtocolRange.TryNegotiate(new FleetProtocolRange(2, 1), new FleetProtocolRange(1, 2), out _));
    }

    [TestMethod]
    public void HandlerExceptionsMapToCodesWithoutTheirMessages()
    {
        Assert.AreEqual(new FleetError("session.stale_run", "refresh"), FleetErrors.FromException(new FleetException("session.stale_run", "refresh")));
        var internalError = FleetErrors.FromException(new InvalidOperationException("C:\\secret\\path"));
        Assert.AreEqual(FleetErrorCodes.InternalError, internalError.Code);
        Assert.IsNull(internalError.Message);
        Assert.AreEqual(FleetErrorCodes.InvalidParams, FleetErrors.FromException(new JsonException("bad")).Code);
        Assert.AreEqual(FleetErrorCodes.Timeout, FleetErrors.FromException(new TimeoutException()).Code);
        Assert.AreEqual(FleetErrorCodes.Cancelled, FleetErrors.FromException(new OperationCanceledException()).Code);
    }
}
