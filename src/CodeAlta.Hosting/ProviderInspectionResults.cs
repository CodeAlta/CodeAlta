using CodeAlta.Agent;

namespace CodeAlta.Hosting;

/// <summary>
/// Describes a cached or temporary provider connectivity-test outcome, not a full readiness validation.
/// </summary>
/// <param name="Success">Whether the cached policy or completed probe reports success. A returned probe's availability is not checked.</param>
/// <param name="Message">Frontend-formatted text, or the unchanged active-provider status on a cached failure.</param>
/// <param name="ModelCount">The model count captured before temporary-runtime disposal; zero for a failed result.</param>
/// <remarks>The default value is the unhandled cached-policy sentinel; do not consume its message.</remarks>
public readonly record struct ProviderInspectionTestResult(bool Success, string Message, int ModelCount);

/// <summary>
/// Describes a cached or temporary provider model-list outcome.
/// </summary>
/// <param name="Success">Whether a Ready cache or completed probe supplied the result. An empty list can be successful.</param>
/// <param name="Message">Frontend-formatted text. Temporary-probe success text is formatted before runtime disposal.</param>
/// <param name="Models">The borrowed cached/probe list, or a new array only when temporary-probe sorting was requested. No snapshot or immutability is promised.</param>
/// <remarks>
/// Runtime disposal or later owner mutations can change a borrowed list after the message was formatted.
/// The default value is the unhandled cached-policy sentinel; its message and models must not be consumed.
/// </remarks>
public readonly record struct ProviderInspectionModelListResult(bool Success, string Message, IReadOnlyList<AgentModelInfo> Models);
