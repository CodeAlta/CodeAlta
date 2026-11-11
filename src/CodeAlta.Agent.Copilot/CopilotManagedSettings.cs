using System.Text.Json;
using GitHub.Copilot.Rpc;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// The enterprise managed settings of GitHub Copilot that apply to one account, or to the computer alone, as the
/// Copilot runtime resolved them (<c>managedSettings.resolve</c>): the policy the enterprise set on GitHub and the one
/// IT deployed on the computer (MDM, registry, <c>managed-settings.json</c>), merged. Only the settings CodeAlta
/// enforces are read; <see cref="ManagedKeys"/> names them all.
/// </summary>
public sealed record CopilotManagedSettings
{
    /// <summary>Gets the opaque identity of the account the settings were resolved for; null for the computer alone.</summary>
    public string? Account { get; init; }

    /// <summary>Gets whether the account's server policy was present.</summary>
    public bool ServerManaged { get; init; }

    /// <summary>Gets whether a policy of the computer (MDM, registry or file) was present.</summary>
    public bool DeviceManaged { get; init; }

    /// <summary>Gets whether the policy could not be determined, a failed fetch for example, and the runtime keeps its safeguards on.</summary>
    public bool FailClosed { get; init; }

    /// <summary>Gets whether the mode that approves everything is turned off (<c>permissions.disableBypassPermissionsMode</c>).</summary>
    public bool BypassPermissionsDisabled { get; init; }

    /// <summary>Gets whether the assisted mode is turned off (<c>permissions.disableAssistedPermissionsMode</c>).</summary>
    public bool AssistedPermissionsDisabled { get; init; }

    /// <summary>Gets the default mode of new sessions (<c>permissions.defaultMode</c>): <c>manual</c>, <c>assisted</c> or <c>allow-all</c>; null for none.</summary>
    public string? DefaultMode { get; init; }

    /// <summary>Gets whether the sandbox is required (<c>sandbox.enabled</c>, or the runtime's fallback while the policy is undetermined).</summary>
    public bool SandboxEnabled { get; init; }

    /// <summary>Gets whether no command runs where the sandbox cannot (<c>sandbox.failIfUnavailable</c>).</summary>
    public bool SandboxFailIfUnavailable { get; init; }

    /// <summary>Gets whether a user may run a command outside the sandbox (<c>sandbox.allowBypass</c>); true when the policy does not say.</summary>
    public bool SandboxBypassAllowed { get; init; } = true;

    /// <summary>Gets whether a fresh server policy is required at each start (<c>forceRemoteSettingsRefresh</c>).</summary>
    public bool ForceRemoteSettingsRefresh { get; init; }

    /// <summary>Gets the help page of the organization (<c>communication.support.url</c>); null for none.</summary>
    public string? SupportUrl { get; init; }

    /// <summary>Gets the setting keys the policy manages.</summary>
    public IReadOnlyList<string> ManagedKeys { get; init; } = [];

    /// <summary>Gets the warnings of the runtime about a source it could not read, or a failed refresh served from its cache.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Maps the answer of the runtime.</summary>
    /// <param name="result">The answer of <c>managedSettings.resolve</c>.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CopilotManagedSettings From(ManagedSettingsResolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return From(result.Resolved, result.Account, result.Values?.Communication?.Support?.Url, result.Diagnostics);
    }

    /// <summary>Maps what the runtime would enforce for candidate documents (<c>managedSettings.compose</c>).</summary>
    /// <param name="result">The answer of <c>managedSettings.compose</c>.</param>
    /// <returns>The settings, for no account.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CopilotManagedSettings From(ManagedSettingsComposeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var url = Read(result.Resolved.Settings, "communication", "support", "url") is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        return From(result.Resolved, account: null, url, result.Diagnostics);
    }

    private static CopilotManagedSettings From(ManagedSettingsResolvedData resolved, string? account, string? supportUrl, IList<ManagedSettingsDiagnostic> diagnostics)
    {
        var settings = resolved.Settings;
        return new CopilotManagedSettings
        {
            Account = string.IsNullOrWhiteSpace(account) ? null : account.Trim(),
            ServerManaged = resolved.ServerManaged,
            DeviceManaged = resolved.DeviceManaged,
            FailClosed = resolved.FailClosed,
            BypassPermissionsDisabled = resolved.BypassPermissionsDisabled,
            AssistedPermissionsDisabled = Read(settings, "permissions", "disableAssistedPermissionsMode") is { ValueKind: JsonValueKind.True },
            DefaultMode = Read(settings, "permissions", "defaultMode") is { ValueKind: JsonValueKind.String } mode ? mode.GetString()?.Trim() : null,
            SandboxEnabled = Read(settings, "sandbox", "enabled") is { ValueKind: JsonValueKind.True } || resolved.SandboxEnabledByUndeterminedPolicy == true,
            SandboxFailIfUnavailable = Read(settings, "sandbox", "failIfUnavailable") is { ValueKind: JsonValueKind.True },
            SandboxBypassAllowed = Read(settings, "sandbox", "allowBypass") is not { ValueKind: JsonValueKind.False },
            ForceRemoteSettingsRefresh = Read(settings, "forceRemoteSettingsRefresh") is { ValueKind: JsonValueKind.True },
            SupportUrl = supportUrl is { Length: > 0 } url ? url.Trim() : null,
            ManagedKeys = [.. resolved.ManagedKeys],
            Diagnostics = [.. diagnostics.Select(static diagnostic => diagnostic.Message).Where(static message => !string.IsNullOrWhiteSpace(message))],
        };
    }

    /// <summary>Combines these settings with others, keeping what is the most restrictive of each.</summary>
    /// <param name="other">The other settings.</param>
    /// <returns>The combined settings, for the account of these.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public CopilotManagedSettings Strictest(CopilotManagedSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return this with
        {
            ServerManaged = ServerManaged || other.ServerManaged,
            DeviceManaged = DeviceManaged || other.DeviceManaged,
            FailClosed = FailClosed || other.FailClosed,
            BypassPermissionsDisabled = BypassPermissionsDisabled || other.BypassPermissionsDisabled,
            AssistedPermissionsDisabled = AssistedPermissionsDisabled || other.AssistedPermissionsDisabled,
            DefaultMode = NarrowerMode(DefaultMode, other.DefaultMode),
            SandboxEnabled = SandboxEnabled || other.SandboxEnabled,
            SandboxFailIfUnavailable = SandboxFailIfUnavailable || other.SandboxFailIfUnavailable,
            SandboxBypassAllowed = SandboxBypassAllowed && other.SandboxBypassAllowed,
            ForceRemoteSettingsRefresh = ForceRemoteSettingsRefresh || other.ForceRemoteSettingsRefresh,
            SupportUrl = SupportUrl ?? other.SupportUrl,
            ManagedKeys = [.. ManagedKeys.Union(other.ManagedKeys, StringComparer.Ordinal)],
            Diagnostics = [.. Diagnostics.Union(other.Diagnostics, StringComparer.Ordinal)],
        };
    }

    // The default mode that asks more: manual, then assisted, then allow-all; a mode Copilot adds later ranks as manual.
    private static string? NarrowerMode(string? left, string? right)
    {
        if (left is null || right is null) return left ?? right;
        static int Rank(string mode) => mode switch { "allow-all" => 2, "assisted" => 1, _ => 0 };
        return Rank(right) < Rank(left) ? right : left;
    }

    /// <summary>
    /// Reads a setting of a managed-settings document: nested objects (<c>{"sandbox":{"enabled":true}}</c>), or a key
    /// that holds the whole dotted path (<c>{"sandbox.enabled":true}</c>), as an MDM layer carries scalar settings.
    /// </summary>
    internal static JsonElement? Read(JsonElement? document, params string[] path)
    {
        if (document is not { ValueKind: JsonValueKind.Object } root) return null;
        if (root.TryGetProperty(string.Join('.', path), out var flat)) return Unwrap(flat);
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }

        return Unwrap(current);
    }

    // An enterprise default a team may override is written { "overridable": value }: the value still applies.
    private static JsonElement Unwrap(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("overridable", out var inner) ? inner : value;
}
