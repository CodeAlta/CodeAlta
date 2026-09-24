namespace CodeAlta.Catalog.Skills;

/// <summary>Outcome of a bounded configuration read for disabled skill names.</summary>
/// <remarks>Names reflect a completed read only for <see cref="SkillBoundedReadStatus.Complete"/> or
/// <see cref="SkillBoundedReadStatus.Missing"/>; all other outcomes must be treated as unknown enablement.
/// Reads are not atomic with external edits and do not establish active-session state.</remarks>
/// <param name="Status">Read or parse outcome, without raw file content or error messages.</param>
/// <param name="Names">Disabled names, empty unless the outcome is complete.</param>
public sealed record SkillDisabledNamesReadResult(SkillBoundedReadStatus Status, IReadOnlySet<string> Names);
