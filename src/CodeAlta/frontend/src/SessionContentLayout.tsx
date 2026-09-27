import type { ReactNode } from "react";

// Stable content slots: changing Explorer geometry never moves or remounts owners.
// Real session-tab model integration is separate from this presentation grid.
export function SessionContentLayout({ sessions, content, splitter, sessionsHidden, projects }: {
  sessions: ReactNode; content: ReactNode; splitter: ReactNode;
  projects?: (sessions: ReactNode) => ReactNode;
  sessionWidth: number; narrow: boolean; sessionsHidden: boolean;
}) {
  return <div className="session-content-layout">
    <div className="session-content-rail-slot" hidden={sessionsHidden}>{projects ? projects(sessions) : sessions}</div>
    {splitter}
    <div className="session-content-main-panel">{content}</div>
  </div>;
}
