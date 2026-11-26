import { useState, type RefObject } from "react";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SessionInfoDialog } from "../SessionInfoDialog";
import { sessionInfoView } from "../sessionInfo";
import type { SessionStorageHost } from "../sessionStorage/sessionStorage";

/** Opens recorded session info from Saved Sessions, resolving the selected id against the latest catalog. */
export function useArchivedSessionInfo(catalog: RefObject<WorkspaceSnapshot | undefined>, demo: boolean,
  storage: (session: WorkspaceSession) => SessionStorageHost | null) {
  const [id, setId] = useState<string | null>(null);
  const latest = catalog.current;
  const session = id && latest?.sessions.find(row => row.id === id);
  const dialog = session && latest ? <SessionInfoDialog info={sessionInfoView(latest, session, session.projectId)}
    demo={demo} onClose={() => setId(null)} storage={storage(session)} /> : null;
  return { show: (row: WorkspaceSession) => setId(row.id), dialog };
}
