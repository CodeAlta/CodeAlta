using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What the host is told when it ranks the skills the prompt of a session lists first: the session, its project and its space.
/// </summary>
/// <param name="SessionId">The session; empty for a session that has no identifier yet.</param>
/// <param name="Project">Its project; <see langword="null"/> for a session of no project.</param>
/// <param name="SpaceId">Its space.</param>
public sealed record PreferredSkillsRequest(string SessionId, ProjectDescriptor? Project, string SpaceId);
