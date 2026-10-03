import { AppWindowSurface } from "./AppWindow";
import { HTMLSelect } from "@blueprintjs/core";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { changeSelection } from "./sessionSelection";
import { createPaletteFocusRestoration } from "./paletteActions";
import { useShellLanguage } from "./shellLanguage";
import { AppIcon } from "./AppIcon";

// Refuse oversized/malformed observations rather than silently offering a partial authority.
export function boundedModelChoices(choices: SessionChoicesResponse | undefined): choices is SessionChoicesResponse {
  const text = (value: unknown, max: number) => typeof value === "string" && value.length > 0 && value.length <= max;
  return choices?.status === "ok" && !!choices.current && Array.isArray(choices.models)
    && choices.models.length > 0 && choices.models.length <= 128
    && choices.models.every(model => !!model && text(model.id, 256) && text(model.name, 512)
      && Array.isArray(model.efforts) && model.efforts.length <= 32 && model.efforts.every((effort: unknown) => text(effort, 128))
      && (model.imageInput == null || typeof model.imageInput === "boolean"))
    && new Set(choices.models.map(model => model.id)).size === choices.models.length;
}

export type ModelChooserCapture = Readonly<{ choices: SessionChoicesResponse; selection: SessionSelection;
  current: () => boolean; available: () => boolean; apply: (selection: SessionSelection) => boolean }>;

export function ModelChooser({ disabled, capture }: { disabled: boolean; capture: () => ModelChooserCapture | null }) {
  const { t } = useShellLanguage();
  type Review = { source: ModelChooserCapture; origin: HTMLButtonElement; valid: boolean };
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
    // Native opening runs its own focus steps after React's mount autofocus.
    if (current(review)) search.current?.focus();
    return () => { if (element.open) element.close(); };
  }, [review]);
  useLayoutEffect(() => {
    if (review && !review.source.current()) { review.valid = false; setFailed(true); }
  });
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const models = review?.source.choices.models.filter(model => terms.every(term => `${model.id} ${model.name}`.toLowerCase().includes(term))) ?? [];
  const model = review?.source.choices.models.find(value => value.id === draft?.modelId);
  function edit(field: "modelId" | "reasoningEffort", value: string) {
    if (!review || !draft || !current(review)) { setFailed(true); return; }
    const next = changeSelection(review.source.choices, draft, field, value);
    if (next) setDraft(next);
  }
  return <>
    <button id="next-send-model-chooser" type="button" disabled={disabled} aria-haspopup="dialog" aria-expanded={!!review} aria-label={t("Next Send model selection")}
      title={t("Choose next Send model (Commands / Ctrl+P)")} onClick={event => {
        if (disabled || active.current || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
        const source = capture();
        if (!source || !boundedModelChoices(source.choices) || !source.current()) return;
        focus.cancel(); composing.current = false;
        const value = { source, origin: event.currentTarget, valid: true };
        active.current = value; setDraft(source.selection); setQuery(""); setFailed(false); setReview(value);
      }}><AppIcon name="search" size={16} /><span className="sr-only">{t("Search models")}</span></button>
    {review && <dialog ref={dialog} className="app-dialog model-chooser" aria-modal="true" aria-labelledby="model-chooser-title"
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
      <AppWindowSurface storageKey="codealta.desktop.window.model-chooser.v1" title={t("Next Send model selection")} titleId="model-chooser-title" preferredSize={viewport => ({ width: Math.min(680, viewport.width - 40), height: Math.min(600, viewport.height - 40) })}
        onClose={close} closeLabel={t("Close")}>
      <p>{t("Cached session choices only. Apply changes only the next Send; Close discards edits.")}</p>
      <p>{t("Provider")}: <code>{review.source.selection.providerKey}</code> · {t("Agent prompt")}: <code>{review.source.selection.agentPromptId}</code></p>
      <label>{t("Search models")}<input ref={search} type="search" maxLength={256} value={query} onChange={event => setQuery(event.target.value)} /></label>
      <div className="model-chooser-list">
        {models.map(value => <button type="button" key={value.id} aria-pressed={draft?.modelId === value.id} onClick={() => edit("modelId", value.id)}>
          <span>{value.name}</span> <code>{value.id}</code>
          <span>{t("Supported efforts")}: {value.efforts.join(", ") || t("Unknown")}</span>
          <span>{t("Image input")}: {t(value.imageInput === true ? "Yes" : value.imageInput === false ? "No" : "Unknown")}</span>
        </button>)}
        {!models.length && <p>{t("No models match this search.")}</p>}
      </div>
      <p>{t("Model")}: <code>{draft?.modelId ?? t("Provider default")}</code></p>
      <label>{t("Reasoning effort for next Send")}<HTMLSelect value={draft?.reasoningEffort ?? ""} disabled={failed || !model?.efforts.length}
        onChange={event => edit("reasoningEffort", event.target.value)}>
        <option value="">{t("Model default")}</option>{model?.efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
      </HTMLSelect></label>
      {failed && <p role="alert">{t("Next Send selection could not be validated. No change was applied.")}</p>}
      <button type="button" className="model-chooser-apply" disabled={failed || !draft} onClick={() => {
        if (!draft || !current(review) || !review.source.apply(draft)) { review.valid = false; setFailed(true); return; }
        close();
      }}>{t("Use model for next Send")}</button>
    </AppWindowSurface></dialog>}
  </>;
}
