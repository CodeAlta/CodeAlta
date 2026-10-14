using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The questions of runs whose conversation another process holds (a Claude Code conversation open in VS Code or a
/// terminal): the page lists those of a session and answers one with a choice it offered. An answer names the exact
/// question (runtime, attachment, run, request and attempt) of the host epoch the page knows.
/// </summary>
[NeoRpcService("sessionConversationHeld", Version = 1)]
internal sealed class ConversationHeldService
{
    private const int MaximumEntries = 4;
    private readonly Func<string, CancellationToken, ValueTask<IReadOnlyList<SessionConversationHeldSnapshot>>> _list;
    private readonly Func<SessionConversationHeldHandle, AgentConversationHeldChoice, CancellationToken, ValueTask<bool>> _resolve;
    private readonly string _epoch;

    internal ConversationHeldService(SessionConversationHeldService owner, string epoch)
        : this(owner.ListAsync, owner.ResolveAsync, epoch) { }

    // Transport-test adapters; never constructs a host or a provider.
    internal ConversationHeldService(Func<string, CancellationToken, ValueTask<IReadOnlyList<SessionConversationHeldSnapshot>>> list,
        Func<SessionConversationHeldHandle, AgentConversationHeldChoice, CancellationToken, ValueTask<bool>> resolve, string epoch)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(resolve);
        if (!GuidValue(epoch, out _)) throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _list = list;
        _resolve = resolve;
        _epoch = epoch;
    }

    /// <summary>Lists the questions that wait for the user in a session, at most four, oldest first.</summary>
    [NeoRpcMethod("list")]
    public async Task<ConversationHeldPage> ListAsync(ConversationHeldListRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !Identity(request.SessionId)) return new("invalid_request", _epoch, null, []);
        ConversationHeldPage Error(string status) => new(status, _epoch, request.SessionId, []);
        if (request.ExpectedHostEpoch != _epoch) return Error("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var page = await _list(request.SessionId, cancellationToken).ConfigureAwait(false);
            if (page is null || page.Count > MaximumEntries) return Error("wire_limit");
            var entries = new List<ConversationHeldEntry>(page.Count);
            foreach (var entry in page)
            {
                if (entry?.Handle is not { } handle || handle.SessionId != request.SessionId || entry.Holder is not { } holder
                    || !Identity(entry.ProviderId) || !Identity(entry.ConversationId)) return Error("wire_limit");
                var wire = Wire(handle);
                if (!TryHandle(wire, out _)) return Error("wire_limit");
                entries.Add(new(wire, entry.ProviderId, entry.Timestamp.ToString("O", CultureInfo.InvariantCulture), entry.ConversationId,
                    new ConversationHeldHolder(OriginName(holder.Origin), Cut(holder.OriginText, 256) ?? string.Empty, holder.ProcessId,
                        Cut(holder.Entrypoint, 128), Cut(holder.Kind, 128), Cut(holder.Status, 128), Cut(holder.WorkingDirectory, 1024), Cut(holder.Name, 256)),
                    [.. entry.Choices.Select(ChoiceName)]));
            }

            return new("ok", _epoch, request.SessionId, [.. entries]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Error("read_failed"); }
    }

    /// <summary>Answers a question with one of the choices it offered: <c>wait</c>, <c>continueAsCopy</c>, <c>openAnyway</c> or <c>cancel</c>.</summary>
    [NeoRpcMethod("resolve")]
    public async Task<ConversationHeldResult> ResolveAsync(ConversationHeldResolveRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !TryHandle(request.Handle, out var handle) || ParseChoice(request.Choice) is not { } choice)
            return new("invalid_request", _epoch, null);
        if (request.ExpectedHostEpoch != _epoch) return new("stale_epoch", _epoch, request.Handle);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var accepted = await _resolve(handle!, choice, cancellationToken).ConfigureAwait(false);
            return new(accepted ? "resolved" : "rejected", _epoch, request.Handle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // A failure is not proof that nothing was decided: no retry, no raw exception content.
        catch (Exception) { return new("uncertain", _epoch, request.Handle); }
    }

    internal static string ChoiceName(AgentConversationHeldChoice choice) => choice switch
    {
        AgentConversationHeldChoice.Wait => "wait",
        AgentConversationHeldChoice.ContinueAsCopy => "continueAsCopy",
        AgentConversationHeldChoice.OpenAnyway => "openAnyway",
        _ => "cancel",
    };

    internal static AgentConversationHeldChoice? ParseChoice(string? choice) => choice switch
    {
        "wait" => AgentConversationHeldChoice.Wait,
        "continueAsCopy" => AgentConversationHeldChoice.ContinueAsCopy,
        "openAnyway" => AgentConversationHeldChoice.OpenAnyway,
        "cancel" => AgentConversationHeldChoice.Cancel,
        _ => null,
    };

    private static string OriginName(AgentConversationHolderOrigin origin) => origin switch
    {
        AgentConversationHolderOrigin.VsCode => "vsCode",
        AgentConversationHolderOrigin.Terminal => "terminal",
        AgentConversationHolderOrigin.OtherApplication => "otherApplication",
        AgentConversationHolderOrigin.BackgroundSession => "backgroundSession",
        _ => "unknown",
    };

    internal static ConversationHeldHandle Wire(SessionConversationHeldHandle value)
        => new(value.RuntimeInstanceId.ToString("D"), value.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), value.SessionId,
            value.RunId, value.RequestId, value.AttemptId.ToString("D"));

    internal static bool TryHandle(ConversationHeldHandle? value, out SessionConversationHeldHandle? handle)
    {
        handle = null;
        if (value is null || !GuidValue(value.RuntimeInstanceId, out var runtime) || !GuidValue(value.AttemptId, out var attempt)
            || !Identity(value.SessionId) || !Identity(value.RequestId) || (value.RunId is not null && !Identity(value.RunId))
            || value.AttachmentGeneration is not { Length: >= 1 and <= 16 }
            || !long.TryParse(value.AttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation is <= 0 or > 9007199254740991 || generation.ToString(CultureInfo.InvariantCulture) != value.AttachmentGeneration) return false;
        handle = new(runtime, generation, value.SessionId, value.RunId, value.RequestId, attempt);
        return true;
    }

    private static bool GuidValue(string? value, out Guid guid) => Guid.TryParseExact(value, "D", out guid) && guid != Guid.Empty && guid.ToString("D") == value;
    private static bool Identity(string? value) => OwnedUserInputValidation.Text(value, 128, identity: true);

    // What the registry of the other process says is shown, cut to a bound; text that is not valid UTF-16 is dropped.
    private static string? Cut(string? value, int limit)
    {
        if (value is null) return null;
        var cut = value.Length > limit ? value[..limit] : value;
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return OwnedUserInputValidation.Text(cut, limit) ? cut : null;
    }
}

/// <summary>Asks for the questions of a session.</summary>
internal sealed record ConversationHeldListRequest(string ExpectedHostEpoch, string SessionId);

/// <summary>The exact identity of a question, which an answer names.</summary>
internal sealed record ConversationHeldHandle(string RuntimeInstanceId, string AttachmentGeneration, string SessionId, string? RunId,
    string RequestId, string AttemptId);

/// <summary>
/// The process that holds the conversation. <paramref name="Origin"/> is <c>vsCode</c>, <c>terminal</c>,
/// <c>otherApplication</c>, <c>backgroundSession</c> or <c>unknown</c>; <paramref name="OriginText"/> says it in English.
/// </summary>
internal sealed record ConversationHeldHolder(string Origin, string OriginText, int? ProcessId, string? Entrypoint, string? Kind,
    string? Status, string? WorkingDirectory, string? Name);

/// <summary>One question: the conversation, who holds it, and the choices offered in their order.</summary>
internal sealed record ConversationHeldEntry(ConversationHeldHandle Handle, string ProviderId, string Timestamp, string ConversationId,
    ConversationHeldHolder Holder, string[] Choices);

/// <summary>The questions of a session; <paramref name="Status"/> is <c>ok</c> or why there are none to show.</summary>
internal sealed record ConversationHeldPage(string Status, string HostEpoch, string? SessionId, ConversationHeldEntry[] Entries);

/// <summary>Answers a question.</summary>
internal sealed record ConversationHeldResolveRequest(string ExpectedHostEpoch, ConversationHeldHandle Handle, string Choice);

/// <summary>
/// What became of an answer: <c>resolved</c>, <c>rejected</c> (the question ended, was answered, or did not offer the
/// choice), <c>stale_epoch</c>, <c>invalid_request</c> or <c>uncertain</c>.
/// </summary>
internal sealed record ConversationHeldResult(string Status, string HostEpoch, ConversationHeldHandle? Handle);
