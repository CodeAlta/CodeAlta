import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";

export type SavedProjectIdentity = Pick<WorkspaceProject, "id" | "path" | "name" | "archived">;

// An ID or path collision is not a navigable identity, even if one row happens to match both.
export function savedProjectSelection(shown: SavedProjectIdentity, current: WorkspaceSnapshot | undefined): WorkspaceProject | null {
  if (!current?.configured || !shown.id || !shown.path) return null;
  const matches = current.projects.filter(project => project.id === shown.id || project.path === shown.path);
  return matches.length === 1 && matches[0].id === shown.id && matches[0].path === shown.path
    && matches[0].name === shown.name && matches[0].archived === shown.archived ? matches[0] : null;
}
