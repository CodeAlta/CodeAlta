export type AskHandle = Readonly<{ operationId: string; runtimeInstanceId: string; attachmentGeneration: string;
  providerId: string; sessionId: string; runId: string; askId: string; responseGeneration: string }>;
export type AskAnswer = Readonly<{ questionIndex: number; selectedChoiceIndexes: readonly number[]; freeformText: string | null }>;
export type AskActionRequest = Readonly<{ expectedHostEpoch: string; action: Readonly<{ actionId: string; handle: AskHandle; answers: readonly AskAnswer[] }> }>;
export type AskDisposition = Readonly<{ actionId: string; handle: AskHandle; status: string; runId: string | null }>;
export type AskQuestion = Readonly<{ title: string; question: string; description: string | null;
  choices: readonly Readonly<{ title: string; description: string | null }>[];
  freeform: Readonly<{ title: string | null; placeholder: string | null }> | null }>;
export type AskPage = Readonly<{ head: Readonly<{ handle: AskHandle; request: Readonly<{ questions: readonly AskQuestion[] }>; state: string }> | null;
  latest: AskDisposition | null; hasMore: boolean }>;
type Rpc = (request: AskActionRequest) => Promise<unknown>;
type Observation = { waiter?: Promise<unknown>; work?: Promise<void>; finished: boolean };
type Entry = { request: AskActionRequest; kind: "answer" | "cancel"; transport: "pending" | "settled" | "uncertain";
  result: AskDisposition | null; observed: AskDisposition | null; waiter?: Promise<unknown>; work?: Promise<void>;
  observation?: Observation; invalidateCapability: () => void };

function object(value: unknown): value is Record<string, unknown> { return !!value && typeof value === "object" && !Array.isArray(value); }
function text(value: unknown, maximum: number): value is string {
  if (typeof value !== "string" || value.length > maximum || value.includes("\0")) return false;
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    if (c < 0xd800 || c > 0xdfff) continue;
    if (c > 0xdbff || ++i === value.length) return false;
    const low = value.charCodeAt(i); if (low < 0xdc00 || low > 0xdfff) return false;
  }
  return true;
}
function guid(value: unknown): value is string {
  return typeof value === "string" && value.length === 36 && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
// .NET Char.IsWhiteSpace / String.Trim, not ECMAScript trim (notably U+0085 and U+FEFF).
function dotNetWhitespace(code: number): boolean {
  return (code >= 0x09 && code <= 0x0d) || code === 0x20 || code === 0x85 || code === 0xa0 || code === 0x1680
    || (code >= 0x2000 && code <= 0x200a) || code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000;
}
function dotNetTrim(value: string): string {
  let start = 0; let end = value.length;
  while (start < end && dotNetWhitespace(value.charCodeAt(start))) start++;
  while (end > start && dotNetWhitespace(value.charCodeAt(end - 1))) end--;
  return value.slice(start, end);
}
function identity(value: unknown): value is string { return text(value, 256) && dotNetTrim(value) === value && value.length > 0 && !/[\u0000-\u001f\u007f-\u009f]/u.test(value); }
function generation(value: unknown, maximum: string, allowZero: boolean): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= maximum.length && !/[^0-9]/u.test(value)
    && (value.length === 1 || value[0] !== "0") && (allowZero || value !== "0")
    && (value.length < maximum.length || value <= maximum);
}
export function validAskHandle(value: unknown): value is AskHandle {
  return object(value) && guid(value.operationId) && guid(value.runtimeInstanceId) && guid(value.askId)
    && generation(value.attachmentGeneration, "9007199254740991", false)
    && generation(value.responseGeneration, "256", true)
    && identity(value.providerId) && identity(value.sessionId) && identity(value.runId);
}
function same(a: AskHandle, b: AskHandle): boolean {
  return a.operationId === b.operationId && a.runtimeInstanceId === b.runtimeInstanceId && a.attachmentGeneration === b.attachmentGeneration
    && a.providerId === b.providerId && a.sessionId === b.sessionId && a.runId === b.runId && a.askId === b.askId && a.responseGeneration === b.responseGeneration;
}
// Projection, not spread: validation of declared fields does not validate unknown nested wire data.
export function askWireHandle(handle: AskHandle) {
  return { operationId: handle.operationId, runtimeInstanceId: handle.runtimeInstanceId, attachmentGeneration: handle.attachmentGeneration,
    providerId: handle.providerId, sessionId: handle.sessionId, runId: handle.runId, askId: handle.askId, responseGeneration: handle.responseGeneration };
}
function disposition(value: unknown): AskDisposition | null {
  if (!object(value) || !guid(value.actionId) || !validAskHandle(value.handle) || typeof value.status !== "string"
    || !["submitting", "admitted", "not_admitted", "indeterminate", "cancelled", "rejected", "conflict", "capacity", "closed", "disabled"].includes(value.status)
    || (value.runId !== null && !identity(value.runId)) || (value.status === "admitted" && !identity(value.runId))) return null;
  return Object.freeze({ actionId: value.actionId, handle: Object.freeze(askWireHandle(value.handle)), status: value.status, runId: value.runId as string | null });
}

export function captureAskAction(epoch: string, handle: AskHandle,
  answers: readonly { questionIndex: number; selectedChoiceIndexes?: readonly number[]; freeformText?: string | null }[], actionId: string): AskActionRequest {
  if (!guid(epoch) || !guid(actionId) || !validAskHandle(handle) || answers.length > 12) throw new Error("Invalid ask action");
  let budget = 8192;
  const seen = new Set<number>();
  const copied = answers.map(answer => {
    const choices = answer.selectedChoiceIndexes ?? [];
    if (!Number.isInteger(answer.questionIndex) || answer.questionIndex < 0 || answer.questionIndex >= 12 || seen.has(answer.questionIndex)
      || choices.length > 20 || choices.some(v => !Number.isInteger(v) || v < 0 || v >= 20) || new Set(choices).size !== choices.length)
      throw new Error("Invalid answer");
    seen.add(answer.questionIndex);
    const freeformText = answer.freeformText ?? null;
    if (freeformText !== null && (!text(freeformText, 8192) || (budget -= freeformText.length) < 0)) throw new Error("Answer too large");
    return Object.freeze({ questionIndex: answer.questionIndex, selectedChoiceIndexes: Object.freeze([...choices]), freeformText });
  });
  return Object.freeze({ expectedHostEpoch: epoch, action: Object.freeze({ actionId, handle: Object.freeze(askWireHandle(handle)), answers: Object.freeze(copied) }) });
}

// Mutable wire copy only at the generated-client boundary; the retained original is never replaced.
export function askWireRequest(request: AskActionRequest) {
  return { expectedHostEpoch: request.expectedHostEpoch, action: { actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
    answers: request.action.answers.map(a => ({ questionIndex: a.questionIndex, freeformText: a.freeformText, selectedChoiceIndexes: [...a.selectedChoiceIndexes] })) } };
}

export function parseAskPage(value: unknown, epoch: string, session: string): AskPage {
  if (!guid(epoch) || !identity(session) || !object(value) || value.status !== "ok" || value.hostEpoch !== epoch || value.sessionId !== session || typeof value.hasMore !== "boolean") throw new Error("Invalid ask page");
  let head: AskPage["head"] = null;
  if (value.head !== null) {
    const h = value.head;
    if (!object(h) || !validAskHandle(h.handle) || h.handle.sessionId !== session || !["pending", "submitting", "indeterminate"].includes(h.state as string)
      || !object(h.request) || h.request.file != null || !Array.isArray(h.request.questions) || h.request.questions.length < 1 || h.request.questions.length > 12) throw new Error("Invalid pending ask");
    let budget = 8192;
    const field = (v: unknown, max: number, required = false): string | null => {
      if (v === null && !required) return null;
      if (!text(v, max) || (required && !dotNetTrim(v)) || (budget -= v.length) < 0) throw new Error("Invalid ask text");
      return v;
    };
    const questions = h.request.questions.map(q => {
      if (!object(q) || !Array.isArray(q.choices) || q.choices.length > 20) throw new Error("Invalid choices");
      const title = field(q.title, 120, true)!; const question = field(q.question, 4000, true)!;
      const description = field(q.description, 4000);
      const choices = q.choices.map(c => {
        if (!object(c)) throw new Error("Invalid choice");
        return Object.freeze({ title: field(c.title, 120, true)!, description: field(c.description, 4000) });
      });
      let freeform: AskQuestion["freeform"] = null;
      if (q.freeform !== null) {
        if (!object(q.freeform)) throw new Error("Invalid freeform");
        freeform = Object.freeze({ title: field(q.freeform.title, 120), placeholder: field(q.freeform.placeholder, 500) });
      }
      if (!choices.length && !freeform) throw new Error("No answer route");
      return Object.freeze({ title, question, description, choices: Object.freeze(choices), freeform });
    });
    head = Object.freeze({ handle: Object.freeze(askWireHandle(h.handle)), request: Object.freeze({ questions: Object.freeze(questions) }), state: h.state as string });
  }
  const latest = value.latest === null ? null : disposition(value.latest);
  if (value.latest !== null && (!latest || latest.handle.sessionId !== session)) throw new Error("Invalid disposition");
  return Object.freeze({ head, latest, hasMore: value.hasMore });
}

export function createAskActions(answer: Rpc, cancel: Rpc) {
  const entries = new Map<string, Entry>();
  const observations: Observation[] = []; // Bounded original explicit reads, not an automatic polling loop.
  const listeners = new Set<() => void>();
  let invalid = false;
  const publish = () => { for (const listener of listeners) { try { listener(); } catch { /* Presentation cannot change retained evidence. */ } } };
  const view = (entry: Entry) => Object.freeze({ request: entry.request, kind: entry.kind, transport: entry.transport, result: entry.result, observed: entry.observed });
  const blocked = (handle: AskHandle) => invalid || [...entries.values()].some(e => e.request.action.handle.askId === handle.askId
    && (e.transport !== "settled" || !e.observed || e.result?.status === "indeterminate" || same(e.request.action.handle, handle)));
  const invalidate = (invalidateCapability: () => void = () => {}) => {
    invalid = true;
    for (const entry of entries.values()) if (entry.transport === "pending") entry.transport = "uncertain";
    // Revoke the captured app-owned capabilities even if their panels are obsolete or unmounted.
    for (const entry of entries.values()) { try { entry.invalidateCapability(); } catch { /* Local revocation remains permanent. */ } }
    try { invalidateCapability(); } catch { /* Local revocation remains permanent. */ }
    publish();
  };
  const observeEpoch = (value: unknown, epoch: string, invalidateCapability: () => void): boolean => {
    if (!object(value) || !guid(value.hostEpoch) || !text(value.status, 64)) return false;
    if (value.status === "stale_epoch" || value.status === "stale_runtime" || value.hostEpoch !== epoch) invalidate(invalidateCapability);
    return !invalid && value.hostEpoch === epoch && value.status === "ok";
  };
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    get(id: string) { const value = entries.get(id); return value ? view(value) : undefined; },
    forSession(session: string) { return [...entries.values()].filter(e => e.request.action.handle.sessionId === session).map(view); },
    blocked(handle: AskHandle) { return blocked(handle); },
    invalidate(_epoch: string) { invalidate(); },
    readPage(value: unknown, epoch: string, session: string, isCurrent: () => boolean, invalidateCapability: () => void): AskPage | undefined {
      const currentEpoch = observeEpoch(value, epoch, invalidateCapability);
      if (!isCurrent()) return undefined;
      if (!currentEpoch) throw new Error("Invalid ask page identity");
      return parseAskPage(value, epoch, session);
    },
    submit(kind: "answer" | "cancel", request: AskActionRequest, allowed: () => boolean, invalidateCapability: () => void = () => {}): Promise<void> {
      const existing = entries.get(request.action.actionId);
      if (existing) return existing.work ?? Promise.resolve(); // Never dispatch again, even after timeout.
      if (!allowed() || blocked(request.action.handle) || entries.size >= 256) return Promise.resolve();
      const entry: Entry = { request, kind, transport: "pending", result: null, observed: null, invalidateCapability };
      entries.set(request.action.actionId, entry);
      let release!: () => void;
      const launch = new Promise<void>(resolve => { release = resolve; });
      entry.work = (async () => {
        await launch;
        if (!allowed() || invalid) { entry.transport = "uncertain"; publish(); return; }
        const timer = setTimeout(() => { if (entry.transport === "pending") { entry.transport = "uncertain"; publish(); } }, 8000);
        try {
          entry.waiter = (kind === "answer" ? answer : cancel)(request);
          const value = await entry.waiter;
          if (!observeEpoch(value, request.expectedHostEpoch, invalidateCapability) || !object(value)) {
            entry.transport = "uncertain";
          } else {
            const result = disposition(value.disposition);
            if (!result || result.actionId !== request.action.actionId || !same(result.handle, request.action.handle)) entry.transport = "uncertain";
            else if (entry.transport === "pending" && !invalid) { entry.result = result; entry.transport = "settled"; }
          }
        } catch { entry.transport = "uncertain"; }
        finally { clearTimeout(timer); publish(); }
      })();
      release(); publish(); return entry.work;
    },
    observe(id: string, value: unknown) {
      const entry = entries.get(id); const observed = disposition(value);
      if (entry && observed && observed.actionId === id && same(observed.handle, entry.request.action.handle)) { entry.observed = observed; publish(); }
    },
    observeRemote(id: string, rpc: (request: AskActionRequest) => Promise<unknown>, invalidateCapability: () => void = () => {}): Promise<void> {
      const entry = entries.get(id);
      if (!entry) return Promise.resolve();
      if (entry.observation && !entry.observation.finished) return entry.observation.work!;
      if (observations.length >= 256) return Promise.resolve();
      const observation: Observation = { finished: false };
      observations.push(observation);
      entry.observation = observation;
      let release!: () => void;
      const launch = new Promise<void>(resolve => { release = resolve; });
      observation.work = (async () => {
        await launch;
        try {
          observation.waiter = rpc(entry.request);
          const value = await observation.waiter;
          // Epoch evidence is processed even when the original interaction is already uncertain.
          observeEpoch(value, entry.request.expectedHostEpoch, invalidateCapability);
          if (!object(value) || !guid(value.hostEpoch) || value.hostEpoch !== entry.request.expectedHostEpoch || value.status !== "ok") return;
          const result = disposition(value.disposition);
          if (result && result.actionId === id && same(result.handle, entry.request.action.handle)) entry.observed = result;
        } catch { /* An unsuccessful observation proves nothing about the retained action. */ }
        finally { observation.finished = true; publish(); }
      })();
      release(); return observation.work;
    },
  };
}
