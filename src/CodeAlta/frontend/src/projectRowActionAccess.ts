import type { WorkspaceProject } from "#neoastra";
import { selectedProjectDetails, type ProjectDetailsContext } from "./ProjectDetailsEntry";

export type ProjectRowContext = ProjectDetailsContext & Readonly<{
  generation: number; modalGeneration: number; canMutate: boolean; locked: boolean;
}>;

export function projectRowAccess(project: WorkspaceProject, context: ProjectRowContext) {
  const exact = selectedProjectDetails({ ...context, projectId: project.id });
  const open = !!exact && exact.path === project.path && exact.name === project.name && exact.archived === project.archived;
  const mutate = open && context.projectId === project.id && context.hostAvailable && !!context.hostEpoch
    && context.canMutate && !context.locked;
  return { open, details: open, rename: mutate && !project.archived, archive: mutate };
}

export function projectRowCurrent(project: WorkspaceProject, captured: ProjectRowContext, current: ProjectRowContext) {
  return captured.active && current.active && captured.snapshot === current.snapshot
    && captured.projectId === current.projectId && captured.sessionId === current.sessionId
    && captured.hostEpoch === current.hostEpoch && captured.hostAvailable === current.hostAvailable
    && captured.refreshVersion === current.refreshVersion && captured.refreshReady && current.refreshReady
    && captured.generation === current.generation && captured.modalGeneration === current.modalGeneration
    && captured.canMutate === current.canMutate && captured.locked === current.locked
    && projectRowAccess(project, current).open;
}
