using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The title of a session, who chose it, and how the session is titled.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Title">The title the session is listed under.</param>
/// <param name="Source">Who chose the title; <see langword="null" /> for a session written before that was recorded, until it runs or is renamed.</param>
/// <param name="Mode">How the session is titled while nobody named it.</param>
/// <param name="OwnMode">Whether <paramref name="Mode" /> is the session's own; otherwise it is the one of the settings.</param>
public sealed record SessionTitleState(string SessionId, string Title, AgentSessionTitleSource? Source, AgentSessionTitleMode Mode, bool OwnMode);
