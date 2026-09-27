import type { PromptCreateRequest, PromptCreateResponse } from "#neoastra";
import type { SkillsCapture } from "./skillsInspection";

type Draft = Pick<PromptCreateRequest, "promptId" | "name" | "description" | "body" | "rootKind" | "mode" | "understoodShadowing">;
type State = Readonly<{ phase: "empty" | "draft" | "review" | "pending" | "created" | "conflict" | "refused" | "uncertain";
  draft: Readonly<Draft>; target: SkillsCapture["target"] | null; original: Readonly<PromptCreateRequest> | null; message: string;
  outcomes: readonly Readonly<{ phase: string; original: Readonly<PromptCreateRequest>; message: string }>[] }>;
const empty = (): Draft => ({ promptId: "", name: "", description: "", body: "", rootKind: "", mode: "", understoodShadowing: false });
const guid = (value: string) => /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const text = (value: string, maximum: number, multiline = false) => value.length <= maximum
  && !(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/u.test(value))
  && (multiline || !/[\r\n\t]/u.test(value))
  && !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value);
const same = (left: SkillsCapture["target"], right: SkillsCapture["target"]) =>
  (Object.keys(left) as Array<keyof typeof left>).every(key => left[key] === right[key]);

// App-owned, never tied to a panel effect or AbortSignal. No transport retry or durable receipt claim.
export function createPromptCreation(invoke: (request: PromptCreateRequest, options: { timeoutMilliseconds: number }) => Promise<PromptCreateResponse>) {
  let state: State = { phase: "empty", draft: Object.freeze(empty()), target: null, original: null, message: "", outcomes: Object.freeze([]) };
  let capture: SkillsCapture | null = null;
  const listeners = new Set<() => void>();
  const publish = (next: State) => { state = Object.freeze(next); for (const listener of [...listeners]) { try { listener(); } catch { /* Views do not own publication. */ } } };
  const current = (value: SkillsCapture) => { try { return value.current() && value.capability.canSubmit({ expectedEpoch: value.target.expectedHostEpoch }); } catch { return false; } };
  return {
    getSnapshot: () => state,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    start(value: SkillsCapture | null) {
      if (state.phase !== "empty" || !value || !current(value)) return;
      publish({ ...state, phase: "draft", target: Object.freeze({ ...value.target }), message: "Draft retained when Settings closes. Discard is explicit." });
    },
    next(value: SkillsCapture | null) {
      if (!["created", "conflict", "refused"].includes(state.phase) || !state.original || state.outcomes.length >= 7 || !value || !current(value)) return;
      const outcomes = Object.freeze([...state.outcomes, Object.freeze({ phase: state.phase, original: state.original, message: state.message })]);
      capture = null;
      publish({ phase: "draft", draft: Object.freeze(empty()), target: Object.freeze({ ...value.target }), original: null, outcomes,
        message: "New blank draft for the explicitly captured selection. Earlier original outcomes remain below; this is not a retry." });
    },
    update(patch: Partial<Draft>) {
      if (state.phase !== "draft" && state.phase !== "review") return;
      capture = null;
      publish({ ...state, phase: "draft", draft: Object.freeze({ ...state.draft, ...patch }) });
    },
    discard() {
      if (state.phase !== "draft" && state.phase !== "review") return;
      capture = null;
      publish({ ...state, phase: "empty", draft: Object.freeze(empty()), target: null, original: null, message: "Unsaved draft discarded." });
    },
    review(value: SkillsCapture | null) {
      if (state.phase !== "draft" || !state.target || !value || !same(state.target, value.target) || !current(value)) return false;
      const d = state.draft;
      if (!/^[a-z][a-z0-9-]{0,63}$/u.test(d.promptId) || /^(?:con|prn|aux|nul|com[0-9]|lpt[0-9])$/u.test(d.promptId)
        || !d.name.trim() || !text(d.name, 128) || !text(d.description, 512) || !d.body.trim() || !text(d.body, 16384, true)
        || !["replace", "append"].includes(d.mode) || !["user_alta", "project_alta"].includes(d.rootKind)
        || d.rootKind === "project_alta" && value.target.scope !== "project" || !d.understoodShadowing) return false;
      capture = value;
      publish({ ...state, phase: "review", message: "Review the original target and complete source below. Creation does not refresh or apply a prompt." });
      return true;
    },
    async confirm() {
      if (state.phase !== "review" || !capture || !state.target) return;
      if (!current(capture)) {
        publish({ ...state, message: "Review capture expired. Nothing submitted. Go back to the retained draft and explicitly review again." }); return;
      }
      const originalCapture = capture;
      const request: PromptCreateRequest = Object.freeze({ ...state.target, ...state.draft, requestId: crypto.randomUUID() });
      publish({ ...state, phase: "pending", original: request, message: "Original creation pending. Closing or navigating does not cancel it." });
      if (!current(originalCapture)) {
        publish({ ...state, phase: "draft", original: null, message: "Capture changed before dispatch. Review again; nothing submitted." }); return;
      }
      try {
        const result = await invoke(request, { timeoutMilliseconds: 30_000 });
        const echo = result?.request;
        if (!echo || !guid(result.hostEpoch) || !["created", "conflict", "refused", "uncertain", "stale_epoch", "closed", "busy"].includes(result.status)
          || (Object.keys(request) as Array<keyof PromptCreateRequest>).some(key => request[key] !== echo[key])) throw new Error("Uncorrelated response");
        // Host identity evidence is not publication proof. Only the original host can settle
        // its operation, except an explicit stale_epoch refusal (rejected before admission).
        // Do not consult current capability/navigation: late original-host success stays true.
        const phase = result.hostEpoch !== request.expectedHostEpoch && result.status !== "stale_epoch" ? "uncertain"
          : result.status === "created" || result.status === "conflict" || result.status === "uncertain" ? result.status : "refused";
        // Still pending during notification: reentrant observers cannot admit a successor.
        // Validated evidence reaches only the original authority, even after host/catalog/navigation ABA.
        try { originalCapture.capability.observe({ status: result.status, epoch: result.hostEpoch }); } catch { /* Outcome cannot be changed by subscriber faults. */ }
        publish({ ...state, phase, message: phase === "created" ? "Created at the original scope. Not proof active or ready. Refresh and application remain separate explicit actions."
          : phase === "conflict" ? "Same-scope destination exists. Nothing overwritten; no renamed fallback. Original request retained."
          : phase === "uncertain" ? "Publication outcome uncertain. Original retained; no retry or discard."
          : `Creation refused (${result.status}). Original request retained.` });
      } catch {
        publish({ ...state, phase: "uncertain", message: "Transport outcome uncertain. Original request retained; no retry, refresh reconciliation or discard." });
      }
    },
  };
}
export type PromptCreation = ReturnType<typeof createPromptCreation>;
