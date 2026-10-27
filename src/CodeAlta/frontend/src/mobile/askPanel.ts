import { ApiError, failureText, SignedOutError, type Api } from "./api";
import { askAnswer, askDraftKey, askMarkKey, askMode, askSettled, newAskDraft, newAskLocal, type AskDraft, type AskLocal } from "./ask";
import { createAskView } from "./askView";
import type { Ask, AskResult, Live } from "./contract";
import { createSheet } from "./sheet";

/**
 * The ask a session waits on, in place of the composer, as the desktop shows it: read when the session opens and when the
 * live stream says its ask changed, answered or cancelled from its card. What is answered is kept per ask while the page
 * lives, until the ask is gone: an answer the session did not take is answered again from the same draft. An answer carries
 * an action id, sent again with it when it did not come back, so that it is never taken twice.
 */
export function createAskPanel(options: Readonly<{
  document: Document;
  window: Window & typeof globalThis;
  /** The bar of the ask, `#ask`, and the sheet that confirms a cancel, `#ask-cancel-sheet`. */
  root: HTMLElement;
  cancelSheet: HTMLElement;
  api: Pick<Api, "ask" | "answerAsk" | "cancelAsk">;
  /** The name of the computer, which a failure names. */
  host: string;
  /** The session on screen, or null. */
  current: () => string | null;
  /** What the live stream says of a session now. */
  live: (id: string) => Live | null | undefined;
  /** Fills an element with Markdown, as the timeline does. */
  markdown: (element: HTMLElement, text: string) => void;
  /** The ask of a session changed: the page shows its bottom bar again. */
  onChange: (id: string) => void;
  /** An answer or a cancel ended: the timeline of the session is read again. */
  onSent: (id: string) => void;
  /** The card changed its height. */
  onResize: () => void;
}>) {
  const { document, root } = options;
  const asks = new Map<string, Ask | null>(), drafts = new Map<string, AskDraft>(), locals = new Map<string, AskLocal>();
  const reads = new Map<string, number>(), marks = new Map<string, string>();
  const cancelSheet = createSheet(document, options.cancelSheet);
  const view = createAskView({
    document, root,
    markdown: options.markdown,
    onSubmit: () => void answer(),
    onCancel: () => cancelSheet.open(document.getElementById("ask-cancel-keep")),
    onResize: options.onResize,
  });
  document.getElementById("ask-cancel-keep")?.addEventListener("click", () => cancelSheet.close());
  document.getElementById("ask-cancel-exit")?.addEventListener("click", () => { cancelSheet.close(); void cancel(); });

  const localOf = (id: string) => locals.get(id) ?? (locals.set(id, newAskLocal()), locals.get(id)!);
  function draftOf(id: string, ask: Ask): AskDraft {
    const key = askDraftKey(id, ask);
    const draft = drafts.get(key) ?? newAskDraft();
    if (draft.index >= ask.questions.length) { draft.index = 0; draft.seen.add(0); }
    drafts.set(key, draft);
    return draft;
  }
  const changed = (id: string) => { if (options.current() === id) options.onChange(id); };

  /** Reads the ask of a session; an answer this phone sent ends with it, or goes again from its draft. */
  async function read(id: string) {
    const count = (reads.get(id) ?? 0) + 1;
    reads.set(id, count);
    let ask: Ask | null;
    try {
      ask = (await options.api.ask(id)).ask ?? null;
    } catch (error) {
      if (reads.get(id) !== count || error instanceof SignedOutError) return;
      // An older CodeAlta has no asks: the composer stays.
      if (error instanceof ApiError && error.status === 404) ask = null; else return;
    }
    if (reads.get(id) !== count) return;
    asks.set(id, ask);
    const local = localOf(id);
    if (local.submitted && !local.sending) {
      const settled = askSettled(ask, local.submitted);
      if (settled === "done") { drafts.delete(askDraftKey(id, { id: local.submitted.askId })); local.submitted = null; local.note = ""; }
      else if (settled === "again") {
        // The session did not take the answer: the ask waits in a new revision, which a new action answers.
        if (ask) { const draft = drafts.get(askDraftKey(id, ask)); if (draft) draft.actionId = null; }
        local.submitted = null;
        local.note = "The session did not take the answer. Try again.";
      } else if (settled === "open") local.submitted = null;
    }
    changed(id);
  }

  async function answer() {
    const id = options.current(), ask = id ? asks.get(id) : null;
    if (!id || !ask) return;
    const draft = draftOf(id, ask), local = localOf(id);
    if (local.sending) return;
    draft.actionId ??= options.window.crypto.randomUUID();
    await send(id, ask, local, () => options.api.answerAsk(id, askAnswer(ask, draft, draft.actionId!)), () => { draft.actionId = null; }, "The answer was not sent");
  }

  async function cancel() {
    const id = options.current(), ask = id ? asks.get(id) : null;
    if (!id || !ask) return;
    const local = localOf(id);
    if (local.sending) return;
    const actionId = options.window.crypto.randomUUID();
    await send(id, ask, local, () => options.api.cancelAsk(id, { actionId, askId: ask.id, revision: ask.revision }), () => {}, "The ask was not canceled");
  }

  // An answer that reached the session ends the ask and its draft. One the computer still sends keeps both, read-only, until
  // the ask is gone or waits again. One refused keeps the draft, says why, and goes again with a new id.
  async function send(id: string, ask: Ask, local: AskLocal, request: () => Promise<AskResult>, refused: () => void, failed: string) {
    local.sending = true;
    local.note = "";
    changed(id);
    try {
      const result = await request();
      if (result.ok && result.pending) local.submitted = { askId: ask.id, revision: ask.revision };
      else if (result.ok) {
        drafts.delete(askDraftKey(id, ask));
        local.submitted = null;
        if (asks.get(id)?.id === ask.id) asks.set(id, null);
      } else {
        refused();
        local.note = result.error ?? `${failed}.`;
      }
    } catch (error) {
      if (error instanceof SignedOutError) return;
      // The answer may have gone: its id is kept, and the same answer sent again is not taken twice.
      local.note = `${failed}: ${failureText(error, options.host)}`;
    } finally {
      local.sending = false;
    }
    changed(id);
    if (options.current() === id) options.onSent(id);
    void read(id);
  }

  return {
    read,
    /** A live update: the ask of the session is read again when it came, went, or changed its revision or its state. */
    onLive(update: Live) {
      if (update.asking === undefined) return;
      const key = askMarkKey(update.asking);
      if (marks.get(update.id) === key) return;
      marks.set(update.id, key);
      if (options.current() === update.id) void read(update.id);
    },
    /** Shows the ask of the session on screen, or nothing: true when it takes the place of the composer. */
    show(): boolean {
      const id = options.current();
      const ask = id ? asks.get(id) : null;
      const local = id ? locals.get(id) : undefined;
      const mode = id ? askMode(ask, options.live(id), local) : null;
      const shown = !!mode && !!ask && !!id;
      root.hidden = !shown;
      view.show(shown ? ask : null, shown ? draftOf(id, ask) : null, local ?? newAskLocal(), mode ?? "form");
      return shown;
    },
  };
}
