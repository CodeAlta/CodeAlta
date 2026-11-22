import { useState } from "react";
import type { WorkspaceProject } from "#neoastra";
import { MoveSessionDialog } from "../MoveSessionDialog";
import type { SessionMoveDrag } from "./sessionDrag";

/** Keeps the session a project move was opened for, separately from moves started by dropping a row. */
export function useSessionMoveDialog(projects: readonly WorkspaceProject[] | undefined, busy: boolean,
  onMove: (request: SessionMoveDrag, project: WorkspaceProject) => void) {
  const [request, setRequest] = useState<SessionMoveDrag | null>(null);
  const close = () => setRequest(null);
  const dialog = request && projects ? <MoveSessionDialog title={request.title} projectId={request.from.projectId}
    projects={projects} busy={busy} onMove={project => onMove(request, project)} onClose={close} /> : null;
  return { open: (move: SessionMoveDrag) => setRequest(move), close, dialog };
}
