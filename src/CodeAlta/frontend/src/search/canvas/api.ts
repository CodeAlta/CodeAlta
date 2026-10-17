import type { localSearch, LocalSearchActivityResponse, LocalSearchLocateResponse, LocalSearchOverviewResponse, LocalSearchQueryResponse, LocalSearchStatusResponse } from "#neoastra";

// What the Search canvas asks of the host, for one host and one space: the calls of `localSearch` with the epoch and the space filled in.
// A fixture gives its own.

/** A query of Explore. */
export type CanvasQuery = Readonly<{ query: string; kind: string | null; projectId: string | null; typing: boolean; limit: number; mode: "words" | "meaning" | "auto" }>;

/** The questions of the canvas. Each may throw when the host is gone; the canvas then keeps what it shows. */
export type SearchCanvasApi = Readonly<{
  status(signal: AbortSignal): Promise<LocalSearchStatusResponse>;
  overview(signal: AbortSignal): Promise<LocalSearchOverviewResponse>;
  query(request: CanvasQuery, signal: AbortSignal): Promise<LocalSearchQueryResponse>;
  browse(kind: string, projectId: string | null, limit: number, signal: AbortSignal): Promise<LocalSearchQueryResponse>;
  locate(ref: string, signal: AbortSignal): Promise<LocalSearchLocateResponse>;
  /** How search was used in the last 90 days. */
  activity(signal: AbortSignal): Promise<LocalSearchActivityResponse>;
  watch(signal: AbortSignal): Promise<AsyncIterable<unknown>>;
}>;

export type LocalSearchClient = Pick<typeof localSearch, "status" | "overview" | "query" | "browse" | "locate" | "activity" | "watch">;

/** The questions of the canvas over the client of the host, for the host of `epoch` and the space `spaceId`. */
export function createSearchCanvasApi(client: LocalSearchClient, epoch: string, spaceId: string): SearchCanvasApi {
  const expectedEpoch = epoch;
  return Object.freeze({
    status: signal => client.status({ expectedEpoch }, { signal, timeoutMilliseconds: 20_000 }),
    overview: signal => client.overview({ expectedEpoch, spaceId }, { signal, timeoutMilliseconds: 20_000 }),
    query: (request, signal) => client.query({ expectedEpoch, query: request.query, kinds: request.kind ? [request.kind] : null, spaceId, projectId: request.projectId,
      limit: request.limit, typing: request.typing, mode: request.mode }, { signal, timeoutMilliseconds: 20_000 }),
    browse: (kind, projectId, limit, signal) => client.browse({ expectedEpoch, kind, spaceId, projectId, limit }, { signal, timeoutMilliseconds: 20_000 }),
    locate: (ref, signal) => client.locate({ expectedEpoch, ref }, { signal, timeoutMilliseconds: 20_000 }),
    activity: signal => client.activity({ expectedEpoch }, { signal, timeoutMilliseconds: 20_000 }),
    watch: signal => client.watch({ expectedEpoch }, { signal }),
  });
}
