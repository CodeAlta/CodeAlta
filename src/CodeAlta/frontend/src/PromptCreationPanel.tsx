import { HTMLSelect } from "@blueprintjs/core";
import { useState, useSyncExternalStore } from "react";
import type { PromptCreation } from "./promptCreation";
import type { SkillsCapture } from "./skillsInspection";
import { useShellLanguage } from "./shellLanguage";

export function PromptCreationPanel({ owner, capture }: { owner: PromptCreation; capture: () => SkillsCapture | null }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const [discard, setDiscard] = useState(false);
  const [error, setError] = useState(false);
  const d = state.draft;
  const editable = state.phase === "draft";
  return <section className="prompt-creation" aria-label={t("Create agent prompt")}>
    <h2>{t("Create an agent prompt source")}</h2>
    <p>{t("Create only. Built-ins and existing sources are read-only here. No template is copied from truncated catalog content.")}</p>
    {state.phase === "empty" ? <button type="button" disabled={!capture()} onClick={() => owner.start(capture())}>{t("New agent prompt")}</button> : <>
      <p>{t("Original session:")} <code>{state.target?.sessionId}</code>. {t("Project:")} <code>{state.target?.projectPath ?? t("User-global session")}</code>.</p>
      <fieldset disabled={!editable}><legend>{t("New source fields")}</legend>
        <label>{t("Publication scope")}<HTMLSelect value={d.rootKind} onChange={e => owner.update({ rootKind: e.target.value, understoodShadowing: false })}>
          <option value="">{t("Choose scope explicitly")}</option><option value="user_alta">{t("User-global prompts")}</option>
          {state.target?.scope === "project" && <option value="project_alta">{t("Original project prompts")}</option>}</HTMLSelect></label>
        <label>{t("Prompt ID")}<input value={d.promptId} maxLength={64} onChange={e => owner.update({ promptId: e.target.value })} /></label>
        <p>{t("1-64 lowercase ASCII letters, digits or hyphens; starts with a letter. Reserved device names are refused.")}</p>
        <label>{t("Display name")}<input value={d.name} maxLength={128} onChange={e => owner.update({ name: e.target.value })} /></label>
        <label>{t("Description (optional)")}<input value={d.description} maxLength={512} onChange={e => owner.update({ description: e.target.value })} /></label>
        <label>{t("Body")}<textarea value={d.body} maxLength={16384} rows={8} onChange={e => owner.update({ body: e.target.value })} /></label>
        <label>{t("Composition mode")}<HTMLSelect value={d.mode} onChange={e => owner.update({ mode: e.target.value, understoodShadowing: false })}>
          <option value="">{t("Choose mode explicitly")}</option><option value="replace">{t("Replace lower-precedence body")}</option><option value="append">{t("Append to lower-precedence body")}</option></HTMLSelect></label>
        <p>{t("Precedence: built-in → user-global → project. Same-ID sources compose in that order; replace discards lower bodies, append adds to them. A project source may shadow this user-global source. Name/description override lower supplied metadata; absent system metadata inherits in append mode, otherwise defaults to default. Outer body/metadata whitespace is trimmed and body newlines normalize when parsed. This does not edit any system prompt.")}</p>
        <label><input type="checkbox" checked={d.understoodShadowing} onChange={e => owner.update({ understoodShadowing: e.target.checked })} />
          {t("I understand same-ID cross-scope shadowing and composition; this is not necessarily an isolated new effective prompt.")}</label>
      </fieldset>
      {editable && <button type="button" onClick={() => setError(!owner.review(capture()))}>{t("Review creation")}</button>}
      {state.phase === "review" && <><p>{t("Publish {file} to {scope} agent prompts, mode {mode}?", { file: `${d.promptId}.prompt.md`, scope: t(d.rootKind === "project_alta" ? "the original project" : "user-global"), mode: d.mode })}</p>
        <pre>{d.body}</pre><button type="button" onClick={() => owner.update({})}>{t("Back to draft")}</button>
        <button type="button" onClick={() => void owner.confirm()}>{t("Confirm create only")}</button></>}
      {(editable || state.phase === "review") && (discard ? <><p>{t("Discard this unsaved authoring draft?")}</p>
        <button type="button" onClick={() => { owner.discard(); setDiscard(false); }}>{t("Confirm discard unsaved draft")}</button>
        <button type="button" onClick={() => setDiscard(false)}>{t("Keep draft")}</button></>
        : <button type="button" onClick={() => setDiscard(true)}>{t("Discard unsaved draft")}</button>)}
      {state.original && <details><summary>{t("Retained original request and text")}</summary><pre>{JSON.stringify(state.original, null, 2)}</pre></details>}
      {["created", "conflict", "refused"].includes(state.phase) && <button type="button" disabled={state.outcomes.length >= 7 || !capture()}
        onClick={() => owner.next(capture())}>{t("New blank draft (keep original outcome)")}</button>}
    </>}
    {state.outcomes.map(outcome => <details key={outcome.original.requestId}><summary>{t("Earlier {phase}: {id} ({scope})", { phase: outcome.phase === "created" || outcome.phase === "conflict" || outcome.phase === "refused" ? t(outcome.phase) : outcome.phase, id: outcome.original.promptId, scope: outcome.original.rootKind })}</summary>
      <p>{outcome.message}</p><pre>{JSON.stringify(outcome.original, null, 2)}</pre></details>)}
    {state.outcomes.length >= 7 && <p>{t("Eight-original App retention limit reached. No earlier outcome is evicted.")}</p>}
    <p role="status">{state.message}</p>{error && <p role="alert">{t("Check all bounds, scope, mode and acknowledgement. Original saved session/catalog must still match; no automatic rebase.")}</p>}
  </section>;
}
