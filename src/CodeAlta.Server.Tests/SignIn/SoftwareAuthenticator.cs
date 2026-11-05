using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// A passkey authenticator in software, as a browser and a platform authenticator would answer
/// <c>navigator.credentials.create()</c> and <c>get()</c> (WebAuthn level 3, ES256, attestation "none", user present and
/// verified), so that the server's ceremonies are tested without a browser.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private uint _counter;
    private string? _userHandle;

    /// <summary>Gets the credential id, base64url.</summary>
    public string CredentialId => Base64Url.EncodeToString(_credentialId);

    /// <summary>Answers <c>create()</c> for creation options, at an origin.</summary>
    /// <param name="optionsJson">The JSON of the creation options.</param>
    /// <param name="origin">The origin of the page, such as <c>http://127.0.0.1:5000</c>.</param>
    /// <param name="challengeOverride">A challenge to answer instead of the one of the options, for refusals.</param>
    /// <returns>The JSON of the credential.</returns>
    public string Create(string optionsJson, string origin, string? challengeOverride = null)
    {
        var options = JsonNode.Parse(optionsJson)!;
        var rpId = options["rp"]?["id"]?.GetValue<string>() ?? new Uri(origin).Host;
        _userHandle = options["user"]!["id"]!.GetValue<string>();
        var clientData = ClientData("webauthn.create", challengeOverride ?? options["challenge"]!.GetValue<string>(), origin);
        var parameters = _key.ExportParameters(false);
        var coseKey = Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)), (Cbor.Int(3), Cbor.Int(-7)), (Cbor.Int(-1), Cbor.Int(1)), (Cbor.Int(-2), Cbor.Bytes(parameters.Q.X!)), (Cbor.Int(-3), Cbor.Bytes(parameters.Q.Y!)));
        var authenticatorData = new List<byte>();
        authenticatorData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authenticatorData.Add(0x45); // user present, user verified, attested credential data
        authenticatorData.AddRange(BigEndian(_counter));
        authenticatorData.AddRange(new byte[16]); // AAGUID
        authenticatorData.Add((byte)(_credentialId.Length >> 8));
        authenticatorData.Add((byte)_credentialId.Length);
        authenticatorData.AddRange(_credentialId);
        authenticatorData.AddRange(coseKey);
        var attestation = Cbor.Map((Cbor.Text("fmt"), Cbor.Text("none")), (Cbor.Text("attStmt"), Cbor.Map()), (Cbor.Text("authData"), Cbor.Bytes([.. authenticatorData])));
        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(attestation),
                ["transports"] = new JsonArray("internal"),
            },
        }.ToJsonString();
    }

    /// <summary>Answers <c>get()</c> for request options, at an origin.</summary>
    /// <param name="optionsJson">The JSON of the request options.</param>
    /// <param name="origin">The origin of the page.</param>
    /// <param name="challengeOverride">A challenge to answer instead of the one of the options, for refusals.</param>
    /// <returns>The JSON of the credential.</returns>
    public string Get(string optionsJson, string origin, string? challengeOverride = null)
    {
        var options = JsonNode.Parse(optionsJson)!;
        var rpId = options["rpId"]?.GetValue<string>() ?? new Uri(origin).Host;
        var clientData = ClientData("webauthn.get", challengeOverride ?? options["challenge"]!.GetValue<string>(), origin);
        _counter++;
        var authenticatorData = new List<byte>();
        authenticatorData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authenticatorData.Add(0x05); // user present, user verified
        authenticatorData.AddRange(BigEndian(_counter));
        var signed = authenticatorData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString([.. authenticatorData]),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = _userHandle,
            },
        }.ToJsonString();
    }

    public void Dispose() => _key.Dispose();

    private static byte[] ClientData(string type, string challenge, string origin)
        => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = origin, ["crossOrigin"] = false });

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>The few CBOR items an authenticator writes.</summary>
    private static class Cbor
    {
        public static byte[] Int(long value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)bytes.Length), .. bytes];
        }

        public static byte[] Map(params (byte[] Key, byte[] Value)[] pairs)
            => [.. Head(5, (ulong)pairs.Length), .. pairs.SelectMany(static pair => pair.Key.Concat(pair.Value))];

        private static byte[] Head(int major, ulong value)
        {
            var type = (byte)(major << 5);
            return value switch
            {
                < 24 => [(byte)(type | (byte)value)],
                <= byte.MaxValue => [(byte)(type | 24), (byte)value],
                <= ushort.MaxValue => [(byte)(type | 25), (byte)(value >> 8), (byte)value],
                _ => [(byte)(type | 26), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value],
            };
        }
    }
}
