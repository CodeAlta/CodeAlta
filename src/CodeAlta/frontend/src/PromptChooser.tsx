import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { boundedModelChoices } from "./ModelChooser";
import { changeSelection, validSelection } from "./sessionSelection";
import { createPaletteFocusRestoration } from "./paletteActions";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";

// No truncation: a partial catalog must never become selection authority.
export function boundedPromptChoices(choices: SessionChoicesResponse | undefined): choices is SessionChoicesResponse {
  const text = (value: unknown, max: number) => typeof value === "string" && value.length > 0 && value.length <= max;
  return choices?.status === "ok" && !!choices.current && text(choices.current.providerKey, 256)
    && text(choices.current.agentPromptId, 256) && Array.isArray(choices.prompts)
    && choices.prompts.length > 0 && choices.prompts.length <= 128
    && choices.prompts.every(prompt => !!prompt && text(prompt.id, 256) && text(prompt.name, 512))
    && new Set(choices.prompts.map(prompt => prompt.id)).size === choices.prompts.length
    && Array.isArray(choices.models) && (choices.models.length === 0 || boundedModelChoices(choices))
    && validSelection(choices, choices.current);
}

export function promptChoicesSignature(choices: SessionChoicesResponse): string {
  return JSON.stringify([choices.status, choices.epoch, choices.sessionId,
    choices.current && [choices.current.providerKey, choices.current.agentPromptId, choices.current.modelId, choices.current.reasoningEffort],
    choices.prompts.map(value => [value.id, value.name]),
    choices.models.map(value => [value.id, value.name, value.efforts, value.imageInput])]);
}

export type PromptChooserCapture = Readonly<{ choices: SessionChoicesResponse; selection: SessionSelection;
  current: () => boolean; available: () => boolean; apply: (selection: SessionSelection) => boolean }>;

export function PromptChooser({ disabled, capture }: { disabled: boolean; capture: () => PromptChooserCapture | null }) {
  const { t } = useShellLanguage();
  type Review = { source: PromptChooserCapture; origin: HTMLButtonElement; valid: boolean };
  const [review, setReview] = useState<Review | null>(null);
  const active = useRef<Review | null>(null);
  const [draft, setDraft] = useState<SessionSelection | null>(null);
  const [query, setQuery] = useState("");
  const [failed, setFailed] = useState(false);
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const composing = useRef(false);
  const [focus] = useState(createPaletteFocusRestoration);
  const current = (value: Review) => active.current === value && value.valid && value.source.current()
    && !!dialog.current?.open && dialog.current.isConnected && !dialog.current.closest("[inert]");
  function close() {
    const value = active.current;
    active.current = null; setReview(null); setDraft(null);
    dialog.current?.close();
    if (value) focus.schedule(value.origin, () => value.origin.isConnected && !value.origin.disabled
      && !value.origin.closest("[inert]") && value.source.available(),
    () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  useEffect(() => {
    const transition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement) || !active.current) return;
      if (event.target === dialog.current && (event as ToggleEvent).newState === "open" && !dialog.current.open) return;
      active.current.valid = false; setFailed(true);
    };
    document.addEventListener("beforetoggle", transition, true);
    return () => { active.current = null; focus.cancel(); document.removeEventListener("beforetoggle", transition, true); };
  }, [focus]);
  useLayoutEffect(() => {
    if (!review) return;
    const element = dialog.current!;
    element.showModal();
    if (current(review)) search.current?.focus();
    return () => { if (element.open) element.close(); };
  }, [review]);
  useLayoutEffect(() => {
    if (review && !review.source.current()) { review.valid = false; setFailed(true); }
  });
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const prompts = review?.source.choices.prompts.filter(prompt => terms.every(term => `${prompt.id} ${prompt.name}`.toLowerCase().includes(term))) ?? [];
  return <>
    <button id="next-send-prompt-chooser" type="button" disabled={disabled} aria-haspopup="dialog" aria-expanded={!!review}
      aria-label={t("Next Send agent prompt selection")} onClick={event => {
        if (disabled || active.current || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
        const source = capture();
        if (!source || !boundedPromptChoices(source.choices) || !source.current()) return;
        focus.cancel(); composing.current = false;
        const value = { source, origin: event.currentTarget, valid: true };
        active.current = value; setDraft(source.selection); setQuery(""); setFailed(false); setReview(value);
      }} title={t("Search agent prompts")}><AppIcon name="prompt" size={16} /><span className="sr-only">{t("Search agent prompts")}</span></button>
    {review && <dialog ref={dialog} className="app-dialog model-chooser prompt-chooser" aria-modal="true" aria-labelledby="prompt-chooser-title"
      onClose={() => { if (active.current === review) close(); }} onCancel={event => { event.preventDefault(); if (!composing.current) close(); }}
      onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented) return;
        if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || composing.current || event.repeat) {
          if (event.key === "Enter" || event.key === "Escape") event.preventDefault();
          return;
        }
        if (event.key === "Escape") { event.preventDefault(); close(); }
        else if (event.key === "Enter" && !(event.target instanceof HTMLButtonElement)) event.preventDefault();
      }}>
      <header><h2 id="prompt-chooser-title">{t("Next Send agent prompt selection")}</h2><button type="button" onClick={close}>{t("Close")}</button></header>
      <p>{t("Cached session choices only. Apply changes only the next Send; Close discards edits.")}</p>
      <p>{t("Observed prompt (choices snapshot, not live execution)")}: <code>{review.source.choices.current?.agentPromptId}</code></p>
      <p>{t("Local next-Send prompt")}: <code>{review.source.selection.agentPromptId}</code></p>
      <label>{t("Search agent prompts")}<input ref={search} type="search" maxLength={256} value={query} onChange={event => setQuery(event.target.value.slice(0, 256))} /></label>
      <div className="model-chooser-list">
        {prompts.map(value => <button type="button" key={value.id} aria-pressed={draft?.agentPromptId === value.id} onClick={() => {
          if (!draft || !current(review)) { setFailed(true); return; }
          const next = changeSelection(review.source.choices, draft, "agentPromptId", value.id);
          if (next) setDraft(next);
        }}><span>{value.name}</span> <code>{value.id}</code></button>)}
        {!prompts.length && <p>{t("No agent prompts match this search.")}</p>}
      </div>
      <p>{t("Agent prompt")}: <code>{draft?.agentPromptId}</code></p>
      <p>{t("Model")}: <code>{draft?.modelId ?? t("Provider default")}</code> · {t("Reasoning")}: <code>{draft?.reasoningEffort ?? t("Model default")}</code></p>
      {failed && <p role="alert">{t("Next Send selection could not be validated. No change was applied.")}</p>}
      <button type="button" className="prompt-chooser-apply" disabled={failed || !draft} onClick={() => {
        if (!draft || !current(review) || !review.source.apply(draft)) { review.valid = false; setFailed(true); return; }
        close();
      }}>{t("Use prompt for next Send")}</button>
    </dialog>}
  </>;
}
