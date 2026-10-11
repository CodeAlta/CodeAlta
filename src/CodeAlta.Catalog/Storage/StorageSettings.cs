namespace CodeAlta.Catalog.Storage;

/// <summary>Automatic maintenance choices from the global <c>[storage]</c> table.</summary>
/// <param name="ClearScratchpadOnStartup">Whether to clear only the owning instance's scratchpad before admitting sessions.</param>
/// <param name="SessionRetention">The inactive-session retention policy.</param>
public sealed record StorageSettings(bool ClearScratchpadOnStartup, SessionRetentionSettings SessionRetention)
{
    /// <summary>Gets the policy when configuration is absent: scratchpad clearing on, retention three months.</summary>
    public static StorageSettings Default { get; } = new(true, SessionRetentionSettings.Default);

    /// <summary>Reads a global storage table without turning invalid values into destructive defaults.</summary>
    /// <exception cref="InvalidDataException">A duration is invalid.</exception>
    public static StorageSettings Read(CodeAltaStorageSettingsDocument? document)
    {
        var duration = document?.SessionRetention;
        var defaults = SessionRetentionSettings.Default;
        var retention = new SessionRetentionSettings(duration?.Enabled ?? defaults.Enabled, duration?.Amount ?? defaults.Amount, duration?.Unit ?? defaults.Unit);
        if (retention.GetValidationError() is { } error) throw new InvalidDataException(error);
        return new(document?.ClearScratchpadOnStartup ?? true, retention);
    }
}

/// <summary>An inactivity duration, or Never. Months and years are calendar durations, not fixed day counts.</summary>
/// <param name="Enabled">Whether automatic session retention is enabled.</param>
/// <param name="Amount">The positive whole-number duration.</param>
/// <param name="Unit">One of days, weeks, months or years.</param>
public sealed record SessionRetentionSettings(bool Enabled, int Amount, string Unit)
{
    /// <summary>Gets the default enabled, three-calendar-month duration.</summary>
    public static SessionRetentionSettings Default { get; } = new(true, 3, "months");

    /// <summary>Gets a configuration error, or null when this duration is valid.</summary>
    public string? GetValidationError()
        => Amount <= 0 ? "Storage session retention amount must be a positive whole number."
            : Unit is not ("days" or "weeks" or "months" or "years") ? "Storage session retention unit must be days, weeks, months or years."
            : null;

    /// <summary>Calculates a UTC cutoff from one captured clock reading. Equality is retained by callers.</summary>
    /// <param name="now">The captured clock reading.</param>
    /// <param name="cutoff">The exclusive upper bound on expired activity timestamps.</param>
    /// <param name="error">A validation error, or null when disabled or successful.</param>
    /// <returns>True when enabled and representable; false for Never or an invalid duration.</returns>
    public bool TryGetCutoff(DateTimeOffset now, out DateTimeOffset cutoff, out string? error)
    {
        cutoff = default;
        error = GetValidationError();
        if (error is not null || !Enabled) return false;
        try
        {
            var utc = now.ToUniversalTime();
            cutoff = Unit switch
            {
                "days" => utc.AddDays(-Amount),
                "weeks" => utc.AddDays(-checked(Amount * 7)),
                "months" => utc.AddMonths(-Amount),
                "years" => utc.AddYears(-Amount),
                _ => throw new InvalidOperationException("Validated retention unit was not recognized."),
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            error = "Storage session retention duration is too large to calculate a cutoff.";
            return false;
        }
    }
}
