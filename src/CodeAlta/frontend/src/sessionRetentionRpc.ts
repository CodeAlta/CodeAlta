import { invoke, invokeChannel } from "@neoastra/client";
import { neoRpcContractHash } from "#neoastra";
import type { RetentionPublication, RetentionReply } from "./sessionRetention";

/** The first item acknowledges lifetime ownership; opening a channel alone does not. */
export async function watchSessionRetention(request: { expectedEpoch: string; pageId: string }, signal: AbortSignal): Promise<AsyncIterable<RetentionReply>> {
  if ((neoRpcContractHash as string) === "demo") return (async function* () {
    yield { accepted: true, reason: null };
    if (!signal.aborted) await new Promise<void>(resolve => signal.addEventListener("abort", () => resolve(), { once: true }));
  })();
  return invokeChannel<typeof request, RetentionReply>("sessionRetention.watch", request, { contractHash: neoRpcContractHash, signal });
}

/** Same invocation/contract-hash boundary as generated services, using the stable sessionRetention wire name. */
export function publishSessionRetention(request: RetentionPublication): Promise<RetentionReply> {
  // The demo contract (also used by isolated browser fixtures) has no native host or retention worker.
  if ((neoRpcContractHash as string) === "demo") return Promise.resolve({ accepted: true, reason: null });
  return invoke<RetentionPublication, RetentionReply>("sessionRetention.publish", request,
    { contractHash: neoRpcContractHash, timeoutMilliseconds: 8000 });
}
