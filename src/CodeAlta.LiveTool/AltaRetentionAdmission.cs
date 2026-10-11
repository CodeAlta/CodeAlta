using CodeAlta.Agent;
using CodeAlta.Plugins;

namespace CodeAlta.LiveTool;

internal static class AltaRetentionAdmission
{
    internal static bool ContainsRetainedDependency(Exception failure)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AgentDependencyRetentionException or PluginEventDependencyException) return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    internal static void ClaimSessionWork(this AltaCommandContext context, string? sessionId)
    {
        if (context.RetentionLifetime is not { } lifetime)
        {
            if (context.Services.Get<ISessionRetentionAdmission>() is not null)
                throw new InvalidOperationException("Retention admission requires AltaCommandRegistry.InvokeAsync.");
            return;
        }
        lifetime.ClaimTarget(context.Services, sessionId);
        // The invocation releases claims after classifying the original callback, not when a
        // nested async handler unwinds and await has exposed only its first failure.
    }

    internal static async Task<T> ObserveOriginalAsync<T>(this AltaCommandContext context, Task<T> original)
    {
        try { return await original.ConfigureAwait(false); }
        catch (Exception failure)
        {
            var evidence = original.Exception ?? failure;
            context.RetentionLifetime?.ObserveFailure(evidence);
            if (ContainsRetainedDependency(evidence))
                throw new AgentDependencyRetentionException("alta command", "original callback", [evidence], original);
            throw;
        }
    }

    internal static async Task ObserveOriginalAsync(this AltaCommandContext context, Task original)
    {
        try { await original.ConfigureAwait(false); }
        catch (Exception failure)
        {
            var evidence = original.Exception ?? failure;
            context.RetentionLifetime?.ObserveFailure(evidence);
            if (ContainsRetainedDependency(evidence))
                throw new AgentDependencyRetentionException("alta command", "original callback", [evidence], original);
            throw;
        }
    }

    // Optional for hosts without automatic retention. A registered owner's refusal is never optional.
    // Null identity means preparation has not discovered its target yet, not a non-session exemption.
    internal static IDisposable? ClaimSessionWork(this IServiceProvider services, string? sessionId)
    {
        var admission = services.Get<ISessionRetentionAdmission>();
        if (admission is null) return null;
        return (string.IsNullOrWhiteSpace(sessionId)
            ? admission.TryProtectAll()
            : admission.TryProtectSession(sessionId.Trim()))
            ?? throw new InvalidOperationException("Session retirement already owns the requested work target.");
    }

    internal static void ClaimUnknownCommandWork(this AltaCommandContext context)
    {
        if (context.RetentionLifetime is { } lifetime)
        {
            lifetime.ClaimUnknownTarget(context.Services);
        }
        else if (context.Services.Get<ISessionRetentionAdmission>() is not null)
        {
            // An escaped raw command tree has no original invocation to own its callbacks.
            throw new InvalidOperationException("Plugin command retirement admission requires AltaCommandRegistry.InvokeAsync.");
        }
    }
}

// One original registry invocation owns the lease, not the parser/factory enumerator or a waiter.
internal sealed class AltaCommandRetentionLifetime(Action<AltaCommandRetentionLifetime> retain) : IDisposable
{
    private readonly List<IDisposable> _claims = [];
    private bool _claimed;
    internal Exception? RetainedFailure { get; private set; }

    internal void ObserveFailure(Exception failure)
    {
        if (AltaRetentionAdmission.ContainsRetainedDependency(failure)) RetainedFailure ??= failure;
    }

    internal void ClaimTarget(IServiceProvider services, string? sessionId)
    {
        if (services.ClaimSessionWork(sessionId) is { } claim) _claims.Add(claim);
    }

    internal void ClaimUnknownTarget(IServiceProvider services)
    {
        if (_claimed) return;
        ClaimTarget(services, null);
        _claimed = true;
    }

    public void Dispose()
    {
        if (RetainedFailure is not null)
        {
            retain(this);
            return;
        }
        foreach (var claim in _claims) claim.Dispose();
    }
}
