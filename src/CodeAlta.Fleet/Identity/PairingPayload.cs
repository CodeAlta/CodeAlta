using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Fleet.Identity;

/// <summary>Why a pairing payload was not read.</summary>
public enum PairingPayloadError
{
    /// <summary>The payload was read.</summary>
    None,

    /// <summary>The text is not a pairing payload, or a field is missing or invalid.</summary>
    Malformed,

    /// <summary>The payload has a version this device does not read.</summary>
    UnsupportedVersion,

    /// <summary>The payload has expired.</summary>
    Expired,
}

/// <summary>
/// What a QR code, a typed link or an SSH bootstrap carries to pair with a device: who it is (device id, name, key
/// fingerprint), where to reach it (candidate addresses), and a one-time secret with its expiry.
/// </summary>
/// <remarks>
/// The text form is a URI: <c>codealta-pair://pair?v=1&amp;id=…&amp;name=…&amp;fp=…&amp;addr=host:port&amp;addr=…&amp;secret=…&amp;exp=…</c>,
/// with the fingerprint and the secret in base64url and the expiry in Unix seconds. The secret makes it a credential
/// until it expires: it is shown, never logged.
/// </remarks>
public sealed class PairingPayload
{
    /// <summary>The version this device writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The length of the one-time secret in bytes.</summary>
    public const int SecretLength = 32;

    /// <summary>The scheme of the URI.</summary>
    public const string UriScheme = "codealta-pair";

    /// <summary>The most addresses a payload carries.</summary>
    public const int MaxAddresses = 8;

    private const string UriPrefix = UriScheme + "://pair?";
    private const int MaxTextLength = 2048;
    private const int MaxHostLength = 253;

    private readonly byte[] _secret;

    /// <summary>Creates a payload.</summary>
    /// <param name="deviceName">The name of the device that offers the pairing.</param>
    /// <param name="fingerprint">The fingerprint of its key; the device id derives from it.</param>
    /// <param name="addresses">Where to reach it, as <c>host:port</c> (an IPv6 address in brackets), 1 to <see cref="MaxAddresses"/>.</param>
    /// <param name="secret">The one-time secret, <see cref="SecretLength"/> bytes.</param>
    /// <param name="expiresAt">When the secret expires; kept to the second.</param>
    /// <exception cref="ArgumentException">A value is missing or invalid.</exception>
    public PairingPayload(string deviceName, DeviceFingerprint fingerprint, IReadOnlyList<string> addresses, ReadOnlySpan<byte> secret, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        DeviceName = TrustedDeviceStore.ValidateName(deviceName, nameof(deviceName));
        if (fingerprint.IsEmpty)
        {
            throw new ArgumentException("A fingerprint is required.", nameof(fingerprint));
        }

        if (addresses.Count is 0 or > MaxAddresses || !addresses.All(IsValidAddress))
        {
            throw new ArgumentException($"A pairing payload has 1 to {MaxAddresses} addresses of the form host:port.", nameof(addresses));
        }

        if (secret.Length != SecretLength)
        {
            throw new ArgumentException($"The secret is {SecretLength} bytes long.", nameof(secret));
        }

        Fingerprint = fingerprint;
        DeviceId = DeviceIdentity.DeviceIdOf(fingerprint);
        Addresses = addresses.ToArray();
        _secret = secret.ToArray();
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds());
    }

    /// <summary>Gets the version of the payload.</summary>
    public int Version => CurrentVersion;

    /// <summary>Gets the device id of the device that offers the pairing.</summary>
    public string DeviceId { get; }

    /// <summary>Gets the name of the device that offers the pairing.</summary>
    public string DeviceName { get; }

    /// <summary>Gets the fingerprint of its key: the pin of the connection.</summary>
    public DeviceFingerprint Fingerprint { get; }

    /// <summary>Gets where to reach it, as <c>host:port</c>, in order of preference.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>Gets the one-time secret.</summary>
    public ReadOnlySpan<byte> Secret => _secret;

    /// <summary>Gets when the secret expires.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Writes the payload as a <c>codealta-pair://</c> URI.</summary>
    /// <returns>The URI.</returns>
    public string ToUri()
    {
        var builder = new StringBuilder(UriPrefix);
        builder.Append("v=").Append(CurrentVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append("&id=").Append(DeviceId);
        builder.Append("&name=").Append(Uri.EscapeDataString(DeviceName));
        builder.Append("&fp=").Append(Fingerprint.ToString());
        foreach (var address in Addresses)
        {
            builder.Append("&addr=").Append(Uri.EscapeDataString(address));
        }

        builder.Append("&secret=").Append(Base64Url.EncodeToString(_secret));
        builder.Append("&exp=").Append(ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <inheritdoc />
    /// <remarks>Leaves out the secret: use <see cref="ToUri"/> to show the payload.</remarks>
    public override string ToString() => $"Pairing with {DeviceName} ({DeviceId}) until {ExpiresAt:O}";

    /// <summary>Reads a payload written by <see cref="ToUri"/>.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="now">The current time, to refuse an expired payload.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="FormatException">The text is not a valid payload, has another version, or has expired.</exception>
    public static PairingPayload Parse(string text, DateTimeOffset now)
    {
        if (TryParse(text, now, out var payload, out var error))
        {
            return payload;
        }

        throw new FormatException(error switch
        {
            PairingPayloadError.Expired => "The pairing code has expired.",
            PairingPayloadError.UnsupportedVersion => "The pairing code comes from a version of CodeAlta this one does not read.",
            _ => "The text is not a CodeAlta pairing code.",
        });
    }

    /// <summary>Reads a payload written by <see cref="ToUri"/>.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="now">The current time, to refuse an expired payload.</param>
    /// <param name="payload">The payload read, or <see langword="null"/>.</param>
    /// <param name="error">Why it was not read, or <see cref="PairingPayloadError.None"/>.</param>
    /// <returns>Whether the payload was read.</returns>
    public static bool TryParse(string? text, DateTimeOffset now, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PairingPayload? payload, out PairingPayloadError error)
    {
        payload = null;
        error = PairingPayloadError.Malformed;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || !text.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? version = null, deviceId = null, name = null, fingerprintText = null, secretText = null, expiryText = null;
        var addresses = new List<string>();
        foreach (var part in text[UriPrefix.Length..].Split('&'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(part[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                return false;
            }

            switch (part[..separator])
            {
                case "v": if (!SetOnce(ref version, value)) return false; break;
                case "id": if (!SetOnce(ref deviceId, value)) return false; break;
                case "name": if (!SetOnce(ref name, value)) return false; break;
                case "fp": if (!SetOnce(ref fingerprintText, value)) return false; break;
                case "secret": if (!SetOnce(ref secretText, value)) return false; break;
                case "exp": if (!SetOnce(ref expiryText, value)) return false; break;
                case "addr": addresses.Add(value); break;
                default: break; // A later version of the same major form may add fields.
            }
        }

        if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber))
        {
            return false;
        }

        if (versionNumber != CurrentVersion)
        {
            error = PairingPayloadError.UnsupportedVersion;
            return false;
        }

        if (!DeviceFingerprint.TryParse(fingerprintText, out var fingerprint)
            || !string.Equals(deviceId, DeviceIdentity.DeviceIdOf(fingerprint), StringComparison.Ordinal)
            || name is null
            || secretText is not { Length: 43 } || !Base64Url.IsValid(secretText)
            || !long.TryParse(expiryText, NumberStyles.None, CultureInfo.InvariantCulture, out var expirySeconds)
            || expirySeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var secret = Base64Url.DecodeFromChars(secretText);
        if (secret.Length != SecretLength || !string.Equals(Base64Url.EncodeToString(secret), secretText, StringComparison.Ordinal))
        {
            return false;
        }

        PairingPayload candidate;
        try
        {
            candidate = new PairingPayload(name, fingerprint, addresses, secret, DateTimeOffset.FromUnixTimeSeconds(expirySeconds));
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (candidate.ExpiresAt <= now)
        {
            error = PairingPayloadError.Expired;
            return false;
        }

        payload = candidate;
        error = PairingPayloadError.None;
        return true;
    }

    /// <summary>Gets whether a text is a candidate address: <c>host:port</c>, with an IPv6 address in brackets.</summary>
    /// <param name="address">The text.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        var separator = address.LastIndexOf(':');
        if (separator <= 0
            || !int.TryParse(address.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        var host = address[..separator];
        if (host.StartsWith('['))
        {
            return host.EndsWith(']')
                && IPAddress.TryParse(host[1..^1], out var ipv6)
                && ipv6.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return host.Length <= MaxHostLength
            && host.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            && !host.StartsWith('.') && !host.StartsWith('-');
    }

    private static bool SetOnce(ref string? field, string value)
    {
        if (field is not null)
        {
            return false;
        }

        field = value;
        return true;
    }
}
