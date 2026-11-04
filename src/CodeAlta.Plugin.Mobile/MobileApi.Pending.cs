using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile;

// What the phone reads and takes back of a session, on demand: the prompts that wait in its queue, a prompt it can still
// take back, and one tool call whole (the timeline only says what a call works on).
internal sealed partial class MobileApi
{
    /// <summary>The longest text of a tool call the phone reads: the start of its arguments and of its diff, the end of its output.</summary>
    internal const int MaximumToolText = 12_000;

    // What a person queued, with `session queue` or a send while the session worked. Messages between agents and what the host
    // queues are not the phone's to take back.
    private static readonly string[] QueuedKinds = ["queue", "send"];

    /// <summary>The prompts of a person that wait in the queue of a session, in the order they will be sent.</summary>
    /// <returns>The prompts, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> QueueAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["session", "queued", id], null, cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var item in result.OfType("alta.session.queued.item"))
        {
            if (MobileAlta.Text(item, "queueItemId") is not { } itemId || !QueuedKinds.Contains(MobileAlta.Text(item, "kind"), StringComparer.Ordinal)) continue;
            items.Add(new JsonObject
            {
                ["id"] = itemId, ["text"] = MobileAlta.Text(item, "prompt") ?? "", ["at"] = MobileAlta.Text(item, "createdAt"), ["state"] = MobileAlta.Text(item, "state"),
                ["files"] = item["files"]?.DeepClone() ?? new JsonArray(),
            });
        }

        return new JsonObject { ["items"] = items, ["error"] = result.Error };
    }

    /// <summary>Removes a prompt from the queue of a session: <c>removed</c>, <c>sending</c> when the session is taking it, or <c>not_found</c>.</summary>
    /// <returns>The result, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> UnqueueAsync(string id, string itemId, CancellationToken cancellationToken)
    {
        if (!IsId(itemId) || await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        // Not tied to the request: the phone that gives up does not change what it asked for.
        var result = await MobileAlta.RunAsync(alta, ["session", "unqueue", id, itemId], null, CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["status"] = MobileAlta.Text(result.OfType("alta.session.unqueued").FirstOrDefault(), "status"), ["error"] = result.Error };
    }

    /// <summary>Takes back a prompt steered into a run: <c>withdrawn</c> says whether the model will never have it.</summary>
    /// <returns>The result, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> UnsteerAsync(string id, string withdrawalId, CancellationToken cancellationToken)
    {
        if (!IsId(withdrawalId) || await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["session", "unsteer", id, withdrawalId], null, CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["withdrawn"] = MobileAlta.Flag(result.OfType("alta.session.unsteered").FirstOrDefault(), "withdrawn"), ["error"] = result.Error };
    }

    /// <summary>One tool call of a session, read when the phone opens it: what it runs, in which folder, and what it gave.</summary>
    /// <param name="id">The session.</param>
    /// <param name="activityId">The id of the call.</param>
    /// <param name="since">The sequence number before the first record of the call, so that the turns before it are not read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The call, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> ToolAsync(string id, string activityId, long? since, CancellationToken cancellationToken)
    {
        if (!IsId(activityId) || await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        List<string> args = ["session", "tool", id, activityId, "--max-text", MaximumToolText.ToString(CultureInfo.InvariantCulture)];
        if (since is > 0) args.AddRange(["--since", since.Value.ToString(CultureInfo.InvariantCulture)]);
        var result = await MobileAlta.RunAsync(alta, args, null, cancellationToken).ConfigureAwait(false);
        if (result.OfType("alta.session.tool").FirstOrDefault() is not { } call) return Error(result.Error ?? "The tool call was not read.");

        // `error` says that the read failed, as in every answer: what the call reported is its `failure`.
        var answer = new JsonObject { ["error"] = null, ["failure"] = call["error"]?.DeepClone() };
        foreach (var name in (string[])["activityId", "kind", "phase", "name", "startedAt", "at", "command", "workingDirectory", "sessionFolder", "exitCode",
            "arguments", "argumentsLength", "output", "outputLength", "diff", "diffLength", "readFiles", "modifiedFiles"])
            if (call[name] is { } value) answer[name] = value.DeepClone();
        return answer;
    }
}
