import type { fleetServer, FleetDeviceView, FleetServerView } from "#neoastra";
import { waitingHolds } from "./budgets";

/** A device that waits for the approval of an owner of a server. */
export interface WaitingApproval { server: FleetServerView; device: FleetDeviceView }

/** The devices that wait for an approval on the servers this computer owns, the oldest first. */
export function waitingApprovals(lists: readonly { server: FleetServerView; devices: readonly FleetDeviceView[] }[]): WaitingApproval[] {
  return lists.flatMap(({ server, devices }) => devices.filter(device => device.status === "pending").map(device => ({ server, device })))
    .sort((left, right) => left.device.createdAt.localeCompare(right.device.createdAt) || left.device.id.localeCompare(right.device.id));
}

/** The key of the toast that says a run waits for a budget before the server lists its hold: the toast of the hold takes its place. */
export const heldRunToastKey = (runId: string) => `fleet-held-run:${runId}`;

/** What Continue anyway did for a run found by its id: continued, not listed yet by a server this computer owns, or refused. */
export type HeldRunOutcome = "continued" | "not_found" | "failed";

/**
 * Continues anyway the run a reached budget holds, found by its run id (the session, for the first prompt of a new session
 * on a machine) among the holds of the servers this computer owns: signed by this computer, for that run only.
 */
export async function continueHeldRun(api: Pick<typeof fleetServer, "status" | "budgets" | "continueRun">, epoch: string, runId: string): Promise<HeldRunOutcome> {
  try {
    const status = await api.status({ expectedEpoch: epoch }, { timeoutMilliseconds: 15_000 });
    for (const server of status.servers.filter(server => server.controller && server.link === "approved")) {
      const budgets = await api.budgets({ expectedEpoch: epoch, linkId: server.id }, { timeoutMilliseconds: 15_000 }).catch(() => null);
      const hold = budgets?.status === "ok" ? waitingHolds(budgets).find(hold => hold.runId === runId) : undefined;
      if (!hold) continue;
      const result = await api.continueRun({ expectedEpoch: epoch, linkId: server.id, id: hold.id }, { timeoutMilliseconds: 30_000 });
      return result.status === "ok" ? "continued" : "failed";
    }
    return "not_found";
  } catch { return "failed"; }
}

/** The key of the plugin of the Fleet canvas and the id of the canvas itself (`DesktopFleetPlugin`): a plugin of the application is keyed `builtin:<id>`. */
export const fleetCanvasRef = { pluginKey: "builtin:fleet", id: "fleet" } as const;

/** The Fleet canvas among the canvases of the window, which the Fleet button opens; null while its plugin does not run. */
export function fleetCanvasOf<T extends { pluginKey: string; id: string }>(canvases: readonly T[]): T | null {
  return canvases.find(item => item.pluginKey === fleetCanvasRef.pluginKey && item.id === fleetCanvasRef.id) ?? null;
}
