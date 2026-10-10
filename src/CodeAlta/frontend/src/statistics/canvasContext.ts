import type { Alta } from "../pluginScript/alta";
import type { StatisticsContext } from "./api";
import { weekDayIndex } from "./frame";
import type { StatisticsDirectory } from "./rpcApi";

// What the canvas is given from the place it is opened at (see canvas.tsx).

/** The start of the key of a canvas opened for one project (`project:<id>`), as the plugin writes it. */
export const projectKeyPrefix = "project:";

/** The project a canvas was opened for: the one its key names (it survives a restart), or the one its input names. */
export function projectOf(context: Pick<Alta["context"], "key" | "input">): string | null {
  const key = context.key;
  if (key && key.startsWith(projectKeyPrefix) && key.length > projectKeyPrefix.length) return key.slice(projectKeyPrefix.length);
  const input = context.input;
  if (typeof input === "object" && input !== null && typeof (input as { project?: unknown }).project === "string" && (input as { project: string }).project) return (input as { project: string }).project;
  return null;
}

/**
 * What the canvas is given from the place it is opened at: its instance, the space it shows (nothing for the space that holds every project), the project
 * it is limited to, the spaces with their projects, and how to open a session.
 */
export function statisticsContext(alta: Pick<Alta, "context" | "host">, visible: boolean, directory: StatisticsDirectory | null): StatisticsContext {
  const project = projectOf(alta.context);
  const space = directory?.spaces.find(item => item.id === alta.context.spaceId) ?? null;
  const known = project ? directory?.projects.find(item => item.id.toLowerCase() === project.toLowerCase()) : undefined;
  return {
    instanceId: alta.context.instanceId ?? "statistics",
    visible,
    // The default space has every project: filtering on it would say nothing.
    spaceId: project || space?.isDefault ? null : alta.context.spaceId,
    spaces: directory?.spaces.map(item => ({ id: item.id, name: item.name, projectIds: item.projectIds })),
    projectId: project,
    projectName: known?.name ?? null,
    providers: directory?.providers,
    weekStart: weekDayIndex(directory?.weekStart),
    openSession: id => alta.host.openSession(id),
  };
}
