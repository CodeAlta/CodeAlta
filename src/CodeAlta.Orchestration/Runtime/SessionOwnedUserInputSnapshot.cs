using System.Collections.ObjectModel;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The exact original operation, attachment and fresh provider-input attempt. RunId remains provider-supplied.</summary>
public sealed record SessionOwnedUserInputHandle(Guid OperationId, Guid RuntimeInstanceId, long AttachmentGeneration,
    string SessionId, string? RunId, string InteractionId, Guid AttemptId);

/// <summary>A deep immutable, bounded nonsecret form from a live owned execution.</summary>
public sealed record SessionOwnedUserInputSnapshot(SessionOwnedUserInputHandle Handle, string ProviderId, AgentUserInputForm Form);

/// <summary>At most four complete forms; absence is not a decision receipt.</summary>
public sealed record SessionOwnedUserInputPage(IReadOnlyList<SessionOwnedUserInputSnapshot> Entries, bool HasMore);

/// <summary>One literal answer. Pairs permit duplicate detection before constructing a response map.</summary>
public sealed record SessionOwnedUserInputAnswer(string PromptId, string Value);

/// <summary>Bounded literal form and answer validation shared by the owner and its transport adapter.</summary>
public static class OwnedUserInputValidation
{
    /// <summary>Rejects null, malformed UTF-16, NUL and oversized strings; identities additionally require canonical visible spelling.</summary>
    public static bool Text(string? value, int limit, bool identity = false, bool required = false)
    {
        if (value is null || value.Length > limit || ((identity || required) && string.IsNullOrWhiteSpace(value))) return false;
        if (identity && !value.AsSpan().Trim().SequenceEqual(value.AsSpan())) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0' || (identity && char.IsControl(c))) return false;
            if (char.IsSurrogate(c) && (!char.IsHighSurrogate(c) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        }
        return true;
    }

    /// <summary>Copies a complete supported nonsecret form into read-only nested collections, or returns null.</summary>
    public static AgentUserInputForm? Snapshot(AgentUserInputForm? form)
    {
        // Do not enumerate or copy unbounded provider collections. Malformed/mutating collections fail closed.
        try
        {
            if (form?.Prompts is not { } source) return null;
            var promptCount = source.Count;
            if (promptCount is < 1 or > 8) return null;
            var prompts = new AgentUserInputPrompt[promptCount]; var ids = new HashSet<string>(StringComparer.Ordinal); var total = 0;
            for (var i = 0; i < prompts.Length; i++)
            {
                var p = source[i];
                if (p is null || p.IsSecret || !Text(p.Id, 128, identity: true) || !ids.Add(p.Id)
                    || !Text(p.Question, 1024, required: true) || (p.Header is not null && !Text(p.Header, 128))) return null;
                total += p.Id.Length + p.Question.Length + (p.Header?.Length ?? 0);
                var count = p.Options?.Count ?? 0;
                if (count is < 0 or > 8 || (!p.AllowFreeform && count == 0)) return null;
                var options = new AgentUserInputOption[count]; var labels = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < count; j++)
                {
                    var o = p.Options![j];
                    if (o is null || !Text(o.Label, 256, identity: true) || !labels.Add(o.Label)
                        || (o.Description is not null && !Text(o.Description, 512))) return null;
                    total += o.Label.Length + (o.Description?.Length ?? 0);
                    if (total > 8192) return null;
                    options[j] = new(o.Label, o.Description);
                }
                if (total > 8192) return null;
                prompts[i] = new(p.Id, p.Question, p.Header, Array.AsReadOnly(options), p.AllowFreeform, false);
            }
            return new(Array.AsReadOnly(prompts));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Validates duplicate pairs before creating a literal response map against an original validated form, or returns null.</summary>
    public static AgentUserInputResponse? Answers(AgentUserInputForm form, IReadOnlyList<SessionOwnedUserInputAnswer>? answers)
    {
        try
        {
            if (answers is null) return null;
            var count = answers.Count;
            if (count is < 1 or > 8 || count != form.Prompts.Count) return null;
            var pairs = new SessionOwnedUserInputAnswer[count]; var ids = new HashSet<string>(StringComparer.Ordinal); var total = 0;
            for (var i = 0; i < pairs.Length; i++)
            {
                var a = answers[i];
                if (a is null || !Text(a.PromptId, 128, identity: true) || !ids.Add(a.PromptId) || !Text(a.Value, 2048)) return null;
                total += a.Value.Length; if (total > 8192) return null;
                var prompt = form.Prompts.FirstOrDefault(p => p.Id == a.PromptId);
                if (prompt is null || (!prompt.AllowFreeform && !prompt.Options!.Any(o => o.Label == a.Value))) return null;
                pairs[i] = new(a.PromptId, a.Value);
            }
            return new(new ReadOnlyDictionary<string, string>(pairs.ToDictionary(a => a.PromptId, a => a.Value, StringComparer.Ordinal)));
        }
        catch (Exception) { return null; }
    }
}
