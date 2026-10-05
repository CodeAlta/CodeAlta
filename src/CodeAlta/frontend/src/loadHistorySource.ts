import type { HistorySourceRequest, HistorySourceResponse } from "#neoastra";
import { rpcFailureCode } from "./rpcDiagnostics";

// Actual production loader; a replaced/canceled review never publishes its late chunk.
export async function loadHistorySource(read: (request: HistorySourceRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<HistorySourceResponse>,
  request: HistorySourceRequest, signal: AbortSignal, current: () => boolean, publish: (value: HistorySourceResponse) => void): Promise<void> {
  if (signal.aborted || !current()) return;
  try {
    const value = await read(request, { signal, timeoutMilliseconds: 30_000 });
    if (signal.aborted || !current()) return;
    if (value.status === "ok" && (value.text === null || value.text.length > 16 * 1024 || value.nextOffset !== null &&
      (!/^\d{1,19}$/.test(value.nextOffset) || BigInt(value.nextOffset) <= BigInt(request.offset) || BigInt(value.nextOffset) >= BigInt(request.end)))) {
      publish({ status: "wire_limit", text: null, nextOffset: null }); return;
    }
    publish(value);
  } catch (error) {
    if (signal.aborted || !current()) return;
    // The host logs its own read failures; this one never reached it or never answered.
    console.warn("[CodeAlta History] source request failed", { code: rpcFailureCode(error) });
    publish({ status: "read_failed", text: null, nextOffset: null });
  }
}
