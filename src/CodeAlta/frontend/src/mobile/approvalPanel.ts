import { ApiError, failureText, SignedOutError, type Api } from "./api";
import { approvalKey, approvalMarkKey, approvalQuestion, approvalSubject } from "./approval";
import type { ApprovalRequest, Live } from "./contract";
import { el, icon } from "./dom";

/** How long the choices of a request stay off once it shows: a tap meant for the request before does not answer it. */
export const approvalArmDelay = 400;

/**
 * What the session on screen asks permission for, above its prompt, as on the desktop: read when the session opens and when
 * the live stream says its requests changed, the first one shown with Allow once and Deny. The prompt stays, with its Stop
 * button: a denial does not stop the agent. An answer goes once: one whose outcome is not known is not sent again, and the
 * request read again says what became of it.
 */
export function createApprovalPanel(options: Readonly<{
  document: Document;
  window: Window & typeof globalThis;
  /** The place of the card, `#approval`. */
  root: HTMLElement;
  api: Pick<Api, "permissions" | "resolvePermission">;
  /** The name of the computer, which a failure names. */
  host: string;
  /** The session on screen, or null. */
  current: () => string | null;
  /** The requests of a session changed: the page shows its bottom bar again. */
  onChange: (id: string) => void;
  /** An answer ended: the timeline of the session is read again. */
  onSent: (id: string) => void;
  /** The card changed its height. */
  onResize: () => void;
}>) {
  const { document, root } = options;
  type State = { requests: readonly ApprovalRequest[]; hasMore: boolean; sending: boolean; note: string; armedFor: string | null };
  const states = new Map<string, State>(), reads = new Map<string, number>(), marks = new Map<string, string>();
  const stateOf = (id: string) => states.get(id) ?? (states.set(id, { requests: [], hasMore: false, sending: false, note: "", armedFor: null }), states.get(id)!);
  const changed = (id: string) => { if (options.current() === id) options.onChange(id); };
  let drawn = "", arming: { id: string; timer: number } | null = null;

  /** Reads the requests of a session. */
  async function read(id: string) {
    const count = (reads.get(id) ?? 0) + 1;
    reads.set(id, count);
    let page: Awaited<ReturnType<Api["permissions"]>>;
    try {
      page = await options.api.permissions(id);
    } catch (error) {
      if (reads.get(id) !== count || error instanceof SignedOutError) return;
      // An older CodeAlta has no requests for the phone.
      if (error instanceof ApiError && error.status === 404) page = { requests: [], hasMore: false, error: null };
      else return;
    }
    if (reads.get(id) !== count) return;
    const state = stateOf(id);
    state.requests = page.requests ?? [];
    state.hasMore = !!page.hasMore;
    changed(id);
  }

  async function decide(id: string, request: ApprovalRequest, decision: "allow_once" | "deny") {
    const state = stateOf(id);
    if (state.sending || state.requests[0]?.id !== request.id || state.armedFor !== request.id) return;
    state.sending = true;
    state.note = "";
    changed(id);
    try {
      const result = await options.api.resolvePermission(id, { requestId: request.id, decision });
      if (result.ok) state.requests = state.requests.filter(item => item.id !== request.id);
      else state.note = result.error ?? "The answer was not sent.";
    } catch (error) {
      if (error instanceof SignedOutError) return;
      state.note = `The answer was not sent: ${failureText(error, options.host)}`;
    } finally {
      state.sending = false;
    }
    changed(id);
    if (options.current() === id) options.onSent(id);
    void read(id);
  }

  function draw(id: string, state: State, request: ApprovalRequest) {
    const armed = state.armedFor === request.id;
    const key = approvalKey(request, state.hasMore, state.sending, armed, state.note);
    if (drawn === `${id}\n${key}`) return;
    drawn = `${id}\n${key}`;
    const card = el(document, "div", "approval-card");
    card.dataset.request = request.id;
    const question = el(document, "h3", "approval-question");
    question.append(icon(document, "shield"), el(document, "span", null, approvalQuestion(request)));
    const subject = el(document, "pre", "approval-subject", approvalSubject(request));
    card.append(question, subject);
    if (request.workingDirectory) card.append(el(document, "p", "approval-detail approval-folder", request.workingDirectory));
    if (request.reason?.trim()) card.append(el(document, "p", "approval-detail approval-reason", request.reason));
    if (request.outsideSandbox) card.append(el(document, "p", "approval-warning", "This runs outside the sandbox of the session."));
    if (request.shortened) card.append(el(document, "p", "approval-warning", "This request is too long to show in full: allowing it allows more than what is shown here."));
    if (state.note) {
      const note = el(document, "p", "ask-note", state.note);
      note.setAttribute("role", "alert");
      card.append(note);
    }
    const actions = el(document, "div", "approval-actions");
    for (const [decision, label, className] of [["deny", "Deny", "ghost"], ["allow_once", "Allow once", "primary"]] as const) {
      const button = el(document, "button", className, label);
      button.type = "button";
      button.dataset.decision = decision;
      button.disabled = !armed || state.sending;
      button.addEventListener("click", () => void decide(id, request, decision));
      actions.append(button);
    }
    card.append(actions);
    if (state.hasMore || state.requests.length > 1) card.append(el(document, "p", "approval-detail", "More requests are waiting after this one."));
    root.replaceChildren(card);
    options.onResize();
  }

  return {
    read,
    /** A live update: the requests of the session are read again when they changed. */
    onLive(update: Live) {
      if (update.approving === undefined) return;
      const key = approvalMarkKey(update.approving);
      if (marks.get(update.id) === key) return;
      marks.set(update.id, key);
      if (options.current() === update.id) void read(update.id);
    },
    /** Shows the first request of the session on screen, or nothing: true when one shows. */
    show(): boolean {
      const id = options.current();
      const state = id ? states.get(id) : undefined;
      const request = state?.requests[0];
      const shown = !!id && !!state && !!request;
      if (!shown) {
        if (!root.hidden || root.childElementCount) { root.hidden = true; root.replaceChildren(); drawn = ""; }
        return false;
      }
      root.hidden = false;
      // A new request waits a moment before its choices answer it.
      if (state.armedFor !== request.id && arming?.id !== request.id) {
        if (arming) options.window.clearTimeout(arming.timer);
        const timer = options.window.setTimeout(() => {
          arming = null;
          stateOf(id).armedFor = request.id;
          changed(id);
        }, approvalArmDelay);
        arming = { id: request.id, timer };
      }
      draw(id, state, request);
      return true;
    },
  };
}
