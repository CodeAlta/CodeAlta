using System.Globalization;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// `alta fleet audit` (doc/fleet/security-and-privacy.md, audit log): the audit log of a server, read by agents whose person
// may read it (owners, admins, auditors). It writes no file: the host runs outside the session's sandbox, so an export to
// a path is a person's action, in Settings > Fleet > Audit.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const int DefaultAuditRecords = 50;
    private const int MaximumAuditRecords = 500;

    private static readonly AltaCommandPolicy[] FleetAuditPolicies =
    [
        Read("fleet audit list"),
        Read("fleet audit verify"),
    ];

    private static Command CreateFleetAuditCommand(AltaCommandContext context)
    {
        var group = Group("audit", "Read the audit log of a CodeAlta Server: who did what, with which device, from where and how it ended; never content.");
        group.Add(CreateFleetAuditListCommand(context));
        group.Add(CreateFleetAuditVerifyCommand(context));
        AddHelpText(
            group,
            "The log is read by owners, admins and auditors. A person exports it as CSV or JSON Lines in Settings > Fleet > Audit.",
            "Examples: `alta fleet audit list --action team. --since 2026-11-01`; `alta fleet audit list --result failure`; `alta fleet audit verify`.");
        return group;
    }

    private static Command CreateFleetAuditListCommand(AltaCommandContext context)
    {
        string? action = null, person = null, device = null, result = null, target = null, since = null, until = null, text = null, before = null, limit = null, server = null;
        var command = Leaf("list", "List records of the audit log, newest first.");
        command.Add("action=", "Actions, exact or by prefix (`team.`), separated by commas.", value => action = value);
        command.Add("person=", "The id of the person behind the records.", value => person = value);
        command.Add("device=", "The id of the device that asked.", value => device = value);
        command.Add("result=", "`success`, `failure` (any refusal) or an error code.", value => result = value);
        command.Add("target=", "What the records were done to.", value => target = value);
        command.Add("since=", "The first time, included: yyyy-MM-dd or ISO 8601.", value => since = value);
        command.Add("until=", "The time the records end before: yyyy-MM-dd or ISO 8601.", value => until = value);
        command.Add("q=", "Words the records hold.", value => text = value);
        command.Add("before=", "Records before this sequence: the `before` of the summary of the page before.", value => before = value);
        command.Add("limit=", $"The most records, up to {MaximumAuditRecords}. Defaults to {DefaultAuditRecords}.", value => limit = value);
        command.Add("server=", "The server: its link id or its address. Defaults to the one online.", value => server = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () =>
        {
            const string CommandPath = "alta fleet audit list";
            if (!context.TryGetRequired<IAltaFleetAudit>(nameof(IAltaFleetAudit), out var audit)) return AltaExitCodes.ServiceUnavailable;
            if (!TryParseLimit(limit, DefaultAuditRecords, MaximumAuditRecords, out var size))
                return UsageError(context, "usage.invalidLimit", $"--limit is a number from 1 to {MaximumAuditRecords}.", CommandPath);
            if (!TryParseAuditTime(since, out var from) || !TryParseAuditTime(until, out var to))
                return UsageError(context, "usage.invalidTime", "--since and --until are days (yyyy-MM-dd) or ISO 8601 times.", CommandPath);
            long? cursor = null;
            if (NormalizeOptionalText(before) is { } beforeText)
            {
                if (!long.TryParse(beforeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                    return UsageError(context, "usage.invalidCursor", "--before is the sequence of a record.", CommandPath);
                cursor = parsed;
            }

            var actions = NormalizeOptionalText(action)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var filters = new AltaFleetAuditFilters(actions, NormalizeOptionalText(person), NormalizeOptionalText(device), NormalizeOptionalText(result), NormalizeOptionalText(target),
                from, to, NormalizeOptionalText(text));
            var page = await audit.ReadAuditAsync(NormalizeOptionalText(server), filters, cursor, size, context.CancellationToken).ConfigureAwait(false);
            if (page.Status != "ok") return AuditFailure(context, page.Status, page.Message);
            foreach (var record in page.Records)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.fleet.audit.record", version = 1, correlationId = context.CorrelationId, sequence = record.Sequence, time = record.Time, action = record.Action,
                    result = record.Result, person = record.Person, device = record.Device, target = record.Target, address = record.Address, details = record.Details,
                });
            }

            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.audit.summary", version = 1, correlationId = context.CorrelationId, server = page.Server, count = page.Records.Count, before = page.Before,
                note = page.Before is null ? null : $"More records: `alta fleet audit list --before {page.Before.Value.ToString(CultureInfo.InvariantCulture)}` with the same filters.",
            });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        return command;
    }

    private static Command CreateFleetAuditVerifyCommand(AltaCommandContext context)
    {
        string? server = null;
        var command = Leaf("verify", "Check the hash chain of the whole audit log at the server: whether a record was changed, removed or inserted.");
        command.Add("server=", "The server: its link id or its address. Defaults to the one online.", value => server = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () =>
        {
            if (!context.TryGetRequired<IAltaFleetAudit>(nameof(IAltaFleetAudit), out var audit)) return AltaExitCodes.ServiceUnavailable;
            var check = await audit.VerifyAuditAsync(NormalizeOptionalText(server), context.CancellationToken).ConfigureAwait(false);
            if (check.Status != "ok") return AuditFailure(context, check.Status, check.Message);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.audit.verification", version = 1, correlationId = context.CorrelationId, valid = check.Valid, records = check.Count, brokenAt = check.BrokenAt,
                head = check.HeadHash,
                note = check.Valid ? "Every record names the hash of the one before it: nothing was changed, removed or inserted."
                    : $"The chain breaks at record {check.BrokenAt}: tell the owner of the server at once.",
            });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        return command;
    }

    private static int AuditFailure(AltaCommandContext context, string status, string? message)
        => status == "forbidden"
            ? FleetFailure(context, status, "The audit log is read by owners, admins and auditors of the organization." + (message is null ? string.Empty : " " + message), null)
            : FleetFailure(context, status, message, null);

    private static bool TryParseAuditTime(string? text, out DateTimeOffset? time)
    {
        time = null;
        if (NormalizeOptionalText(text) is not { } given) return true;
        if (DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            time = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return true;
        }

        if (!DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        time = parsed;
        return true;
    }
}
