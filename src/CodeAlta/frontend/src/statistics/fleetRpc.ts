import { fleetServer } from "#neoastra";
import type { FleetStatisticsApi } from "./fleet";

// The "My fleet" scope over the `fleetServer` service of the window, which asks CodeAlta Server for the sums with this computer's device key
// and names what only this computer can name. The page never reaches the server itself.

/** The binding of the scope for a host lifetime of the window. */
export function createFleetApi(epoch: string): FleetStatisticsApi {
  return {
    available: async signal => {
      const status = await fleetServer.status({ expectedEpoch: epoch }, { signal });
      return status.status === "ok" && status.servers.some(server => server.link === "approved");
    },
    statistics: (query, signal) => fleetServer.statistics({ expectedEpoch: epoch, linkId: null, from: query.from, to: query.to, timeZone: query.timeZone, groupBy: [...query.groupBy] },
      { signal, timeoutMilliseconds: 30_000 }),
  };
}
