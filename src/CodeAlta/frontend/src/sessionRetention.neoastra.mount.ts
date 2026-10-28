// Isolated real-shell fixture: no native bridge, profile, or provider work.
export * from "./projectFocus.neoastra.mount";
export { spaces } from "./spaces/spaces.neoastra.mount";
import { pluginUi as original, sessionOperations as operations } from "./projectFocus.neoastra.mount";
import type { PluginUiAnswer, PluginUiEvent, SessionSendRequest, SessionAdmission, SessionRuntimeStateRequest, SessionRuntimeStateResponse } from "#neoastra";

const pending: PluginUiEvent[] = [];
const answers: PluginUiAnswer[] = [];
const delivered: string[] = [];
const runtimeWaiters: (() => void)[] = [];
let runtimeReady = false;
let wake: (() => void) | undefined;
const sends: { request: SessionSendRequest; signal: AbortSignal; resolve: (value: SessionAdmission) => void }[] = [];
Object.assign(window, { retentionPlugins: { answers, sends, delivered,
  emit(event: PluginUiEvent) { pending.push(event); wake?.(); },
  allowRuntime() { runtimeReady = true; for (const resolve of runtimeWaiters.splice(0)) resolve(); },
  busy(index: number) { const { request, resolve } = sends[index]; resolve({ status: "busy", epoch: request.expectedEpoch, receipt: null }); },
  accept(index: number, terminal = false) {
    const { request, resolve } = sends[index];
    resolve({ status: "accepted", epoch: request.expectedEpoch, receipt: { clientRequestId: request.clientRequestId,
      sessionId: request.sessionId, operationId: "abcdefab-1234-5678-9abc-abcdefabcdef", targetOperationId: null,
      kind: "Send", state: terminal ? "terminal" : "pending", outcome: terminal ? "Completed" : null, code: null, runId: null } });
  },
} });
export const sessionOperations = { ...operations,
  send: (request: SessionSendRequest, { signal }: { signal: AbortSignal }) => new Promise<SessionAdmission>(resolve => sends.push({ request, signal, resolve })),
};
// A saved, unattached session can enqueue a deferred Send; no runtime attachment or Queue RPC exists.
export const sessionRuntimeState = {
  async current(request: SessionRuntimeStateRequest): Promise<SessionRuntimeStateResponse> {
    if (!runtimeReady) await new Promise<void>(resolve => runtimeWaiters.push(resolve));
    return { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
      runtimeInstanceId: request.expectedHostEpoch, coordinatorTransitionInProgress: false, entry: null };
  },
};
export const pluginUi = {
  ...original,
  async *watch(_request: unknown, { signal }: { signal: AbortSignal }) {
    while (!signal.aborted) {
      if (!pending.length) await new Promise<void>(resolve => {
        wake = resolve;
        signal.addEventListener("abort", () => resolve(), { once: true });
      });
      if (!signal.aborted && pending.length) {
        const event = pending.shift()!;
        if (event.requestId) delivered.push(event.requestId);
        yield event;
      }
    }
  },
  async respond(answer: PluginUiAnswer) { answers.push(answer); },
};
