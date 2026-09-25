import type { ComponentProps, ReactNode } from "react";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { ReminderPanel } from "./ReminderPanel";

export function archivedProjectScope(snapshot: WorkspaceSnapshot, projectId: string | null): boolean {
  return projectId !== null && snapshot.projects.some(project => project.id === projectId && project.archived);
}

// A retained owner is keyed by session/epoch; verify catalog scope before revealing its text.
export function archivedRecoveryTarget(snapshot: WorkspaceSnapshot, projectId: string | null,
  session: WorkspaceSession | undefined, epoch: string | null): boolean {
  if (!epoch || !session || !archivedProjectScope(snapshot, projectId)) return false;
  const projects = snapshot.projects.filter(project => project.id === projectId);
  const sessions = snapshot.sessions.filter(row => row.id === session.id);
  return projects.length === 1 && projects[0].path === session.workspacePath && session.scopeKind === "project"
    && session.projectId === projectId && sessions.length === 1 && sessions[0].scopeKind === session.scopeKind
    && sessions[0].projectId === session.projectId && sessions[0].workspacePath === session.workspacePath;
}

export function SessionComposerGate({ snapshot, projectId, session, epoch, owned, readOnly, recovery }: {
  snapshot: WorkspaceSnapshot; projectId: string | null; session: WorkspaceSession;
  epoch: string | null; owned: ReactNode; readOnly: ReactNode; recovery: ReactNode;
}) {
  if (archivedProjectScope(snapshot, projectId)) return <>{readOnly}
    {archivedRecoveryTarget(snapshot, projectId, session, epoch) && recovery}</>;
  return epoch ? owned : readOnly;
}

export function ReminderScopeGate({ snapshot, projectId, session, epoch, ...panel }: {
  snapshot: WorkspaceSnapshot | undefined; projectId: string | null; session: WorkspaceSession | undefined;
  epoch: string | null;
} & Omit<ComponentProps<typeof ReminderPanel>, "target" | "mutationAllowed" | "canMutate"> & {
  mutationAllowed: boolean; canMutate: () => boolean;
}) {
  const archived = !!snapshot && archivedProjectScope(snapshot, projectId);
  const target = snapshot && epoch && session && (!archived || archivedRecoveryTarget(snapshot, projectId, session, epoch))
    ? { epoch, sessionId: session.id } : null;
  return <ReminderPanel key={target ? JSON.stringify([target.epoch, target.sessionId]) : "none"} {...panel}
    target={target} readOnly={archived} mutationAllowed={panel.mutationAllowed && !archived}
    canMutate={() => !archived && panel.canMutate()} />;
}
