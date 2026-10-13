import { useRef } from "react";
import { CommandPermissionPanel } from "./CommandPermissionPanel";
import { SessionReference } from "./SessionReference";
import type { createPermissionReviewer } from "./sessionPermissions";
import type { WaitingSubSession } from "./spaces/spaces";
import { useShellLanguage } from "./shellLanguage";

type Reviewer = ReturnType<typeof createPermissionReviewer>;

/**
 * The sessions under this one (the ones it created, theirs…) that wait for the user, above its composer: each is
 * named with a link that opens it, and a command or a file change it waits to be allowed is shown to be answered
 * here. A question or a form is answered in its own pane. "Deny and say what to do instead" is left to that pane
 * too: its text belongs to the composer of the session that asks.
 */
export function WaitingSubSessionsPanel({ sessions, epoch, canReview, createReviewer, onAnswered }: {
  sessions: readonly WaitingSubSession[];
  epoch: string;
  canReview: () => boolean;
  /** Gives each session its own reviewer: one reviewer reads the requests of one session at a time. Null when the host reviews no command. */
  createReviewer: (() => Reviewer) | null;
  /** Called when the user answered a request: the composer of the pane takes the focus back. */
  onAnswered?: () => void;
}) {
  const { t } = useShellLanguage();
  const reviewers = useRef(new Map<string, Reviewer>());
  if (!sessions.length) { reviewers.current.clear(); return null; }
  // A session that no longer waits gives its reviewer back.
  for (const id of [...reviewers.current.keys()]) if (!sessions.some(session => session.sessionId === id)) reviewers.current.delete(id);
  const reviewerOf = (sessionId: string) => {
    let reviewer = reviewers.current.get(sessionId);
    if (!reviewer && createReviewer) reviewers.current.set(sessionId, reviewer = createReviewer());
    return reviewer ?? null;
  };
  return <section className="waiting-sub-sessions" aria-label={t("Sub-sessions that wait for you")}>
    {sessions.map(session => {
      const reviewer = reviewerOf(session.sessionId);
      return <div key={session.sessionId} className="waiting-sub-session">
        <p className="waiting-sub-session-line"><SessionReference sessionId={session.sessionId} />
          <span>{t("Waits for your answer")}</span></p>
        {reviewer && <CommandPermissionPanel reviewer={reviewer} epoch={epoch} sessionId={session.sessionId} canReview={canReview} running onAnswered={onAnswered}
          label={t("Pending permissions of {title}", { title: session.title || t("A session") })} />}
      </div>;
    })}
  </section>;
}
