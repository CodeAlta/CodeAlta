import type { FleetDeviceView, FleetServerView } from "#neoastra";

/** A device that waits for the approval of an owner of a server. */
export interface WaitingApproval { server: FleetServerView; device: FleetDeviceView }

/** The devices that wait for an approval on the servers this computer owns, the oldest first. */
export function waitingApprovals(lists: readonly { server: FleetServerView; devices: readonly FleetDeviceView[] }[]): WaitingApproval[] {
  return lists.flatMap(({ server, devices }) => devices.filter(device => device.status === "pending").map(device => ({ server, device })))
    .sort((left, right) => left.device.createdAt.localeCompare(right.device.createdAt) || left.device.id.localeCompare(right.device.id));
}

/** The id of the plugin of the Fleet canvas and of the canvas itself (`DesktopFleetPlugin`). */
export const fleetCanvasRef = { pluginKey: "fleet", id: "fleet" } as const;

/** The Fleet canvas among the canvases of the window, which the Fleet button opens; null while its plugin does not run. */
export function fleetCanvasOf<T extends { pluginKey: string; id: string }>(canvases: readonly T[]): T | null {
  return canvases.find(item => item.pluginKey === fleetCanvasRef.pluginKey && item.id === fleetCanvasRef.id) ?? null;
}
