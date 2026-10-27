import { questionTabTitle } from "../askReview";
import {
  askBadge, askChoice, askCommentsText, askGo, askLimits, askPrimaryLabel, askReviewWhat, askReviewWhere, askSendable, askStep, askTextRoom, askVerdict, askVerdicts,
  type AskDraft,
} from "./ask";
import type { Ask } from "./contract";
import { el, icon, iconSvg } from "./dom";

/** What the card of an ask shows besides the ask: an answer on its way, and why the last one did not go. */
export type AskViewState = Readonly<{ sending: boolean; note: string }>;

/** What the card is: the questions to answer, or a line read-only while an answer goes or when its outcome is unknown. */
export type AskViewMode = "form" | "sending" | "unknown";

/** What the read-only card says. */
export const askNotices = {
  sending: "Sending your answer to the session…",
  unknown: "The answer was sent, but CodeAlta cannot tell whether the session got it. Check the session on the computer.",
} as const;

/** What the card says when the text answers reach their limit. */
export const askTextLimitHint = `The answers hold ${askLimits.answerText} characters at most, all the questions together.`;

/**
 * The card of an ask in place of the composer, as the desktop draws it: the badge, the steps of several questions, the
 * question with its choices as numbered cards and its text field, the verdict of a review, and Cancel, Back and Next or
 * Submit. It is drawn again when another question is shown; a choice or a keystroke only updates it.
 */
export function createAskView(options: Readonly<{
  document: Document;
  root: HTMLElement;
  /** Fills an element with Markdown, as the timeline does. */
  markdown: (element: HTMLElement, text: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  /** The card changed its height. */
  onResize: () => void;
}>) {
  const { document, root } = options;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  let shown: { ask: Ask; draft: AskDraft; state: AskViewState; mode: AskViewMode } | null = null;
  let drawn = "";

  // An answer on its way, or whose outcome is unknown: the badge and one line, nothing to change.
  function drawNotice(ask: Ask, mode: Exclude<AskViewMode, "form">) {
    const card = make("div", "ask-card ask-notice");
    card.dataset.mode = mode;
    card.setAttribute("role", "status");
    card.setAttribute("aria-live", "polite");
    const badge = make("span", "ask-badge");
    badge.append(icon(document, ask.review ? "chat" : "ask"), document.createTextNode(askBadge(ask)));
    const line = make("p", "ask-notice-text");
    if (mode === "sending") line.append(make("span", "ask-notice-spinner"));
    else line.append(icon(document, "computer"));
    line.append(make("span", null, askNotices[mode]));
    card.append(badge, line);
    root.replaceChildren(card, ...Array.from(root.children).filter(child => child.id === "foot"));
    drawn = key();
    options.onResize();
  }

  function draw() {
    if (!shown) return;
    const { ask, draft, mode } = shown;
    if (mode !== "form") { drawNotice(ask, mode); return; }
    const count = ask.questions.length, question = ask.questions[draft.index], review = ask.review;
    const form = make("fieldset", "ask-card ask-form");
    form.dataset.review = review?.kind ?? "";
    form.append(make("legend", "sr-only", askBadge(ask)));

    const head = make("header", "ask-head");
    const badge = make("span", "ask-badge");
    badge.append(icon(document, review ? "chat" : "ask"), document.createTextNode(askBadge(ask)));
    if (review) {
      const what = make("span", "ask-review-what", askReviewWhat(review));
      if (review.path) what.title = review.path;
      badge.append(what);
    }
    head.append(badge);
    if (count > 1) {
      const steps = make("ol", "ask-steps");
      steps.setAttribute("aria-label", "Questions");
      ask.questions.forEach((item, index) => {
        const button = make("button");
        button.type = "button";
        button.dataset.step = String(index);
        button.setAttribute("aria-label", questionTabTitle(item.title, index, count));
        button.title = item.title;
        button.append(make("span", "ask-step-mark"), make("span", "ask-step-title", item.title));
        const entry = make("li");
        entry.append(button);
        steps.append(entry);
      });
      head.append(steps);
    }
    form.append(head);

    if (review?.message) {
      const message = make("div", "ask-review-message markdown-content");
      options.markdown(message, review.message);
      form.append(message);
    }
    if (review) {
      const where = make("p", "ask-review-where");
      where.append(icon(document, "computer"), make("span", null, askReviewWhere(review)));
      form.append(where);
    }

    if (question) {
      const part = make("div", "ask-question");
      part.dataset.askQuestion = String(draft.index);
      part.setAttribute("role", "group");
      part.setAttribute("aria-label", question.title);
      part.append(make("h4", null, question.question));
      if (question.description) part.append(make("p", "detail", question.description));
      if (question.choices.length) {
        const choices = make("div", "ask-choices");
        choices.setAttribute("role", "radiogroup");
        choices.setAttribute("aria-label", question.title);
        question.choices.forEach((choice, index) => {
          const label = make("label", "ask-choice");
          const input = make("input", "sr-only");
          input.type = "radio";
          input.name = `ask-choice-${draft.index}`;
          input.value = String(index);
          input.dataset.choice = String(index);
          const text = make("span", "ask-choice-text");
          text.append(make("strong", null, choice.title));
          if (choice.description) text.append(make("small", null, choice.description));
          const number = make("span", "ask-choice-number", index + 1);
          number.setAttribute("aria-hidden", "true");
          label.append(input, number, text, icon(document, "check", "ask-choice-check"));
          choices.append(label);
        });
        part.append(choices);
      }
      if (question.freeform) {
        const label = make("label", "ask-freeform");
        const title = question.freeform.title ?? "Answer";
        const box = make("textarea");
        box.rows = 2;
        box.maxLength = askTextRoom(ask, draft, draft.index);
        box.dataset.field = "text";
        box.setAttribute("aria-label", title);
        box.placeholder = question.freeform.placeholder ?? "";
        box.value = draft.text.get(draft.index) ?? "";
        label.append(make("span", null, title), box, make("small", "ask-hint ask-text-hint", askTextLimitHint));
        part.append(label);
      }
      form.append(part);
    }

    if (review) {
      const verdict = make("div", "ask-review-verdict");
      verdict.setAttribute("role", "group");
      verdict.setAttribute("aria-label", "Your review");
      verdict.append(make("span", "ask-review-counts", askCommentsText(review.comments)));
      const group = make("div", "review-verdicts");
      group.setAttribute("role", "radiogroup");
      group.setAttribute("aria-label", "Verdict");
      for (const item of askVerdicts) {
        const label = make("label", "review-verdict");
        label.dataset.verdict = item.value;
        const input = make("input", "sr-only");
        input.type = "radio";
        input.name = "ask-verdict";
        input.value = item.value;
        input.dataset.verdict = item.value;
        label.append(input, make("strong", null, item.label), make("small", null, item.detail));
        group.append(label);
      }
      const overall = make("label", "ask-freeform");
      const box = make("textarea");
      box.rows = 2;
      box.maxLength = askLimits.overallComment;
      box.dataset.field = "overall";
      box.setAttribute("aria-label", "Overall comment (optional)");
      box.value = draft.overall;
      overall.append(make("span", null, "Overall comment (optional)"), box);
      verdict.append(group, overall, make("small", "ask-hint ask-review-hint", "Write a comment or an overall comment to send a comment-only review."));
      form.append(verdict);
    }

    const note = make("p", "ask-note");
    note.setAttribute("role", "status");
    note.setAttribute("aria-live", "polite");
    const actions = make("footer", "ask-actions");
    const cancel = make("button", "ghost ask-cancel", "Cancel");
    cancel.type = "button";
    actions.append(cancel);
    if (count > 1) {
      const back = make("button", "ghost ask-back");
      back.type = "button";
      back.innerHTML = iconSvg("back");
      back.append(make("span", null, "Back"));
      actions.append(back);
    }
    const primary = make("button", "primary ask-answer");
    primary.type = "button";
    actions.append(primary);
    form.append(note, actions);
    root.replaceChildren(form, ...Array.from(root.children).filter(child => child.id === "foot"));
    drawn = key();
    sync();
  }

  // What changes without drawing the card again: the marks of the steps, the choice and the verdict, the main button, the note.
  function sync() {
    if (!shown) return;
    const { ask, draft, state } = shown;
    const form = root.querySelector<HTMLFieldSetElement>("fieldset.ask-form");
    if (!form) return;
    form.disabled = state.sending;
    form.setAttribute("aria-busy", String(state.sending));
    for (const button of Array.from(form.querySelectorAll<HTMLButtonElement>("[data-step]"))) {
      const index = Number(button.dataset.step);
      const current = index === draft.index, seen = draft.seen.has(index);
      button.dataset.state = current ? "current" : seen ? "seen" : "ahead";
      button.setAttribute("aria-current", String(current));
      const mark = button.querySelector(".ask-step-mark")!;
      if (!current && seen) mark.replaceChildren(icon(document, "check")); else mark.textContent = String(index + 1);
    }
    const choice = askChoice(ask, draft, draft.index);
    for (const input of Array.from(form.querySelectorAll<HTMLInputElement>("input[data-choice]"))) {
      const on = Number(input.dataset.choice) === choice;
      input.checked = on;
      input.closest("label")!.toggleAttribute("data-selected", on);
    }
    const verdict = askVerdict(ask, draft);
    for (const input of Array.from(form.querySelectorAll<HTMLInputElement>("input[data-verdict]"))) {
      const on = input.dataset.verdict === verdict;
      input.checked = on;
      input.closest("label")!.toggleAttribute("data-selected", on);
    }
    const sendable = askSendable(ask, draft), step = askStep(ask, draft);
    const hint = form.querySelector<HTMLElement>(".ask-review-hint");
    if (hint) hint.hidden = sendable || !ask.review;
    // The text answers share one limit: the box of a question has what the others left.
    const text = form.querySelector<HTMLTextAreaElement>("textarea[data-field='text']");
    if (text) {
      const room = askTextRoom(ask, draft, draft.index);
      text.maxLength = room;
      form.querySelector<HTMLElement>(".ask-text-hint")!.hidden = text.value.length < room;
    }
    const back = form.querySelector<HTMLButtonElement>(".ask-back");
    if (back) back.disabled = draft.index === 0;
    const primary = form.querySelector<HTMLButtonElement>(".ask-answer")!;
    const label = state.sending ? "Sending…" : askPrimaryLabel(ask, draft);
    if (primary.textContent !== label) {
      primary.replaceChildren(make("span", null, label));
      primary.insertAdjacentHTML("beforeend", iconSvg(step.kind === "show" ? "chevron" : "send"));
    }
    primary.disabled = step.kind === "submit" && !sendable;
    form.querySelector(".ask-note")!.textContent = state.note;
    options.onResize();
  }

  const key = () => shown ? `${shown.mode}\n${shown.ask.id}\n${shown.ask.revision}\n${shown.draft.index}\n${shown.ask.review?.comments ?? ""}` : "";

  // Another question: it is drawn, and the choice or the field it has takes the focus, as the tab it replaces had it.
  function go(index: number) {
    if (!shown || shown.state.sending || !askGo(shown.ask, shown.draft, index)) return;
    draw();
    const part = root.querySelector<HTMLElement>(".ask-question");
    (part?.querySelector<HTMLElement>("input:checked") ?? part?.querySelector<HTMLElement>("textarea") ?? part?.querySelector<HTMLElement>("input"))?.focus();
  }

  root.addEventListener("change", event => {
    const target = event.target as HTMLInputElement;
    if (!shown || target.type !== "radio") return;
    if (target.dataset.choice !== undefined) shown.draft.choices.set(shown.draft.index, Number(target.dataset.choice));
    else if (target.dataset.verdict) shown.draft.verdict = target.dataset.verdict as AskDraft["verdict"];
    sync();
  });
  root.addEventListener("input", event => {
    const target = event.target as HTMLTextAreaElement;
    if (!shown || !(target instanceof HTMLTextAreaElement)) return;
    if (target.dataset.field === "text") shown.draft.text.set(shown.draft.index, target.value);
    else if (target.dataset.field === "overall") shown.draft.overall = target.value;
    sync();
  });
  root.addEventListener("click", event => {
    const button = (event.target as Element | null)?.closest<HTMLButtonElement>("button");
    if (!shown || !button || button.disabled || !root.contains(button)) return;
    if (button.dataset.step !== undefined) go(Number(button.dataset.step));
    else if (button.classList.contains("ask-back")) go(shown.draft.index - 1);
    else if (button.classList.contains("ask-cancel")) options.onCancel();
    else if (button.classList.contains("ask-answer")) {
      const step = askStep(shown.ask, shown.draft);
      if (step.kind === "show") go(step.index); else options.onSubmit();
    }
  });

  return {
    /** Shows an ask with its draft, as a form or a read-only card, or nothing. */
    show(ask: Ask | null, draft: AskDraft | null, state: AskViewState, mode: AskViewMode = "form") {
      if (!ask || !draft) { shown = null; drawn = ""; root.querySelector(".ask-card")?.remove(); return; }
      shown = { ask, draft, state, mode };
      if (key() !== drawn || !root.querySelector(".ask-card")) draw(); else if (mode === "form") sync();
    },
  };
}
