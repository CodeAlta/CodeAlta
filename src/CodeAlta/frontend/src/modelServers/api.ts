import { modelServers } from "#neoastra";
import type { HubModel, HubModelView, ModelHostAction, ModelHostActionResult, ModelHostStatus, ModelServersMachine } from "./types";

/** What the pages of the model servers ask the host, with the records of the model hosts read from their JSON. */
export type ModelServersApi = Readonly<{
  machines(signal?: AbortSignal): Promise<readonly ModelServersMachine[]>;
  status(machine: string, signal?: AbortSignal): Promise<ModelHostStatus>;
  act(machine: string, action: ModelHostAction): Promise<ModelHostActionResult>;
  useInSessions(machine: string, server: string, name: string, machines: readonly string[]): Promise<readonly Readonly<{ machine: string; problem?: string | null }>[]>;
  hubSearch(query: Readonly<{ search?: string; author?: string; pipeline?: string; sort?: string; limit?: number }>, signal?: AbortSignal): Promise<readonly HubModel[]>;
  hubModel(repo: string, machines: readonly string[], signal?: AbortSignal): Promise<HubModelView>;
  watch(signal: AbortSignal, onEvent: (event: ModelServersWatchEvent) => void): Promise<void>;
}>;

/** A change of the model servers of a machine, or the end of a download or an install there. */
export type ModelServersWatchEvent = Readonly<{ kind: "changed" | "download" | "task"; machine: string; json?: string | null }>;

/** Why a call of the model servers failed, in the words of the host. */
export class ModelServersError extends Error {
  constructor(readonly status: string, message: string | null | undefined) {
    super(message || status);
  }
}

function read<T>(response: Readonly<{ status: string; json?: string | null; message?: string | null }>): T {
  if (response.status !== "ok" || !response.json) throw new ModelServersError(response.status, response.message);
  return JSON.parse(response.json) as T;
}

/** The API of the model servers over the `modelServers` service of a window of an epoch. */
export function modelServersApi(epoch: string): ModelServersApi {
  const expectedEpoch = epoch;
  return {
    async machines(signal) {
      const response = await modelServers.machines({ expectedEpoch }, { signal, timeoutMilliseconds: 30_000 });
      if (response.status !== "ok") throw new ModelServersError(response.status, null);
      return response.machines;
    },
    async status(machine, signal) {
      return read<ModelHostStatus>(await modelServers.status({ expectedEpoch, machine }, { signal, timeoutMilliseconds: 30_000 }));
    },
    async act(machine, action) {
      return read<ModelHostActionResult>(await modelServers.act({ expectedEpoch, machine, action: JSON.stringify(action) }, { timeoutMilliseconds: 600_000 }));
    },
    async useInSessions(machine, server, name, machines) {
      const response = await modelServers.useInSessions({ expectedEpoch, machine, server, name, machines: [...machines] }, { timeoutMilliseconds: 60_000 });
      if (response.status !== "ok") throw new ModelServersError(response.status, null);
      return response.results;
    },
    async hubSearch(query, signal) {
      return read<HubModel[]>(await modelServers.hubSearch({ expectedEpoch, search: query.search ?? null, author: query.author ?? null, pipeline: query.pipeline ?? null,
        sort: query.sort ?? "trendingScore", limit: query.limit ?? 30 }, { signal, timeoutMilliseconds: 30_000 }));
    },
    async hubModel(repo, machines, signal) {
      return read<HubModelView>(await modelServers.hubModel({ expectedEpoch, repo, machines: [...machines] }, { signal, timeoutMilliseconds: 60_000 }));
    },
    async watch(signal, onEvent) {
      for await (const event of await modelServers.watch({ expectedEpoch }, { signal })) onEvent(event as ModelServersWatchEvent);
    },
  };
}
