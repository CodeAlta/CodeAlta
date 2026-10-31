namespace CodeAlta.Catalog.ModelTiers;

/// <summary>
/// How CodeAlta chooses the model of each turn of a session on Adaptive, from the <c>[models.adaptive]</c> table: the
/// tier its turns run on, whether a turn that changed code is asked once to check its change against the request, and
/// the tier a turn that ends in visible trouble (no answer, a failed last test run) continues on.
/// </summary>
/// <remarks>
/// Adaptive is set up when the table is there and the tiers it names have models: a session cannot be put on Adaptive
/// before that, so that it never runs on a model the user did not choose.
/// </remarks>
/// <param name="Code">The tier the turns of the session run on.</param>
/// <param name="Escalate">The tier a turn that ends in visible trouble continues on, once.</param>
/// <param name="Recheck">Whether a turn that changed code is asked once to check its change against the request.</param>
public sealed record AdaptiveModelSettings(ModelTier Code, ModelTier Escalate, bool Recheck)
{
    /// <summary>
    /// Gets the choices the evaluation of the trial found best (#273): the quick tier with a recheck, and the complex tier
    /// on trouble. They are also what a table that names nothing gives.
    /// </summary>
    public static AdaptiveModelSettings Recommended { get; } = new(ModelTier.Quick, ModelTier.Complex, true);

    /// <summary>Gets whether the tiers Adaptive takes its models from have models: a session can then be put on it.</summary>
    /// <param name="tiers">The tiers of the space of the session.</param>
    /// <returns><see langword="true"/> when the turn tier and the escalation tier both have a target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    public bool IsReadyIn(ModelTierSettings tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return tiers.Get(Code).Count > 0 && tiers.Get(Escalate).Count > 0;
    }

    /// <summary>Gets the table as the configuration writes it.</summary>
    /// <returns>The table, every choice written.</returns>
    public CodeAltaModelAdaptiveDocument ToDocument()
        => new() { Code = ModelTiers.NameOf(Code), Escalate = ModelTiers.NameOf(Escalate), Recheck = Recheck };

    /// <summary>Reads the <c>[models.adaptive]</c> table; a choice it does not write takes its <see cref="Recommended"/> value.</summary>
    /// <param name="document">The table, or <see langword="null"/> when the file has none.</param>
    /// <returns>The settings, or <see langword="null"/> when Adaptive is not set up.</returns>
    public static AdaptiveModelSettings? Read(CodeAltaModelAdaptiveDocument? document)
        => document is null
            ? null
            : new AdaptiveModelSettings(
                ModelTiers.Parse(document.Code) ?? Recommended.Code,
                ModelTiers.Parse(document.Escalate) ?? Recommended.Escalate,
                document.Recheck ?? Recommended.Recheck);

    // What is wrong with the table, each message naming the setting.
    internal static void Validate(List<string> errors, CodeAltaModelAdaptiveDocument? document)
    {
        if (document is null) return;
        if (document.Code is { } code && ModelTiers.Parse(code) is null)
            errors.Add($"models.adaptive.code: '{code}' is not a tier. Use complex, standard or quick.");
        if (document.Escalate is { } escalate && ModelTiers.Parse(escalate) is null)
            errors.Add($"models.adaptive.escalate: '{escalate}' is not a tier. Use complex, standard or quick.");
    }
}
