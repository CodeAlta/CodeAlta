namespace CodeAlta.Agent;

/// <summary>Reports a terminal failed prerequisite whose dependent acquisitions must remain owned.</summary>
/// <remarks>
/// This result is not evidence that an outstanding original has terminated. Owners must join genuine
/// active work separately and retain the actual acquisitions, originals and outcomes in the payload.
/// Ordinary callback faults and cancellation are not retention signals by themselves.
/// </remarks>
public sealed class AgentDependencyRetentionException : AggregateException
{
    /// <summary>Creates an attributed retained-dependency result without flattening its failures.</summary>
    /// <param name="operation">The owning operation's attribution.</param>
    /// <param name="stage">The failed required-prerequisite stage.</param>
    /// <param name="failures">Ordered original failure references; duplicate references are significant.</param>
    /// <param name="dependencies">The actual retained owner, acquisitions and original/outcome inventory.</param>
    /// <exception cref="ArgumentNullException">A required argument or failure is null.</exception>
    /// <exception cref="ArgumentException">Attribution is empty or no failures are supplied.</exception>
    public AgentDependencyRetentionException(string operation, string stage, IEnumerable<Exception> failures, object dependencies)
        : base("A required dependency release was not confirmed; acquisitions remain retained.", failures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (InnerExceptions.Count == 0) throw new ArgumentException("At least one failure is required.", nameof(failures));
        Operation = operation;
        Stage = stage;
        Dependencies = dependencies;
    }

    /// <summary>Gets the owning operation attribution.</summary>
    public string Operation { get; }

    /// <summary>Gets the failed prerequisite attribution.</summary>
    public string Stage { get; }

    /// <summary>Gets strong references to the actual retained ownership inventory.</summary>
    public object Dependencies { get; }

    /// <summary>Recognizes explicit retention anywhere in an exception graph without changing it.</summary>
    /// <param name="failure">The complete failure graph to inspect.</param>
    /// <returns>Whether the graph contains an explicit Agent retained-dependency result.</returns>
    /// <exception cref="ArgumentNullException">The failure is null.</exception>
    public static bool Contains(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AgentDependencyRetentionException) return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }
}
