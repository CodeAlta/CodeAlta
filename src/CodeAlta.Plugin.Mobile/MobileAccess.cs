using System.Security.Cryptography;
using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Who may use the mobile view: the pairing token of the QR code, and the key of each phone it signed in. Neither is kept
/// on disk, only their SHA-256: a program of this computer that reads the files of the plugin (an agent with a shell) gets
/// nothing it can sign in with, and so cannot answer through the phone what CodeAlta refuses to agents. The pairing token
/// lives in memory, to show the QR code; after a restart the Mobile tab makes a new one, and the phones keep their keys.
/// </summary>
internal sealed class MobileAccess
{
    /// <summary>The name of the file of the plugin's state that keeps the hashes.</summary>
    internal const string FileName = "mobile-access.json";

    /// <summary>The most phones signed in at once: a new one makes the oldest sign in again.</summary>
    internal const int MaximumPhones = 64;

    private readonly IPluginStateStore _state;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private string? _pairing;
    private string? _pairingHash;
    private List<string> _phones;

    private MobileAccess(IPluginStateStore state, MobileAccessFile? saved)
    {
        _state = state;
        _pairingHash = IsHash(saved?.Pairing) ? saved!.Pairing : null;
        _phones = [.. (saved?.Phones ?? []).Where(IsHash).Distinct(StringComparer.Ordinal).TakeLast(MaximumPhones)];
    }

    /// <summary>Gets the pairing token the QR code shows, or null until <see cref="PairAsync"/> made one.</summary>
    internal string? PairingToken
    {
        get { lock (_gate) return _pairing; }
    }

    /// <summary>Gets how many phones are signed in.</summary>
    internal int PhoneCount
    {
        get { lock (_gate) return _phones.Count; }
    }

    /// <summary>Reads the hashes the plugin kept.</summary>
    /// <param name="state">The state of the plugin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<MobileAccess> LoadAsync(IPluginStateStore state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(state, await state.ReadJsonAsync<MobileAccessFile>(PluginStateScope.User, FileName, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Takes over the token a file of before kept itself: it was the cookie and the kept token of every phone it signed in, so
    /// it becomes the key of those phones, and they stay signed in.
    /// </summary>
    internal async Task AdoptAsync(string token, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var hash = Hash(token);
            if (_phones.Contains(hash, StringComparer.Ordinal)) return;
            _phones.Add(hash);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives the pairing token of the QR code, made now when this run of CodeAlta has none yet.</summary>
    internal async Task<string> PairAsync(CancellationToken cancellationToken)
    {
        string token;
        lock (_gate)
        {
            if (_pairing is { } pairing) return pairing;
            token = _pairing = MobileToken.Create();
            _pairingHash = Hash(token);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <summary>A new pairing token, and no phone signed in: every phone has to scan the new code.</summary>
    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _pairing = MobileToken.Create();
            _pairingHash = Hash(_pairing);
            _phones = [];
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs a phone in: with the pairing token it gets a key of its own; with its key it keeps it.
    /// </summary>
    /// <param name="given">The pairing token, or the key of the phone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The key of the phone, its cookie; null when <paramref name="given"/> is neither.</returns>
    internal async Task<string?> SignInAsync(string? given, CancellationToken cancellationToken)
    {
        if (!MobileToken.IsValid(given)) return null;
        var hash = Hash(given!);
        string key;
        lock (_gate)
        {
            if (Contains(_phones, hash)) return given;
            if (_pairingHash is null || !Same(hash, _pairingHash)) return null;
            key = MobileToken.Create();
            _phones.Add(Hash(key));
            if (_phones.Count > MaximumPhones) _phones.RemoveRange(0, _phones.Count - MaximumPhones);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>Whether a request comes from a phone signed in: <paramref name="given"/> is its key.</summary>
    internal bool IsPhone(string? given)
    {
        if (!MobileToken.IsValid(given)) return false;
        var hash = Hash(given!);
        lock (_gate) return Contains(_phones, hash);
    }

    /// <summary>The SHA-256 of a token, base64url: what the file keeps.</summary>
    internal static string Hash(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Every hash is compared, in constant time: how long a refusal takes says nothing of the keys.
    private static bool Contains(List<string> hashes, string hash)
    {
        var found = false;
        foreach (var known in hashes) found |= Same(hash, known);
        return found;
    }

    private static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static bool IsHash(string? value) => value is { Length: 43 } && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // One write at a time, each of the state of when it starts: the last one written is the last state.
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MobileAccessFile file;
            lock (_gate) file = new() { Pairing = _pairingHash, Phones = [.. _phones] };
            await _state.WriteJsonAsync(PluginStateScope.User, FileName, file, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _saving.Release();
        }
    }
}

/// <summary>What <see cref="MobileAccess"/> keeps: hashes only.</summary>
internal sealed record MobileAccessFile
{
    /// <summary>Gets the SHA-256 of the pairing token, base64url; null before the first pairing.</summary>
    public string? Pairing { get; init; }

    /// <summary>Gets the SHA-256 of the key of each phone signed in, base64url, the oldest first.</summary>
    public IReadOnlyList<string> Phones { get; init; } = [];
}
