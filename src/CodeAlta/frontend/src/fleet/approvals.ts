import type { FleetDeviceView, FleetServerView } from "#neoastra";

/** A device that waits for the approval of an owner of a server. */
export interface WaitingApproval { server: FleetServerView; device: FleetDeviceView }

/** The devices that wait for an approval on the servers this computer owns, the oldest first. */
export function waitingApprovals(lists: readonly { server: FleetServerView; devices: readonly FleetDeviceView[] }[]): WaitingApproval[] {
  return lists.flatMap(({ server, devices }) => devices.filter(device => device.status === "pending").map(device => ({ server, device })))
    .sort((left, right) => left.device.createdAt.localeCompare(right.device.createdAt) || left.device.id.localeCompare(right.device.id));
}
