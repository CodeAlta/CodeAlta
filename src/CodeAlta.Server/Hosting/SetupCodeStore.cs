using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;

namespace CodeAlta.Server.Hosting;

/// <summary>A one-time setup code and its expiry.</summary>
/// <param name="Code">The code, as people read and type it (<c>ABCD-EFGH-JKLM-NPQR</c>).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record SetupCode(string Code, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The result of checking a setup code.</summary>
public enum SetupCodeCheck
{
    /// <summary>The code is the one waiting, and it has not expired.</summary>
    Valid,

    /// <summary>No code waits, or the code is not the one waiting.</summary>
    Invalid,

    /// <summary>The code is the one waiting, but it expired.</summary>
    Expired,
}

/// <summary>
/// The one-time setup code of a server without an owner, in a file of its data root that only its user reads, so that
/// <c>altaserver setup-code</c> prints or renews it beside a running server. It ends with the setup, with its expiry,
/// or after a few wrong attempts.
/// </summary>
public sealed class SetupCodeStore
{
    /// <summary>How many wrong codes are accepted before the waiting one is dropped.</summary>
    public const int MaxFailedAttempts = 5;

    // Crockford-like: no 0/O, 1/I/L, so that a code read aloud or from a screen is typed right.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 16;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private int _failedAttempts;

    /// <summary>Initializes a new instance of the <see cref="SetupCodeStore"/> class.</summary>
    /// <param name="path">The file of the code.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    public SetupCodeStore(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        _path = path;
        _time = time;
    }

    /// <summary>Gets the code that waits and has not expired, or <see langword="null"/>.</summary>
    /// <returns>The code, or <see langword="null"/>.</returns>
    public SetupCode? GetCurrent()
    {
        var code = Read();
        return code is not null && code.ExpiresAt > _time.GetUtcNow() ? code : null;
    }

    /// <summary>Gets the code that waits, or makes a new one when none does or it expired.</summary>
    /// <param name="lifetime">How long a new code works.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public SetupCode GetOrCreate(TimeSpan lifetime) => GetCurrent() ?? Create(lifetime);

    /// <summary>Makes a new code in place of any other.</summary>
    /// <param name="lifetime">How long it works.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public SetupCode Create(TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        Span<char> chars = stackalloc char[CodeLength];
        for (var index = 0; index < CodeLength; index++) chars[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var text = $"{chars[..4]}-{chars[4..8]}-{chars[8..12]}-{chars[12..]}";
        var now = _time.GetUtcNow();
        var code = new SetupCode(text, now, now + lifetime);
        Write(code);
        lock (_gate) _failedAttempts = 0;
        return code;
    }

    /// <summary>Checks a code typed by a person. After <see cref="MaxFailedAttempts"/> wrong codes, the waiting one is dropped.</summary>
    /// <param name="candidate">The code given, in any case, with or without its dashes.</param>
    /// <returns>Whether it is valid.</returns>
    public SetupCodeCheck Check(string? candidate)
    {
        var code = Read();
        if (code is null) return SetupCodeCheck.Invalid;
        var given = Encoding.UTF8.GetBytes(Normalize(candidate));
        var expected = Encoding.UTF8.GetBytes(Normalize(code.Code));
        if (!CryptographicOperations.FixedTimeEquals(given, expected))
        {
            lock (_gate)
            {
                if (++_failedAttempts >= MaxFailedAttempts)
                {
                    _failedAttempts = 0;
                    Consume();
                }
            }

            return SetupCodeCheck.Invalid;
        }

        return code.ExpiresAt > _time.GetUtcNow() ? SetupCodeCheck.Valid : SetupCodeCheck.Expired;
    }

    /// <summary>Removes the code: after the setup, it never works again.</summary>
    public void Consume()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Another process holds it for an instant; an owner exists now, so the code is refused anyway.
        }
    }

    private SetupCode? Read()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, SetupCodeJsonContext.Default.SetupCode);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private void Write(SetupCode code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, code, SetupCodeJsonContext.Default.SetupCode);
        }

        // The code sets up the owner: only the user of the server reads it.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        StagedFileReplacement.MoveOver(temporary, _path);
    }

    private static string Normalize(string? code)
        => string.IsNullOrEmpty(code) ? string.Empty : code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetupCode))]
internal sealed partial class SetupCodeJsonContext : JsonSerializerContext;
