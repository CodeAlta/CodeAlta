import type { SessionAdmission, SessionReceiptPage, SessionReceiptRequest, SessionSendRequest } from "#neoastra";

type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
export type SubmissionResult = SessionAdmission | { status: "uncertain"; epoch: string; receipt: null };

export function createMutationCapability(epoch: string) {
  let valid = true;
  return {
    canMutate: () => valid,
    canSubmit: (request: SessionSendRequest) => valid && request.expectedEpoch === epoch,
    observe(result: { status: string; epoch: string | null }): boolean {
      if (result.status === "stale_epoch" || result.status === "stale_runtime" || (result.epoch !== null && result.epoch !== epoch)) valid = false;
      return valid;
    },
  };
}

export function captureSubmission(epoch: string, sessionId: string, text: string, key: string): SessionSendRequest {
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, text });
}

export function hasSubmissionReceipt(request: SessionSendRequest, page: SessionReceiptPage): boolean {
  return page.status === "ok" && page.epoch === request.expectedEpoch
    && page.rows.some(row => row.kind === "Send" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId);
}

export async function sendSubmission(
  invoke: (request: SessionSendRequest, options: WaitOptions) => Promise<SessionAdmission>,
  request: SessionSendRequest, signal: AbortSignal, publish: (result: SubmissionResult) => void,
  capability: ReturnType<typeof createMutationCapability>,
): Promise<void> {
  if (signal.aborted || !capability.canSubmit(request)) return;
  try {
    const result = await invoke(request, { signal, timeoutMilliseconds: 8_000 });
    if (!signal.aborted) { capability.observe(result); publish(result); }
  } catch {
    if (!signal.aborted) publish({ status: "uncertain", epoch: request.expectedEpoch, receipt: null });
  }
}

export async function refreshSubmissions(
  invoke: (request: SessionReceiptRequest, options: WaitOptions) => Promise<SessionReceiptPage>,
  epoch: string, offset: number, signal: AbortSignal, publish: (result: SessionReceiptPage) => void,
): Promise<void> {
  if (signal.aborted) return;
  try {
    const result = await invoke({ expectedEpoch: epoch, offset }, { signal, timeoutMilliseconds: 8_000 });
    if (!signal.aborted) publish(result);
  } catch {
    if (!signal.aborted) publish({ status: "read_failed", epoch, rows: [], next: null });
  }
}
