// A project that a start of alta added (`alta .`, `alta <folder>`) while CodeAlta runs: the window selects it.

/**
 * The space in which the window shows a project it was asked to show: the space shown when it has the project, else
 * the space the project joined, else the default space, which has every project.
 */
export function spaceShowingProject(projectId: string, shownProjects: readonly Readonly<{ id: string }>[], shownSpace: string,
  joinedSpace: string | null, defaultSpace: string): string {
  if (shownProjects.some(project => project.id === projectId)) return shownSpace;
  return joinedSpace ?? defaultSpace;
}

/** Waits until a project is in the catalog the window reads again, at most a few seconds; true once it is. */
export async function whenProjectKnown(projectId: string, projects: () => readonly Readonly<{ id: string }>[] | undefined,
  wait: (milliseconds: number) => Promise<void> = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)), attempts = 50): Promise<boolean> {
  for (let attempt = 0; attempt < attempts; attempt++) {
    if (projects()?.some(project => project.id === projectId)) return true;
    await wait(100);
  }
  return !!projects()?.some(project => project.id === projectId);
}
