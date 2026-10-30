using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile;

internal sealed partial class MobileApi
{
    /// <summary>The background jobs of a visible session, including recent results as on the desktop.</summary>
    internal async Task<JsonObject?> JobsAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await MobileAlta.RunAsync(alta, ["job", "list", "--session", id], null, cancellationToken).ConfigureAwait(false);
        var jobs = new JsonArray();
        foreach (var job in result.OfType("alta.job"))
        {
            if (MobileAlta.Text(job, "sessionId") != id) continue;
            if (MobileAlta.Text(job, "state") != "running"
                && (!DateTimeOffset.TryParse(MobileAlta.Text(job, "endedAt"), out var ended) || _clock.GetUtcNow() - ended >= TimeSpan.FromMinutes(10))) continue;
            jobs.Add(job.DeepClone());
        }

        return new JsonObject { ["jobs"] = jobs, ["error"] = result.Error };
    }

    /// <summary>Reads a bounded tail or stops a job, after checking both its session and its ownership.</summary>
    internal async Task<JsonObject?> JobAsync(string id, string jobId, bool stop, CancellationToken cancellationToken)
    {
        if (!IsId(jobId) || await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var status = await MobileAlta.RunAsync(alta, ["job", "status", jobId], null, cancellationToken).ConfigureAwait(false);
        var job = status.OfType("alta.job.status").FirstOrDefault();
        if (job is null || MobileAlta.Text(job, "sessionId") != id) return null;
        var result = await MobileAlta.RunAsync(alta, stop ? ["job", "cancel", jobId] : ["job", "output", jobId, "--lines", "200"], null, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) return Error(result.Error);
        if (stop) return new JsonObject { ["error"] = null };
        var output = result.OfType("alta.job.output").FirstOrDefault();
        return output is null ? Error("The output of the job was not read.") : new JsonObject
        {
            ["job"] = job.DeepClone(), ["text"] = MobileAlta.Text(output, "text") ?? "", ["truncated"] = MobileAlta.Flag(output, "truncated"), ["error"] = null,
        };
    }
}
