using System.Security.Cryptography;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class PairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PayloadRoundTripsThroughItsUri()
    {
        var fingerprint = DeviceFingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
        var secret = RandomNumberGenerator.GetBytes(PairingPayload.SecretLength);
        var payload = new PairingPayload(
            "Alex's Mac mini é&=?",
            fingerprint,
            ["mac-mini.tail1234.ts.net:2585", "192.168.1.20:2585", "[fe80::1]:2585"],
            secret,
            Now.AddMinutes(10).AddMilliseconds(400));

        var uri = payload.ToUri();
        var read = PairingPayload.Parse(uri, Now);

        Assert.StartsWith("codealta-pair://pair?v=1&", uri);
        Assert.IsTrue(Uri.TryCreate(uri, UriKind.Absolute, out var parsed), uri);
        Assert.AreEqual(PairingPayload.UriScheme, parsed.Scheme);
        Assert.AreEqual("pair", parsed.Host);
        Assert.AreEqual(PairingPayload.CurrentVersion, read.Version);
        Assert.AreEqual(DeviceIdentity.DeviceIdOf(fingerprint), read.DeviceId);
        Assert.AreEqual("Alex's Mac mini é&=?", read.DeviceName);
        Assert.AreEqual(fingerprint, read.Fingerprint);
        CollectionAssert.AreEqual(payload.Addresses.ToArray(), read.Addresses.ToArray());
        CollectionAssert.AreEqual(secret, read.Secret.ToArray());
        Assert.AreEqual(Now.AddMinutes(10), read.ExpiresAt);
        Assert.DoesNotContain(Convert.ToBase64String(secret), payload.ToString());
    }

    [TestMethod]
    public void ExpiredPayloadIsRefused()
    {
        var uri = NewPayload(Now.AddMinutes(5)).ToUri();

        Assert.IsTrue(PairingPayload.TryParse(uri, Now.AddMinutes(4), out _, out _));
        Assert.IsFalse(PairingPayload.TryParse(uri, Now.AddMinutes(5), out var payload, out var error));
        Assert.IsNull(payload);
        Assert.AreEqual(PairingPayloadError.Expired, error);
        Assert.ThrowsExactly<FormatException>(() => PairingPayload.Parse(uri, Now.AddHours(1)));
    }

    [TestMethod]
    public void MalformedPayloadsAreRefused()
    {
        var valid = NewPayload(Now.AddMinutes(10)).ToUri();
        var other = DeviceFingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
        string[] malformed =
        [
            "",
            "hello",
            "https://example.com/?v=1",
            valid.Replace("codealta-pair://pair?", "codealta-pair://other?", StringComparison.Ordinal),
            Remove(valid, "secret"),
            Remove(valid, "fp"),
            Remove(valid, "id"),
            Remove(valid, "name"),
            Remove(valid, "exp"),
            Remove(valid, "addr"),
            Replace(valid, "secret", "AAAA"),
            Replace(valid, "fp", "not-a-fingerprint"),
            Replace(valid, "id", DeviceIdentity.DeviceIdOf(other)),
            Replace(valid, "addr", "host-without-port"),
            Replace(valid, "addr", "host:0"),
            Replace(valid, "addr", "host:65536"),
            Replace(valid, "addr", "[not-ipv6]:2585"),
            Replace(valid, "exp", "-5"),
            Replace(valid, "name", "%00bad"),
            valid + "&secret=" + new string('A', 43),
            valid + "&broken",
            valid + new string('x', 2048),
        ];

        foreach (var text in malformed)
        {
            Assert.IsFalse(PairingPayload.TryParse(text, Now, out _, out var error), text);
            Assert.AreEqual(PairingPayloadError.Malformed, error, text);
            Assert.ThrowsExactly<FormatException>(() => PairingPayload.Parse(text, Now), text);
        }

        Assert.IsFalse(PairingPayload.TryParse(Replace(valid, "v", "2"), Now, out _, out var versionError));
        Assert.AreEqual(PairingPayloadError.UnsupportedVersion, versionError);
        Assert.IsTrue(PairingPayload.TryParse(valid + "&later=field", Now, out _, out _));
    }

    [TestMethod]
    public void OfferSecretIsAcceptedOnce()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        var clock = new ManualTimeProvider(Now);
        var offers = new PairingOfferService(identity, clock);

        var payload = offers.CreateOffer("Mac mini", ["192.168.1.20:2585"]);

        Assert.AreEqual(identity.DeviceId, payload.DeviceId);
        Assert.AreEqual(identity.Fingerprint, payload.Fingerprint);
        Assert.AreEqual(Now + PairingOfferService.DefaultLifetime, payload.ExpiresAt);
        Assert.IsTrue(offers.HasOpenOffer);
        Assert.AreEqual(PairingRedeemResult.WrongSecret, offers.Redeem(RandomNumberGenerator.GetBytes(32)));
        Assert.AreEqual(PairingRedeemResult.WrongSecret, offers.Redeem(payload.Secret[..16]));
        Assert.AreEqual(PairingRedeemResult.Accepted, offers.Redeem(payload.Secret));
        Assert.AreEqual(PairingRedeemResult.NoOffer, offers.Redeem(payload.Secret));
        Assert.IsFalse(offers.HasOpenOffer);
    }

    [TestMethod]
    public void OfferIsRefusedAfterItsExpiry()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        var clock = new ManualTimeProvider(Now);
        var offers = new PairingOfferService(identity, clock);
        var payload = offers.CreateOffer("Mac mini", ["192.168.1.20:2585"], TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.IsFalse(offers.HasOpenOffer);
        Assert.AreEqual(PairingRedeemResult.Expired, offers.Redeem(payload.Secret));
        Assert.AreEqual(PairingRedeemResult.NoOffer, offers.Redeem(payload.Secret));
    }

    [TestMethod]
    public void OfferIsRefusedAfterTooManyAttempts()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        var offers = new PairingOfferService(identity, new ManualTimeProvider(Now), maxAttempts: 3);
        var payload = offers.CreateOffer("Mac mini", ["192.168.1.20:2585"]);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.AreEqual(PairingRedeemResult.WrongSecret, offers.Redeem(RandomNumberGenerator.GetBytes(32)));
        }

        Assert.AreEqual(PairingRedeemResult.TooManyAttempts, offers.Redeem(payload.Secret));
        Assert.AreEqual(PairingRedeemResult.TooManyAttempts, offers.Redeem(payload.Secret));
    }

    [TestMethod]
    public void NewOfferReplacesTheOpenOne()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        var offers = new PairingOfferService(identity, new ManualTimeProvider(Now));
        var first = offers.CreateOffer("Mac mini", ["192.168.1.20:2585"]);
        var second = offers.CreateOffer("Mac mini", ["192.168.1.20:2585"]);

        Assert.AreEqual(PairingRedeemResult.WrongSecret, offers.Redeem(first.Secret));
        Assert.AreEqual(PairingRedeemResult.Accepted, offers.Redeem(second.Secret));

        offers.CreateOffer("Mac mini", ["192.168.1.20:2585"]);
        offers.Cancel();
        Assert.AreEqual(PairingRedeemResult.NoOffer, offers.Redeem(second.Secret));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => offers.CreateOffer("Mac mini", ["host:1"], TimeSpan.FromDays(2)));
    }

    private static PairingPayload NewPayload(DateTimeOffset expiresAt)
        => new("Mac mini", DeviceFingerprint.FromBytes(RandomNumberGenerator.GetBytes(32)), ["192.168.1.20:2585", "[::1]:2585"],
            RandomNumberGenerator.GetBytes(PairingPayload.SecretLength), expiresAt);

    private static string Remove(string uri, string field)
    {
        var (prefix, parts) = Split(uri);
        return prefix + string.Join('&', parts.Where(part => !part.StartsWith(field + "=", StringComparison.Ordinal)));
    }

    private static string Replace(string uri, string field, string value)
    {
        var (prefix, parts) = Split(uri);
        var replaced = false;
        return prefix + string.Join('&', parts.Select(part =>
        {
            if (replaced || !part.StartsWith(field + "=", StringComparison.Ordinal))
            {
                return part;
            }

            replaced = true;
            return field + "=" + value;
        }));
    }

    private static (string Prefix, string[] Parts) Split(string uri)
    {
        var query = uri.IndexOf('?', StringComparison.Ordinal) + 1;
        return (uri[..query], uri[query..].Split('&'));
    }
}
