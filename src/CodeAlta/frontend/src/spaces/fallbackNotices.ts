import type { SpaceSessionActivity } from "#neoastra";

/** A session that went on with another model after a failure of its provider, to be said once. */
export type FallbackNotice = Readonly<{ key: string; sessionId: string; projectId: string | null; title: string; message: string }>;

/**
 * The fallbacks of the sessions that were not said yet: each is said once, by the key of the session and the instant
 * of its fallback. The first reading of a window says none of them: what happened before it opened is not news.
 */
export function newFallbackNotices(said: ReadonlySet<string> | null, sessions: readonly SpaceSessionActivity[])
  : { notices: readonly FallbackNotice[]; said: ReadonlySet<string> } {
  const known = new Set<string>();
  const notices: FallbackNotice[] = [];
  for (const session of sessions) {
    if (!session.fallback) continue;
    const key = `${session.sessionId}@${session.fallback.at}`;
    known.add(key);
    if (said && !said.has(key))
      notices.push({ key, sessionId: session.sessionId, projectId: session.projectId ?? null, title: session.title, message: session.fallback.message });
  }

  // A fallback said once stays said while the host lists it.
  if (said) for (const key of said) if (sessions.some(session => key.startsWith(`${session.sessionId}@`))) known.add(key);
  return { notices, said: known };
}
