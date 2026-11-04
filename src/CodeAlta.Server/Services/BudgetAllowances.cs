namespace CodeAlta.Server.Services;

/// <summary>
/// The arithmetic of the allowances of an enforced budget (<c>doc/fleet/observability.md</c>, budgets and alerts). Each machine
/// commits the larger of what the server gave it and what it used (a machine that spends without allowances, a desktop, still
/// counts); the server gives only what no machine committed, so what all machines hold or used never passes the limit. A
/// machine asks for more when what it holds beyond what it spent is less than it needs, and gets at least a share, so that it
/// asks seldom and spends offline.
/// </summary>
public static class BudgetAllowances
{
    /// <summary>The share of its limit the server gives at least at once: a quarter.</summary>
    public const int ShareDivisor = 4;

    /// <summary>Gets the share of a limit given at least at once.</summary>
    /// <param name="limit">The limit.</param>
    /// <returns>A quarter of it, at least 1.</returns>
    public static long Share(long limit) => Math.Max(1, (limit + ShareDivisor - 1) / ShareDivisor);

    /// <summary>Gets what the machines committed of a budget: for each, the larger of its allowance and what it used.</summary>
    /// <param name="granted">The allowance of each machine.</param>
    /// <param name="used">What the facts of each machine count.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static long Committed(IReadOnlyDictionary<string, long> granted, IReadOnlyDictionary<string, long> used)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(used);
        long total = 0;
        foreach (var machine in granted.Keys.Union(used.Keys, StringComparer.Ordinal))
            total += Math.Max(granted.GetValueOrDefault(machine), used.GetValueOrDefault(machine));
        return total;
    }

    /// <summary>Gives a machine what it needs of a budget, within what no machine committed.</summary>
    /// <param name="limit">The limit of the budget.</param>
    /// <param name="granted">The allowance of each machine in the period.</param>
    /// <param name="used">What the facts of each machine count in the period.</param>
    /// <param name="machine">The machine that asks.</param>
    /// <param name="spent">What it says it spent (its own count, which may be ahead of the facts the server has).</param>
    /// <param name="needed">What it needs to hold beyond what it spent.</param>
    /// <param name="share">The least given at once.</param>
    /// <returns>The allowance of the machine now, and whether it still holds less than it needs (the budget is reached).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not positive, or an amount is negative.</exception>
    public static (long Granted, bool Exhausted) Grant(long limit, IReadOnlyDictionary<string, long> granted, IReadOnlyDictionary<string, long> used, string machine,
        long spent, long needed, long share)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(used);
        ArgumentException.ThrowIfNullOrEmpty(machine);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(spent);
        ArgumentOutOfRangeException.ThrowIfNegative(needed);
        ArgumentOutOfRangeException.ThrowIfNegative(share);
        var held = granted.GetValueOrDefault(machine);
        var own = Math.Max(spent, used.GetValueOrDefault(machine));
        if (held - own >= needed) return (held, false);
        long others = 0;
        foreach (var other in granted.Keys.Union(used.Keys, StringComparer.Ordinal))
        {
            if (other != machine) others += Math.Max(granted.GetValueOrDefault(other), used.GetValueOrDefault(other));
        }

        var committed = Math.Max(held, own);
        var pool = limit - others - committed;
        if (pool <= 0) return (held, true);
        var give = Math.Min(pool, Math.Max(needed - (committed - own), share));
        var now = committed + give;
        return (now, now - own < needed);
    }
}
