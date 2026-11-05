using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The receipts of the side effects of the node API the runtime does not deduplicate itself (a creation, a queued prompt,
/// an answer), by idempotency key (<c>doc/fleet/protocol.md</c>, Durability). The first call of a key runs; a retry with
/// the same parameters gets its result, with others a conflict; a key whose result was evicted, or whose call a stop of
/// the node interrupted, answers <see cref="FleetErrorCodes.Expired"/> and never runs again. A call that failed is tried
/// again: it failed before its effect.
/// </summary>
/// <remarks>
/// Kept in <c>fleet/retry-receipts.json</c>, written at the start and at the end of each call, so that a restart forgets
/// nothing: the results of the latest calls (<c>resultCapacity</c>), and every key for <see cref="Lifetime"/>, the
/// lifetime of a retry token. A key is never forgotten within its lifetime: once its result is evicted it stays as a
/// tombstone, the digest of the key and its time. When <c>keyCapacity</c> keys are within their lifetime, a new key is
/// refused (<see cref="FleetErrorCodes.ServerBusy"/>, retry after the oldest expires) rather than forget one a client may
/// still retry. The file holds at most <c>keyCapacity</c> entries, of about 80 bytes each without a result, and
/// <c>resultCapacity</c> results of at most 16 KiB: with the defaults, about 1.3 MiB of keys and 4 MiB of results. Keys
/// and parameters are kept as digests only.
/// </remarks>
internal sealed class FleetRetryReceipts
{
    /// <summary>The name of the file in the fleet folder.</summary>
    public const string FileName = "retry-receipts.json";

    /// <summary>How many results are kept by default.</summary>
    public const int DefaultResultCapacity = 256;

    /// <summary>How many keys are kept by default, with or without their result: the most calls with a key in <see cref="Lifetime"/>.</summary>
    public const int DefaultKeyCapacity = 16_384;

    /// <summary>How long a key is remembered: a client never retries a key older than this.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private const int FileVersion = 1;

    // A result larger than this is not kept: a retry of its key answers expired.
    private const int MaxResultBytes = 16 * 1024;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly int _resultCapacity;
    private readonly int _keyCapacity;
    private readonly Action<string>? _warn;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    // Oldest first.
    private readonly LinkedList<Receipt> _order = new();
    private int _results;

    /// <summary>Opens the receipts kept in a fleet folder.</summary>
    /// <param name="fleetRoot">The fleet folder.</param>
    /// <param name="time">The clock of the lifetime of the keys.</param>
    /// <param name="resultCapacity">How many results are kept.</param>
    /// <param name="keyCapacity">How many keys within their lifetime are kept, at least as many as results; past it, new keys are refused.</param>
    /// <param name="warn">Told when the file cannot be read or written; null for nothing.</param>
    public FleetRetryReceipts(string fleetRoot, TimeProvider time, int resultCapacity = DefaultResultCapacity, int keyCapacity = DefaultKeyCapacity,
        Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(resultCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(keyCapacity, resultCapacity);
        _path = Path.Combine(Path.GetFullPath(fleetRoot), FileName);
        _time = time;
        _resultCapacity = resultCapacity;
        _keyCapacity = keyCapacity;
        _warn = warn;
        Load();
    }

    /// <summary>Runs a side effect once for its key.</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="key">The key, namespaced by the method and the device.</param>
    /// <param name="parameters">The parameters of the call, as they came.</param>
    /// <param name="type">How the result is kept.</param>
    /// <param name="run">The side effect.</param>
    /// <returns>The result of the first call of the key.</returns>
    /// <exception cref="FleetException">
    /// The key was used for other parameters, or its receipt expired; or the node holds as many keys within their lifetime
    /// as it keeps (<see cref="FleetErrorCodes.ServerBusy"/>, with when to retry).
    /// </exception>
    public Task<T> RunOnceAsync<T>(string key, string parameters, JsonTypeInfo<T> type, Func<Task<T>> run) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(run);
        var digest = Digest(key);
        var fingerprint = Digest(parameters ?? "");
        Receipt receipt;
        Task<T> work;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_receipts.TryGetValue(digest, out var kept))
            {
                // A tombstone no longer knows its parameters: whatever they are, it never runs again.
                if (kept.Fingerprint is not null && kept.Fingerprint != fingerprint)
                    throw new FleetException(FleetErrorCodes.InvalidRequest, null, "The idempotency key was already used for another request.");
                if (kept.Work is { } shared && !shared.IsFaulted && !shared.IsCanceled) return Replay(shared);
                if (kept.Work is null)
                {
                    if (kept.Result is { } result && TryRead(result, type) is { } value) return Task.FromResult(value);
                    throw new FleetException(FleetErrorCodes.Expired, null, "The receipt of this idempotency key expired: the request is not run again.");
                }

                // Failed before its effect: tried again.
                Remove(kept);
            }

            if (_receipts.Count >= _keyCapacity) throw Full(now);
            receipt = new Receipt(digest, fingerprint, now);
            receipt.Node = _order.AddLast(receipt);
            _receipts.Add(digest, receipt);
            Evict();
            if (!TrySave())
            {
                // Not recorded, not run: a retry after a restart must never find the call unknown when it ran.
                Remove(receipt);
                throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, "The node cannot record the request.");
            }

            work = Task.Run(run);
            receipt.Work = Box(work);

            // A failed call forgets its receipt, so nothing awaits its boxed task again: observe its failure here (the caller
            // gets it from the call itself), or the finalizer reports it as unobserved.
            _ = receipt.Work.ContinueWith(static boxed => _ = boxed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // The caller gets the result once its receipt is recorded: a retry after the answer always finds it.
        return Recorded(work, SettleAsync(receipt, work, type));

        static async Task<T> Replay(Task<object> shared) => (T)await shared.ConfigureAwait(false);
        static async Task<object> Box(Task<T> value) => await value.ConfigureAwait(false);
        static async Task<T> Recorded(Task<T> value, Task settled)
        {
            await settled.ConfigureAwait(false);
            return await value.ConfigureAwait(false);
        }
    }

    // Keeps the result of a call that succeeded (or forgets one that failed), once it ends.
    private async Task SettleAsync<T>(Receipt receipt, Task<T> work, JsonTypeInfo<T> type) where T : class
    {
        T? result = null;
        try
        {
            result = await work.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The caller gets the failure from the call itself.
        }

        lock (_gate)
        {
            if (!_receipts.TryGetValue(receipt.Key, out var kept) || !ReferenceEquals(kept, receipt)) return;
            if (result is null)
            {
                Remove(receipt);
            }
            else
            {
                var json = JsonSerializer.SerializeToElement(result, type);
                receipt.Result = json.GetRawText().Length <= MaxResultBytes ? json : null;
                if (receipt.Result is null) receipt.Bury();
                else _results++;
                receipt.Settled = true;
                Evict();
            }

            TrySave();
        }
    }

    // The results past capacity become tombstones (a retry answers expired). Keys are forgotten only past their lifetime
    // (Prune), never for room: a full store refuses new keys instead (Full).
    private void Evict()
    {
        for (var node = _order.First; node is not null && _results > _resultCapacity; node = node.Next)
        {
            if (node.Value.Result is null) continue;
            node.Value.Result = null;
            node.Value.Bury();
            _results--;
        }
    }

    // A new key while the store holds as many keys within their lifetime as it keeps: retried once the oldest expires.
    private FleetException Full(DateTimeOffset now)
    {
        var oldest = _order.FirstOrDefault(static receipt => !receipt.IsRunning)?.At ?? now;
        var wait = Math.Clamp(Math.Ceiling((oldest + Lifetime - now).TotalSeconds), 1, int.MaxValue);
        return new FleetException(new FleetError(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry,
            $"The node holds the {_keyCapacity} idempotency keys it keeps for their lifetime: the request was not run, retry it later.", (int)wait));
    }

    private void Prune(DateTimeOffset now)
    {
        var horizon = now - Lifetime;
        for (var node = _order.First; node is not null && node.Value.At < horizon;)
        {
            var next = node.Next;
            if (!node.Value.IsRunning) Remove(node.Value);
            node = next;
        }
    }

    private void Remove(Receipt receipt)
    {
        if (!_receipts.Remove(receipt.Key)) return;
        if (receipt.Node is { } node) _order.Remove(node);
        if (receipt.Result is not null) _results--;
    }

    private bool TrySave()
    {
        var file = new RetryReceiptsFile
        {
            Version = FileVersion,
            Receipts = [.. _order.Select(static receipt => new RetryReceiptEntry
            {
                Key = receipt.Key,
                Fingerprint = receipt.Fingerprint,
                At = receipt.At,
                Running = receipt.IsRunning ? true : null,
                Result = receipt.Result,
            })],
        };
        try
        {
            FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(file, FleetRetryReceiptsJsonContext.Default.RetryReceiptsFile));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"The retry receipts of the node cannot be written to {_path}: {exception.Message}");
            return false;
        }
    }

    // A call that was running when the node stopped is a key without a result: whether its effect happened is not known.
    private void Load()
    {
        RetryReceiptsFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetRetryReceiptsJsonContext.Default.RetryReceiptsFile);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn?.Invoke($"The retry receipts of the node cannot be read from {_path}: {exception.Message}");
            return;
        }

        if (file is not { Version: FileVersion } || file.Receipts is null)
        {
            _warn?.Invoke($"The retry receipts of the node in {_path} have an unknown version.");
            return;
        }

        foreach (var entry in file.Receipts.OrderBy(static entry => entry.At))
        {
            if (string.IsNullOrEmpty(entry.Key) || _receipts.ContainsKey(entry.Key)) continue;
            var result = entry.Running == true || string.IsNullOrEmpty(entry.Fingerprint) || entry.Result is not { ValueKind: JsonValueKind.Object } ? null : entry.Result;
            var receipt = new Receipt(entry.Key, string.IsNullOrEmpty(entry.Fingerprint) ? null : entry.Fingerprint, entry.At) { Result = result, Settled = true };
            // A call a stop interrupted, or a key whose result was evicted: a tombstone.
            if (result is null) receipt.Bury();
            receipt.Node = _order.AddLast(receipt);
            _receipts.Add(receipt.Key, receipt);
            if (receipt.Result is not null) _results++;
        }

        Prune(_time.GetUtcNow());
        Evict();
    }

    private static T? TryRead<T>(JsonElement result, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return result.Deserialize(type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Digest(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToBase64String(hash[..18]);
    }

    private sealed class Receipt(string key, string? fingerprint, DateTimeOffset at)
    {
        public string Key { get; } = key;

        // The digest of the parameters; null for a tombstone, which never runs again whatever they are.
        public string? Fingerprint { get; private set; } = fingerprint;

        public DateTimeOffset At { get; } = at;

        public LinkedListNode<Receipt>? Node { get; set; }

        // The call in this process, while it runs and while its result is kept; null after a restart or once evicted.
        public Task<object>? Work { get; set; }

        public JsonElement? Result { get; set; }

        // Whether the end of the call was recorded.
        public bool Settled { get; set; }

        public bool IsRunning => !Settled;

        // Keeps only what makes a retry answer expired until the key's lifetime ends: its digest and its time.
        public void Bury()
        {
            Work = null;
            Fingerprint = null;
        }
    }
}

/// <summary>The file of the retry receipts of a node.</summary>
internal sealed class RetryReceiptsFile
{
    /// <summary>Gets or sets the version of the file.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Gets or sets the receipts, oldest first.</summary>
    [JsonPropertyName("receipts")]
    public List<RetryReceiptEntry>? Receipts { get; set; }
}

/// <summary>One receipt of the file.</summary>
internal sealed class RetryReceiptEntry
{
    /// <summary>Gets or sets the digest of the key.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>Gets or sets the digest of the parameters; null for a tombstone, a key kept without its result.</summary>
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; set; }

    /// <summary>Gets or sets when the call came.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets whether the call was running when the file was written.</summary>
    [JsonPropertyName("running")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Running { get; set; }

    /// <summary>Gets or sets the result, while it is kept.</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }
}

[JsonSerializable(typeof(RetryReceiptsFile))]
internal sealed partial class FleetRetryReceiptsJsonContext : JsonSerializerContext;
