using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Diffing;
using CodeAlta.Agent.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class ToolCallsService
{
    /// <summary>
    /// Gets the read of a system prompt record with the one it replaced; null serves no prompt.
    /// </summary>
    internal Func<string, long, CancellationToken, Task<AgentSystemPromptRecord>>? Prompts { get; init; }

    /// <summary>
    /// Returns a system prompt record of a session, whole, with the prompt it replaced; or the next part of one
    /// of its texts; or only the lines the change added and removed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    [NeoRpcMethod("readPrompt")]
    public async Task<SystemPromptResponse> ReadPromptAsync(SystemPromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null || Prompts is null) return PromptRefused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return PromptRefused("stale_epoch");
        if (!Identity(request.SessionId) || !Offset(request.Offset, out var offset) || request.Position < 0
            || request.Part is not (null or "system" or "developer" or "change") || request.Part is null or "change" && request.Position != 0)
            return PromptRefused("invalid");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // The rest of a text names its own record, this one or the one before it: nothing is looked for again.
            if (request.Part is "system" or "developer")
            {
                if (await ReadRecordAsync(request.SessionId, offset, cancellationToken).ConfigureAwait(false) is not AgentSystemPromptEvent continued)
                    return PromptRefused("missing_record");
                var text = request.Part == "system" ? continued.SystemMessage : continued.DeveloperInstructions;
                return text is null || request.Position > text.Length ? PromptRefused("invalid") : new("ok", null, Part(text, request.Position), null);
            }

            var record = await AdmitAsync(() => Prompts(request.SessionId, offset, cancellationToken), cancellationToken).ConfigureAwait(false);
            var prompt = record.Prompt;
            var change = record.Previous is null ? null : SystemPromptProjection.Change(record.Previous, prompt);
            if (request.Part == "change") return new("ok", null, null, change);
            var usage = prompt.AgentPromptUsage;
            return new("ok", new(SystemPromptProjection.IsChange(prompt), prompt.EffectivePromptHash, SystemPromptProjection.Name(prompt), usage?.SourcePath,
                prompt.Timestamp, prompt.Statistics.TotalApproxTokens, prompt.Statistics.SystemApproxTokens, prompt.Statistics.DeveloperApproxTokens,
                prompt.ProviderPayloadSummary.AppliedToProvider, prompt.ProviderPayloadSummary.Lossy,
                Part(prompt.SystemMessage, 0), Part(prompt.DeveloperInstructions, 0),
                record.Previous is { } previous && record.PreviousOffset is { } previousOffset
                    ? new(previousOffset.ToString(CultureInfo.InvariantCulture), previous.Timestamp, Part(previous.SystemMessage, 0), Part(previous.DeveloperInstructions, 0))
                    : null,
                change), null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ToolCallRefusal refusal) { return PromptRefused(refusal.Status); }
    }

    private static SystemPromptResponse PromptRefused(string status) => new(status, null, null, null);
}

/// <summary>What a system prompt record says to the timeline and to its window, apart from its texts.</summary>
internal static class SystemPromptProjection
{
    /// <summary>
    /// The most UTF-16 code units two prompts may have together for their changed lines to be counted: beyond it
    /// the count is left unknown rather than computed on a history page.
    /// </summary>
    internal const int MaximumComparedCharacters = 2 * 1024 * 1024;

    /// <summary>Whether a record replaced an earlier prompt of its session.</summary>
    internal static bool IsChange(AgentSystemPromptEvent prompt) => !string.Equals(prompt.Change.Kind, "initial", StringComparison.OrdinalIgnoreCase);

    /// <summary>The agent prompt of a record, by the name a user knows it by.</summary>
    internal static string? Name(AgentSystemPromptEvent prompt)
        => Text(prompt.AgentPromptUsage?.DisplayName) ?? Text(prompt.AgentPromptUsage?.PromptName) ?? Text(prompt.AgentPromptId);

    /// <summary>Counts the lines a prompt added to and removed from the one before it, when both are small enough to compare.</summary>
    /// <param name="previous">The prompt that was replaced.</param>
    /// <param name="prompt">The prompt that replaced it.</param>
    internal static SystemPromptChange? Change(AgentSystemPromptEvent previous, AgentSystemPromptEvent prompt)
    {
        var before = Document(previous);
        var after = Document(prompt);
        if ((long)before.Length + after.Length > MaximumComparedCharacters) return null;
        var (added, removed) = UnifiedDiffBuilder.CountChangedLines(before, after);
        return new(added, removed);
    }

    // The two parts as the window compares them: one under the other, each trimmed, apart by a line no prompt has.
    private static string Document(AgentSystemPromptEvent prompt)
        => $"{prompt.SystemMessage?.Trim()}\n\u0001\n{prompt.DeveloperInstructions?.Trim()}";

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Asks for a system prompt record of a session, for the next part of one of its texts, or for the size of its change.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The selected session.</param>
/// <param name="Offset">
/// The journal offset of the record, as its history row reported it; with a <paramref name="Part"/> that continues
/// a text, the offset of the record that has the text, which may be the previous prompt of a reply.
/// </param>
/// <param name="Part">
/// Null for the record with the one before it; <c>system</c> or <c>developer</c> to continue that text;
/// <c>change</c> for the added and removed lines alone.
/// </param>
/// <param name="Position">Where the part starts in its text, in UTF-16 code units; 0 unless a text is continued.</param>
internal sealed record SystemPromptRequest(string ExpectedEpoch, string SessionId, string Offset, string? Part, int Position);

/// <summary>A system prompt record, the next part of one of its texts, the size of its change, or the reason none is served.</summary>
/// <param name="Status">
/// <c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>capacity</c>, <c>closed</c>,
/// <c>missing_session</c>, <c>missing_record</c>, <c>too_large</c> or <c>read_failed</c>.
/// </param>
/// <param name="Prompt">The record, for a request without a part; null otherwise.</param>
/// <param name="Text">The part, for a request that continues a text; null otherwise.</param>
/// <param name="Change">The size of the change, for a <c>change</c> request about a prompt that replaced one; null otherwise.</param>
internal sealed record SystemPromptResponse(string Status, SystemPromptView? Prompt, ToolCallText? Text, SystemPromptChange? Change);

/// <summary>What a system prompt record says.</summary>
/// <param name="Changed">Whether the prompt replaced an earlier one of its session.</param>
/// <param name="Hash">The hash of the prompt.</param>
/// <param name="Name">The agent prompt, by its display name or its identifier.</param>
/// <param name="SourcePath">The file of the agent prompt, when the record names one.</param>
/// <param name="Timestamp">The time of the record.</param>
/// <param name="TotalTokens">The approximate size of the prompt, in tokens.</param>
/// <param name="SystemTokens">The approximate size of the system message, in tokens.</param>
/// <param name="DeveloperTokens">The approximate size of the developer instructions, in tokens.</param>
/// <param name="Applied">Whether the prompt was sent to the provider.</param>
/// <param name="Lossy">Whether the provider could not take the prompt as it is written.</param>
/// <param name="System">The system message.</param>
/// <param name="Developer">The developer instructions.</param>
/// <param name="Previous">The prompt this one replaced, when the journal has it.</param>
/// <param name="Change">The lines added to and removed from <paramref name="Previous"/>, when they were counted.</param>
internal sealed record SystemPromptView(bool Changed, string Hash, string? Name, string? SourcePath, DateTimeOffset Timestamp,
    int TotalTokens, int SystemTokens, int DeveloperTokens, bool Applied, bool Lossy, ToolCallText? System, ToolCallText? Developer,
    SystemPromptPrevious? Previous, SystemPromptChange? Change);

/// <summary>The prompt a system prompt record replaced.</summary>
/// <param name="Offset">The journal offset of its record, by which the rest of its texts is asked for.</param>
/// <param name="Timestamp">The time of its record.</param>
/// <param name="System">Its system message.</param>
/// <param name="Developer">Its developer instructions.</param>
internal sealed record SystemPromptPrevious(string Offset, DateTimeOffset Timestamp, ToolCallText? System, ToolCallText? Developer);

/// <summary>The lines a prompt added to and removed from the one before it.</summary>
/// <param name="Added">The added lines.</param>
/// <param name="Removed">The removed lines.</param>
internal sealed record SystemPromptChange(int Added, int Removed);
