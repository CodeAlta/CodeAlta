import type { SessionRuntimeStateRequest, SessionRuntimeStateResponse } from "#neoastra";

export type RuntimeState =
  | { kind: "loading" }
  | { kind: "ready"; snapshot: SessionRuntimeStateResponse }
  | { kind: "error"; code: string };

// App-owned identity/reload latch only, no snapshot cache or polling. Each selection owns its requests.
export function createRuntimeStateReader(
  invoke: (request: SessionRuntimeStateRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionRuntimeStateResponse>,
) {
  let runtimeInstanceId: string | null = null;
  let reloadCode: string | null = null;
  return {
    forSelection(request: SessionRuntimeStateRequest, signal: AbortSignal, publish: (state: RuntimeState) => void) {
      let generation = 0;
      if (!signal.aborted && reloadCode) publish({ kind: "error", code: reloadCode });
      return {
        async refresh(): Promise<void> {
          if (signal.aborted) return;
          if (reloadCode) { publish({ kind: "error", code: reloadCode }); return; }
          const current = ++generation;
          publish({ kind: "loading" });
          const error = (code: string) => {
            if (code === "stale_epoch" || code === "stale_runtime") reloadCode = code;
            publish({ kind: "error", code: reloadCode ?? code });
          };
          try {
            const snapshot = await invoke(request, { signal, timeoutMilliseconds: 8_000 });
            if (signal.aborted || current !== generation) return;
            if (reloadCode) { error(reloadCode); return; }
            if (snapshot.status === "stale_epoch" || snapshot.hostEpoch !== request.expectedHostEpoch) { error("stale_epoch"); return; }
            if (snapshot.status !== "ok") { error(snapshot.status); return; }
            if (snapshot.sessionId !== request.sessionId || !snapshot.runtimeInstanceId || typeof snapshot.coordinatorTransitionInProgress !== "boolean") {
              error("invalid_response"); return;
            }
            if (runtimeInstanceId !== null && runtimeInstanceId !== snapshot.runtimeInstanceId) { error("stale_runtime"); return; }
            runtimeInstanceId = snapshot.runtimeInstanceId;
            publish({ kind: "ready", snapshot });
          } catch {
            if (!signal.aborted && current === generation) error("read_failed");
          }
        },
      };
    },
  };
}
