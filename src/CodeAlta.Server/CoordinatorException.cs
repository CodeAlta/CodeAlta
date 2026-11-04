using CodeAlta.Fleet.Protocol;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server;

/// <summary>
/// A refusal of the coordinator: an HTTP status, a stable code and a hint, answered as problem details (RFC 9457) on
/// the REST API and as a <see cref="FleetError"/> on the control connection.
/// </summary>
public sealed class CoordinatorException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CoordinatorException"/> class.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The stable code (<see cref="CoordinatorErrorCodes"/> or <see cref="FleetErrorCodes"/>).</param>
    /// <param name="message">A description for people, without sensitive data.</param>
    /// <param name="hint">What the client should do, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty.</exception>
    public CoordinatorException(int status, string code, string message, string? hint = null) : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Status = status;
        Code = code;
        Hint = hint;
    }

    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; }

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }

    /// <summary>Gets the hint, or <see langword="null"/>.</summary>
    public string? Hint { get; }

    /// <summary>Gets the error of the fleet protocol with the same code.</summary>
    /// <returns>The error.</returns>
    public FleetError ToFleetError() => new(Code, Hint, Message);

    internal static CoordinatorException BadRequest(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);

    internal static CoordinatorException Unauthenticated(string code, string message)
        => new(StatusCodes.Status401Unauthorized, code, message, FleetErrorHints.Reauthenticate);

    internal static CoordinatorException Forbidden(string code, string message, string? hint = null) => new(StatusCodes.Status403Forbidden, code, message, hint);

    internal static CoordinatorException NotFound(string code, string message) => new(StatusCodes.Status404NotFound, code, message);

    internal static CoordinatorException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message, FleetErrorHints.Refresh);
}

/// <summary>The stable codes of the coordinator's refusals, beside those of <see cref="FleetErrorCodes"/>.</summary>
public static class CoordinatorErrorCodes
{
    /// <summary>The server has no organization yet: set it up first.</summary>
    public const string NotSetUp = "setup.required";

    /// <summary>A named value, or its copies, does not hold.</summary>
    public const string SecretInvalid = "secret.invalid";

    /// <summary>Another value has this name in this scope: rotate it instead.</summary>
    public const string SecretExists = "secret.exists";

    /// <summary>The value is not at the version before the one written: read it again.</summary>
    public const string SecretConflict = "secret.conflict";

    /// <summary>No value of this person has this id.</summary>
    public const string SecretNotFound = "secret.not_found";

    /// <summary>An environment does not hold.</summary>
    public const string EnvironmentInvalid = "environment.invalid";

    /// <summary>The environment is not newer than the one held, or another environment has its name.</summary>
    public const string EnvironmentConflict = "environment.conflict";

    /// <summary>No environment of this person has this id.</summary>
    public const string EnvironmentNotFound = "environment.not_found";

    /// <summary>An approval does not hold.</summary>
    public const string ApprovalInvalid = "approval.invalid";

    /// <summary>The approval was given already.</summary>
    public const string ApprovalConflict = "approval.conflict";

    /// <summary>No approval the caller may see has this id.</summary>
    public const string ApprovalNotFound = "approval.not_found";

    /// <summary>The server already has an owner.</summary>
    public const string AlreadySetUp = "setup.done";

    /// <summary>Setup is accepted only from this machine.</summary>
    public const string SetupNotLocal = "setup.loopback_only";

    /// <summary>The setup code is wrong, or none is waiting.</summary>
    public const string SetupCodeInvalid = "setup.invalid_code";

    /// <summary>The setup code expired: <c>altaserver setup-code</c> makes a new one.</summary>
    public const string SetupCodeExpired = "setup.code_expired";

    /// <summary>A signed request without its headers, or with headers that do not read.</summary>
    public const string SignatureMissing = "request.signature_missing";

    /// <summary>The signature of a request does not verify.</summary>
    public const string SignatureInvalid = "request.signature_invalid";

    /// <summary>The time of a request is too far from the server's.</summary>
    public const string ClockSkew = "request.clock_skew";

    /// <summary>The nonce of a request was seen before.</summary>
    public const string Replayed = "request.replayed";

    /// <summary>The body of a request does not read.</summary>
    public const string InvalidBody = "request.invalid_body";

    /// <summary>No device has that id.</summary>
    public const string DeviceNotFound = "device.not_found";

    /// <summary>A device with that key is already registered.</summary>
    public const string DeviceExists = "device.exists";

    /// <summary>The device is not in the state the operation needs.</summary>
    public const string DeviceState = "device.state";

    /// <summary>The last approved controller device cannot be revoked.</summary>
    public const string LastController = "device.last_controller";

    /// <summary>The enrollment token is unknown.</summary>
    public const string EnrollmentTokenInvalid = "enrollment.invalid_token";

    /// <summary>The enrollment token was already used.</summary>
    public const string EnrollmentTokenUsed = "enrollment.token_used";

    /// <summary>The enrollment token expired.</summary>
    public const string EnrollmentTokenExpired = "enrollment.token_expired";

    /// <summary>An entry of the device list that does not say what the operation needs.</summary>
    public const string EntryInvalid = "device_list.invalid_entry";

    /// <summary>The signature of an entry of the device list does not verify.</summary>
    public const string EntrySignatureInvalid = "device_list.invalid_signature";

    /// <summary>The device list moved on: read it again and sign an entry that follows its last one.</summary>
    public const string EntryConflict = "device_list.conflict";

    /// <summary>A session fact or an inventory that does not read.</summary>
    public const string PublicationInvalid = "publication.invalid";

    /// <summary>The person has a metadata key at that epoch already: read it and add wraps instead.</summary>
    public const string MetadataKeyExists = "metadata_key.exists";

    /// <summary>No metadata key of the person has this id.</summary>
    public const string MetadataKeyNotFound = "metadata_key.not_found";

    /// <summary>A wrap of a metadata key that does not read, or names a device that may not have it.</summary>
    public const string MetadataKeyInvalid = "metadata_key.invalid";

    /// <summary>The calling device is not a recovery key the server keeps an anchor for.</summary>
    public const string RecoveryKeyNotFound = "recovery_key.not_found";

    /// <summary>A recovery that is not signed by a recovery key of the organization, or does not add the device that sends it.</summary>
    public const string RecoveryRefused = "recovery.refused";

    /// <summary>A telemetry setting that does not read, does not hold, or is not signed by the device that sends it.</summary>
    public const string TelemetryInvalid = "telemetry.invalid";

    /// <summary>A telemetry setting whose sequence is not after the one the server holds: read it again.</summary>
    public const string TelemetryConflict = "telemetry.conflict";

    /// <summary>The fleet does not send its telemetry through the server, or not this signal.</summary>
    public const string TelemetryNotForwarded = "telemetry.not_forwarded";

    /// <summary>A manifest or an update policy that does not read, does not hold, or is not signed by the device that sends it.</summary>
    public const string UpdateInvalid = "update.invalid";

    /// <summary>A manifest whose sequence is taken, a policy not newer than the one held, or a campaign while another runs.</summary>
    public const string UpdateConflict = "update.conflict";

    /// <summary>No campaign of the organization has this id.</summary>
    public const string UpdateNotFound = "update.not_found";

    /// <summary>An automation version that does not hold, is not signed by the caller, or is given in clear.</summary>
    public const string AutomationInvalid = "automation.invalid";

    /// <summary>An automation version not newer than the one held, or forgotten before its machine took it away.</summary>
    public const string AutomationConflict = "automation.conflict";

    /// <summary>No automation of the caller's person has this id.</summary>
    public const string AutomationNotFound = "automation.not_found";

    /// <summary>A statistics query that does not read: its days, its time zone, its groups or its scope.</summary>
    public const string StatisticsInvalid = "statistics.invalid";

    /// <summary>A budget that does not hold, is not signed by the caller, or limits a scope that cannot be limited yet.</summary>
    public const string BudgetInvalid = "budget.invalid";

    /// <summary>A budget not newer than the one held, or changed since the machine read it.</summary>
    public const string BudgetConflict = "budget.conflict";

    /// <summary>No budget of the caller's person has this id.</summary>
    public const string BudgetNotFound = "budget.not_found";

    /// <summary>A hold, or its Continue anyway, that does not hold.</summary>
    public const string BudgetHoldInvalid = "budget_hold.invalid";

    /// <summary>The run was continued or stopped already, or asked another hold.</summary>
    public const string BudgetHoldConflict = "budget_hold.conflict";

    /// <summary>No hold the caller may see has this id.</summary>
    public const string BudgetHoldNotFound = "budget_hold.not_found";

    /// <summary>The caller's person has no notification with this id.</summary>
    public const string NotificationNotFound = "notification.not_found";

    /// <summary>A change of access does not hold: a role, a team, a grant, a transfer, a proposal.</summary>
    public const string AccessInvalid = "access.invalid";

    /// <summary>No person, team, grant, transfer, resource or proposal of the organization has this id.</summary>
    public const string AccessNotFound = "access.not_found";

    /// <summary>The change of access was overtaken: read again.</summary>
    public const string AccessConflict = "access.conflict";

    /// <summary>The change would leave the organization without an active owner.</summary>
    public const string LastOwner = "access.last_owner";

    /// <summary>A version of the signed access document does not hold, or does not follow the last one.</summary>
    public const string AccessDocumentInvalid = "access_document.invalid";
}