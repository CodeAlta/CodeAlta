using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// What a request of a session waits for before it goes out.
/// </summary>
public enum AgentWaitKind
{
    /// <summary>A free request of its provider (<c>max_concurrent_requests</c>).</summary>
    ProviderRequest,

    /// <summary>A free slot of the model server that serves its model.</summary>
    ModelServerSlot,
}

/// <summary>
/// The notice of a session whose request waits in a queue: one session update that says the place of the request, is
/// replaced each time the place changes, and is removed once the request goes out.
/// </summary>
/// <remarks>
/// <para>
/// Each update is an <see cref="AgentSessionUpdateKind.Info"/> update, never kept by the journal, whose details name the
/// wait (<c>wait</c>, <c>waitId</c>, <c>name</c>, <c>before</c>, <c>state</c>): the timeline shows the latest one of a
/// <c>waitId</c> while its <c>state</c> is <c>waiting</c>, and none once it is <c>ended</c>.
/// </para>
/// <para>One instance follows one wait; it is not meant to be used from several threads at once.</para>
/// </remarks>
public sealed class AgentWaitNotice
{
    /// <summary>The <c>state</c> of the details of a wait still going on.</summary>
    public const string WaitingState = "waiting";

    /// <summary>The <c>state</c> of the details of a wait that ended.</summary>
    public const string EndedState = "ended";

    private readonly Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> _onSessionUpdate;
    private int? _reported;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentWaitNotice"/> class.
    /// </summary>
    /// <param name="kind">What the request waits for.</param>
    /// <param name="name">The name of what it waits for, such as the key of a provider or <c>pc-ai/coder</c>.</param>
    /// <param name="onSessionUpdate">Gives the updates to the session.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="onSessionUpdate"/> is <see langword="null"/>.</exception>
    public AgentWaitNotice(
        AgentWaitKind kind,
        string name,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        Kind = kind;
        Name = name;
        _onSessionUpdate = onSessionUpdate;
    }

    /// <summary>Gets what the request waits for.</summary>
    public AgentWaitKind Kind { get; }

    /// <summary>Gets the name of what it waits for.</summary>
    public string Name { get; }

    /// <summary>Gets the identifier of this wait, the same in each of its updates.</summary>
    public string WaitId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets whether the session was told the request waits, and not yet that it ended.</summary>
    public bool IsShown => _reported is not null;

    /// <summary>
    /// Says the place of the request: how many requests are before it. Nothing is said when the place did not change.
    /// </summary>
    /// <param name="before">The number of requests before this one.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="before"/> is negative.</exception>
    public async ValueTask ReportAsync(int before, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(before);
        if (_reported == before)
        {
            return;
        }

        await _onSessionUpdate(CreateUpdate(Kind, WaitId, Name, before), cancellationToken).ConfigureAwait(false);
        _reported = before;
    }

    /// <summary>
    /// Removes the notice, when it was shown: the request went out, or stopped waiting. It is said even when the wait
    /// was cancelled; a session that cannot take it any more is left as it is.
    /// </summary>
    public async ValueTask EndAsync()
    {
        if (_reported is null)
        {
            return;
        }

        _reported = null;
        try
        {
            await _onSessionUpdate(CreateUpdate(Kind, WaitId, Name, before: null), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Creates the update of a wait.
    /// </summary>
    /// <param name="kind">What the request waits for.</param>
    /// <param name="waitId">The identifier of the wait.</param>
    /// <param name="name">The name of what it waits for.</param>
    /// <param name="before">The number of requests before it, or <see langword="null"/> when the wait ended.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentException"><paramref name="waitId"/> or <paramref name="name"/> is empty.</exception>
    public static AgentTurnSessionUpdate CreateUpdate(AgentWaitKind kind, string waitId, string name, int? before)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var what = kind == AgentWaitKind.ModelServerSlot ? "slot" : "request";
        var message = before switch
        {
            null => $"Got a free {what} of {name}",
            0 => $"Waits for a free {what} of {name} (next in line)",
            1 => $"Waits for a free {what} of {name} (1 before it)",
            _ => $"Waits for a free {what} of {name} ({before.Value.ToString(CultureInfo.InvariantCulture)} before it)",
        };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("wait", kind == AgentWaitKind.ModelServerSlot ? "model-server-slot" : "provider-request");
            writer.WriteString("waitId", waitId);
            writer.WriteString("name", name);
            if (before is { } count)
            {
                writer.WriteNumber("before", count);
            }

            writer.WriteString("state", before is null ? EndedState : WaitingState);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return new AgentTurnSessionUpdate
        {
            Kind = AgentSessionUpdateKind.Info,
            Message = message,
            Details = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// Returns whether the details of a session update are those of a wait that ended.
    /// </summary>
    /// <param name="details">The details of the update.</param>
    /// <returns><see langword="true"/> for the end of a wait; otherwise <see langword="false"/>.</returns>
    public static bool IsEnded(JsonElement? details)
        => details is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty("waitId", out _) &&
           value.TryGetProperty("state", out var state) &&
           state.ValueKind == JsonValueKind.String &&
           state.ValueEquals(EndedState);
}
