export type InputHandle = Readonly<{ operationId: string; runtimeInstanceId: string; attachmentGeneration: string;
  sessionId: string; runId: string | null; interactionId: string; attemptId: string }>;
export type InputAnswer = Readonly<{ promptId: string; value: string }>;
export type InputPrompt = Readonly<{ id: string; question: string; header: string | null;
  options: readonly Readonly<{ label: string; description: string | null }>[]; allowFreeform: boolean }>;
export type InputEntry = Readonly<{ handle: InputHandle; providerId: string; prompts: readonly InputPrompt[] }>;
export type InputPage = Readonly<{ entries: readonly InputEntry[]; hasMore: boolean }>;
type ListRequest = { expectedHostEpoch: string; sessionId: string };
type ActionRequest = { expectedHostEpoch: string; handle: InputHandle; answers: readonly InputAnswer[] };
type Original = { readonly request: Readonly<ActionRequest>; readonly action: "resolve" | "cancel"; readonly waiter: Promise<void>;
  kind: "pending" | "terminal" | "uncertain"; status: string; observed: boolean };
const object = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);
const guid = (v: unknown): v is string => typeof v === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(v) && v !== "00000000-0000-0000-0000-000000000000";
export function inputText(v: unknown, limit: number, identity = false, required = false): v is string {
  if (typeof v !== "string" || v.length > limit || ((identity || required) && !v.trim()) || (identity && (v.trim() !== v || /[\x00-\x1f\x7f-\x9f]/.test(v))) || v.includes("\0")) return false;
  for (let i = 0; i < v.length; i++) {
    const c = v.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) { const low = v.charCodeAt(++i); if (!(low >= 0xdc00 && low <= 0xdfff)) return false; }
    else if (c >= 0xdc00 && c <= 0xdfff) return false;
  }
  return true;
}
export function inputHandle(v: unknown): v is InputHandle {
  return object(v) && guid(v.operationId) && guid(v.runtimeInstanceId) && guid(v.attemptId)
    && inputText(v.sessionId, 128, true) && inputText(v.interactionId, 128, true) && (v.runId === null || inputText(v.runId, 128, true))
    && typeof v.attachmentGeneration === "string" && /^[1-9][0-9]{0,15}$/.test(v.attachmentGeneration)
    && (v.attachmentGeneration.length < 16 || v.attachmentGeneration <= "9007199254740991");
}
const same = (a: InputHandle, b: InputHandle) => a.operationId === b.operationId && a.runtimeInstanceId === b.runtimeInstanceId
  && a.attachmentGeneration === b.attachmentGeneration && a.sessionId === b.sessionId && a.runId === b.runId && a.interactionId === b.interactionId && a.attemptId === b.attemptId;
function copyHandle(h: InputHandle): InputHandle { return Object.freeze({ operationId: h.operationId, runtimeInstanceId: h.runtimeInstanceId,
  attachmentGeneration: h.attachmentGeneration, sessionId: h.sessionId, runId: h.runId, interactionId: h.interactionId, attemptId: h.attemptId }); }
export function inputPage(value: unknown, session: string): InputPage | undefined {
  if (!object(value) || value.status !== "ok" || value.sessionId !== session || typeof value.hasMore !== "boolean" || !Array.isArray(value.entries) || value.entries.length > 4) return;
  const entries: InputEntry[] = []; const attempts = new Set<string>();
  for (const entry of value.entries) {
    if (!object(entry) || !inputHandle(entry.handle) || entry.handle.sessionId !== session || !inputText(entry.providerId, 128, true)
      || attempts.has(entry.handle.attemptId) || !Array.isArray(entry.prompts) || entry.prompts.length < 1 || entry.prompts.length > 8) return;
    attempts.add(entry.handle.attemptId); let total = 0; const ids = new Set<string>(); const prompts: InputPrompt[] = [];
    for (const p of entry.prompts) {
      if (!object(p) || p.isSecret === true || !inputText(p.id, 128, true) || ids.has(p.id) || !inputText(p.question, 1024, false, true)
        || (p.header !== null && !inputText(p.header, 128)) || typeof p.allowFreeform !== "boolean" || !Array.isArray(p.options) || p.options.length > 8 || (!p.allowFreeform && !p.options.length)) return;
      ids.add(p.id); total += p.id.length + p.question.length + (p.header?.length ?? 0);
      const labels = new Set<string>(); const options: { label: string; description: string | null }[] = [];
      for (const o of p.options) {
        if (!object(o) || !inputText(o.label, 256, true) || labels.has(o.label) || (o.description !== null && !inputText(o.description, 512))) return;
        labels.add(o.label); total += o.label.length + (o.description?.length ?? 0); if (total > 8192) return;
        options.push(Object.freeze({ label: o.label, description: o.description }));
      }
      if (total > 8192) return;
      prompts.push(Object.freeze({ id: p.id, question: p.question, header: p.header, options: Object.freeze(options), allowFreeform: p.allowFreeform }));
    }
    entries.push(Object.freeze({ handle: copyHandle(entry.handle), providerId: entry.providerId, prompts: Object.freeze(prompts) }));
  }
  return Object.freeze({ entries: Object.freeze(entries), hasMore: value.hasMore });
}
function answersFor(entry: InputEntry, answers: readonly InputAnswer[]): boolean {
  if (!Array.isArray(answers) || answers.length !== entry.prompts.length) return false;
  const ids = new Set<string>(); let total = 0;
  for (const a of answers) {
    if (!object(a) || !inputText(a.promptId, 128, true) || ids.has(a.promptId) || !inputText(a.value, 2048)) return false;
    ids.add(a.promptId); total += a.value.length; if (total > 8192) return false;
    const p = entry.prompts.find(p => p.id === a.promptId); if (!p || (!p.allowFreeform && !p.options.some(o => o.label === a.value))) return false;
  }
  return true;
}

// App lifetime, not presentation lifetime. No auto list, retry, ledger or browser persistence.
export function createUserInputReviewer(list: (r: ListRequest) => Promise<unknown>, resolve: (r: ActionRequest) => Promise<unknown>,
  cancel: (r: Omit<ActionRequest, "answers">) => Promise<unknown>) {
  let original: Original | undefined; let revoked = false; let selected: object | undefined; let notify = () => {};
  // Bounded app-owned authority, not an attempted-ID ledger. Action start and acknowledgment
  // invalidate every retained page and every read admitted before that boundary, across remounts.
  let authority: object = {};
  const observeEpoch = (value: unknown, epoch: string, invalidate: () => void) => {
    if (!object(value) || !guid(value.hostEpoch) || typeof value.status !== "string") return false;
    if (value.status === "stale_epoch" || value.hostEpoch !== epoch) { revoked = true; try { invalidate(); } catch { /* Remains revoked. */ } }
    return !revoked;
  };
  return {
    blocked: () => revoked || original !== undefined,
    original: () => original ? { kind: original.kind, status: original.status, sessionId: original.request.handle.sessionId, action: original.action } : undefined,
    observeOriginal() { if (original?.kind === "terminal") original.observed = true; return this.original(); },
    acknowledge() { if (original?.kind !== "terminal" || !original.observed) return false; authority = {}; original = undefined; notify(); return true; },
    forSelection(epoch: string, session: string, signal: AbortSignal, changed: () => void, invalidate: () => void, allowed = () => true) {
      const selection = {}; selected = selection; let page: InputPage | undefined; let reading: Promise<void> | undefined;
      let pageAuthority: object | undefined;
      const current = () => selected === selection && !signal.aborted;
      notify = () => { if (current()) changed(); };
      const act = (action: "resolve" | "cancel", handle: InputHandle, answers: readonly InputAnswer[]): Promise<void> => {
        if (original) return original.waiter; // Synchronous, global original exclusion, including after remount.
        const entry = pageAuthority === authority ? page?.entries.find(e => inputHandle(handle) && same(e.handle, handle)) : undefined;
        if (!current() || revoked || !allowed() || !guid(epoch) || !entry || (action === "resolve" && !answersFor(entry, answers))) return Promise.resolve();
        const request = Object.freeze({ expectedHostEpoch: epoch, handle: copyHandle(entry.handle),
          answers: Object.freeze(answers.map(a => Object.freeze({ promptId: a.promptId, value: a.value }))) });
        // Microtask launch occurs only after the original request and its observing waiter are retained.
        const waiter = Promise.resolve().then(async () => {
          try {
            const value = await (action === "resolve" ? resolve(request) : cancel({ expectedHostEpoch: epoch, handle: request.handle }));
            const validEpoch = observeEpoch(value, epoch, invalidate);
            if (validEpoch && object(value) && typeof value.status === "string" && inputHandle(value.handle) && same(value.handle, request.handle)
              && (value.status === "rejected" || value.status === (action === "resolve" ? "resolved" : "cancelled"))) {
              entryOriginal.kind = "terminal"; entryOriginal.status = value.status;
            } else { entryOriginal.kind = "uncertain"; entryOriginal.status = "uncertain"; }
          } catch { entryOriginal.kind = "uncertain"; entryOriginal.status = "uncertain"; }
          notify();
        });
        const entryOriginal: Original = { request, action, waiter, kind: "pending", status: "pending", observed: false };
        original = entryOriginal; authority = {}; notify(); return waiter;
      };
      return {
        page: () => pageAuthority === authority ? page : undefined,
        refresh(): Promise<void> {
          if (reading) return reading;
          if (!current() || revoked || !allowed() || !guid(epoch) || !inputText(session, 128, true)) return Promise.resolve();
          const readAuthority = authority;
          reading = Promise.resolve().then(async () => {
            try {
              const value = await list({ expectedHostEpoch: epoch, sessionId: session });
              const validEpoch = observeEpoch(value, epoch, invalidate);
              if (!current()) return;
              if (readAuthority !== authority) return;
              pageAuthority = readAuthority;
              page = validEpoch ? inputPage(value, session) : undefined;
            } catch { if (current() && readAuthority === authority) page = undefined; }
            finally { reading = undefined; if (current()) changed(); }
          });
          return reading;
        },
        resolve: (handle: InputHandle, answers: readonly InputAnswer[]) => act("resolve", handle, answers),
        cancel: (handle: InputHandle) => act("cancel", handle, []),
      };
    },
  };
}
