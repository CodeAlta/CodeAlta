import type { SessionNotesRequest } from "#neoastra";

export type NotesState = { kind: "loading" } | { kind: "ready"; markdown: string } | { kind: "error"; code: string };
export type NotesClearState = { kind: "clearing" } | { kind: "cleared" } | { kind: "error"; code: string };
export function canClearNotes(state: NotesState | undefined, uncertain: NotesClearState | undefined,
  canMutate: boolean, action: NotesClearState | undefined): boolean {
  return canMutate && state?.kind === "ready" && !!state.markdown && !uncertain && action?.kind !== "clearing"
    && !(action?.kind === "error" && action.code === "clear_unconfirmed");
}
type Invoke = (request: SessionNotesRequest, options: { timeoutMilliseconds: number }) => Promise<unknown>;
type Original = { work?: Promise<void>; waiter?: Promise<unknown> };

function object(value: unknown): value is Record<string, unknown> { return !!value && typeof value === "object" && !Array.isArray(value); }
function guid(value: unknown): value is string {
  return typeof value === "string" && value.length === 36 && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function text(value: unknown, maximum: number): value is string {
  if (typeof value !== "string" || value.length > maximum) return false;
  for (let i = 0; i < value.length; i++) {
    const high = value.charCodeAt(i);
    if (high < 0xd800 || high > 0xdfff) continue;
    if (high > 0xdbff || ++i === value.length) return false;
    const low = value.charCodeAt(i); if (low < 0xdc00 || low > 0xdfff) return false;
  }
  return true;
}
function whitespace(code: number): boolean {
  return (code >= 9 && code <= 13) || code === 32 || code === 0x85 || code === 0xa0 || code === 0x1680
    || (code >= 0x2000 && code <= 0x200a) || code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000;
}
function identity(value: unknown): value is string {
  return text(value, 256) && value.length > 0 && !whitespace(value.charCodeAt(0)) && !whitespace(value.charCodeAt(value.length - 1))
    && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
}
function status(value: unknown): value is string {
  return typeof value === "string" && ["ok", "invalid_request", "stale_epoch", "missing_session", "closed", "capacity", "read_failed", "wire_limit"].includes(value);
}

// App-owned selection coordination. No cached notes or automatic mutation retry.
export function createNotesReader(invoke: Invoke, clear?: Invoke) {
  let selection = 0;
  let active: Original | undefined;
  let activeClear: Original | undefined;
  const uncertainClears = new Map<string, NotesClearState>(); // No Markdown; retain uncertain outcomes across selection/unmount.
  let hostEpoch: string | undefined;
  let invalid = false;
  const observeEpoch = (value: unknown, epoch: string, revoke: () => void) => {
    if (object(value) && guid(value.hostEpoch) && (status(value.status) || value.status === "clear_unconfirmed")
      && (value.status === "stale_epoch" || value.hostEpoch !== epoch)) {
      invalid = true;
      uncertainClears.clear();
      try { revoke(); } catch { /* The local read latch still remains revoked. */ }
    }
  };
  return {
    forSelection(epoch: string, sessionId: string, signal: AbortSignal, publish: (state: NotesState) => void, revoke: () => void) {
      if (hostEpoch !== epoch) { uncertainClears.clear(); hostEpoch = epoch; }
      const selected = ++selection;
      const clearKey = `${epoch}:${sessionId}`;
      const request = Object.freeze({ expectedHostEpoch: epoch, sessionId });
      const current = () => !signal.aborted && selected === selection;
      const show = (state: NotesState) => { if (current()) { try { publish(state); } catch { /* Presentation cannot change original ownership. */ } } };
      function read(reconcile: boolean): Promise<void> {
        if (activeClear) { show({ kind: "error", code: "busy" }); return activeClear.work!; }
        if (active) { show({ kind: "error", code: "busy" }); return active.work!; }
        if (!current()) return Promise.resolve();
        if (!guid(epoch) || !identity(sessionId)) { show({ kind: "error", code: "invalid_request" }); return Promise.resolve(); }
        if (invalid) { show({ kind: "error", code: "stale_epoch" }); return Promise.resolve(); }
        const original: Original = {};
        active = original; // Synchronous exclusion precedes transport and presentation callbacks.
        let release!: () => void;
        const launch = new Promise<void>(resolve => { release = resolve; });
        original.work = (async () => {
          await launch;
          try {
            // Selection cancellation only detaches presentation. Keep the original response available
            // for epoch evidence; the bridge deadline does not terminate the backend's actual read.
            original.waiter = invoke(request, { timeoutMilliseconds: 8000 });
            const value = await original.waiter;
            observeEpoch(value, request.expectedHostEpoch, revoke);
            if (invalid || hostEpoch !== epoch) { show({ kind: "error", code: "stale_epoch" }); return; }
            if (!object(value) || !guid(value.hostEpoch) || value.hostEpoch !== epoch || !status(value.status)
              || value.sessionId !== sessionId || (value.status === "ok" ? !text(value.markdown, 16384) : value.markdown !== null)) {
              show({ kind: "error", code: "invalid_response" }); return;
            }
            if (value.status === "ok") {
              if (reconcile && (value.markdown as string).length > 0) uncertainClears.delete(clearKey);
              show({ kind: "ready", markdown: value.markdown as string });
            }
            else show({ kind: "error", code: value.status });
          } catch { show({ kind: "error", code: "read_failed" }); }
          finally { if (active === original) active = undefined; }
        })();
        show({ kind: "loading" });
        release();
        return original.work;
      }
      return {
        uncertainClear(): NotesClearState | undefined { return uncertainClears.get(clearKey); },
        refresh(): Promise<void> { return read(false); },
        reconcile(): Promise<void> { return read(true); },
        clear(publishResult: (state: NotesClearState) => void, onCleared: () => void): Promise<void> {
          const result = (state: NotesClearState) => {
            if (hostEpoch === epoch && !invalid) {
              if (state.kind === "error" && state.code === "clear_unconfirmed") uncertainClears.set(clearKey, state);
              if (state.kind === "cleared") uncertainClears.delete(clearKey);
            }
            if (current()) publishResult(state);
          };
          if (activeClear || active) { result({ kind: "error", code: "busy" }); return (activeClear ?? active)!.work!; }
          if (!current() || !clear) return Promise.resolve();
          if (!guid(epoch) || !identity(sessionId)) { result({ kind: "error", code: "invalid_request" }); return Promise.resolve(); }
          if (invalid || hostEpoch !== epoch) { result({ kind: "error", code: "stale_epoch" }); return Promise.resolve(); }
          if (uncertainClears.has(clearKey)) { result({ kind: "error", code: "clear_unconfirmed" }); return Promise.resolve(); }
          const original: Original = {};
          activeClear = original;
          let release!: () => void;
          const launch = new Promise<void>(resolve => { release = resolve; });
          original.work = (async () => {
            await launch;
            try {
              original.waiter = clear(request, { timeoutMilliseconds: 8000 });
              const value = await original.waiter;
              observeEpoch(value, epoch, revoke);
              if (invalid) { result({ kind: "error", code: "stale_epoch" }); return; }
              if (!object(value) || !guid(value.hostEpoch) || value.hostEpoch !== epoch || value.sessionId !== sessionId
                || !["ok", "invalid_request", "missing_session", "closed", "stale_epoch", "clear_unconfirmed"].includes(String(value.status))) {
                result({ kind: "error", code: "clear_unconfirmed" }); return;
              }
              if (value.status === "ok") {
                if (current()) { show({ kind: "ready", markdown: "" }); onCleared(); }
                result({ kind: "cleared" });
              } else result({ kind: "error", code: value.status as string });
            } catch { result({ kind: "error", code: "clear_unconfirmed" }); }
            finally { if (activeClear === original) activeClear = undefined; }
          })();
          result({ kind: "clearing" });
          release();
          return original.work;
        },
      };
    },
  };
}

export async function copyNotesMarkdown(markdown: string, write: (text: string) => Promise<void>): Promise<"copied" | "copy_failed"> {
  try { await write(markdown); return "copied"; }
  catch { return "copy_failed"; }
}

export function notesMessage(code: string): string {
  switch (code) {
    case "busy": return "The original notes read is still pending. Refresh explicitly after it settles.";
    case "capacity": return "The host's shared read capacity is full. No notes read was admitted.";
    case "missing_session": return "No known session matches this notes scope.";
    case "closed": return "Host read admission is closed.";
    case "stale_epoch": return "Host identity changed. Reload before further operations.";
    case "wire_limit": return "The complete notes cannot be displayed within this view's text limit. Nothing was truncated.";
    default: return "Notes could not be read or validated. This is not empty notes. Refresh is manual.";
  }
}
