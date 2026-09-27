import { useEffect, useState, useSyncExternalStore } from "react";
import type { WorkspaceDeleteSessionRequest } from "#neoastra";
import { selectVisibleSessions, sessionDeleteBatchLimit, type SessionBatchDeletion } from "./sessionBatchDeletion";
import { useShellLanguage } from "./shellLanguage";

export type BatchDeleteControls = { owner: SessionBatchDeletion; epoch: string; canReview: boolean;
  review: (requests: readonly WorkspaceDeleteSessionRequest[]) => boolean };
export function SessionBatchDeletePanel({ controls, candidates, inputKey }: { controls: BatchDeleteControls;
  candidates: readonly WorkspaceDeleteSessionRequest[]; inputKey: string }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(controls.owner.subscribe, controls.owner.getSnapshot);
  const [selected, setSelected] = useState<string[]>([]);
  const [confirmation, setConfirmation] = useState("");
  const [notice, setNotice] = useState<"limit" | "changed" | null>(null);
  useEffect(() => { controls.owner.invalidate(); setSelected([]); setConfirmation(""); }, [inputKey, controls.owner]);
  useEffect(() => () => controls.owner.invalidate(), [controls.owner]);
  function select(next: string[] | null) {
    controls.owner.invalidate(); setConfirmation("");
    if (next === null) { setNotice("limit"); return; }
    setSelected(next); setNotice(null);
  }
  const editable = controls.canReview && state.phase !== "running" && state.phase !== "settled";
  return <section className="session-batch-delete" aria-label={t("Batch session deletion")}>
    <details open={state.phase !== "idle" || undefined}><summary>{t("Select saved sessions for exact deletion")}</summary>
      <p>{t("Visible verified candidates only; maximum {limit}. Search, order, count, scope or browser lifetime changes clear selection/review and stop further dispatch. Selection never opens a session. No project archive, cascade or child ordering.", { limit: sessionDeleteBatchLimit })}</p>
      {!controls.canReview && <p>{t("Deletion unavailable: owned unchanged host and scope required, with no conflicting or uncertain operation.")}</p>}
      {!candidates.length && <p>{t("No eligible visible targets. Archived, ambiguous, truncated or unverified identity/title evidence cannot authorize deletion.")}</p>}
      <button type="button" disabled={!editable} onClick={() => select(selectVisibleSessions(selected, candidates.map(row => row.sessionId), false))}>{t("Select visible eligible")}</button>
      <button type="button" disabled={!editable} onClick={() => select(selectVisibleSessions(selected, candidates.map(row => row.sessionId), true))}>{t("Invert visible eligible")}</button>
      <button type="button" disabled={!editable} onClick={() => select([])}>{t("Clear selection")}</button>
      <fieldset disabled={!editable}><legend>{t("{selected} selected / {visible} visible eligible", { selected: selected.length, visible: candidates.length })}</legend>
        {candidates.map(row => <label key={row.sessionId}><input type="checkbox" checked={selected.includes(row.sessionId)}
          onChange={event => select(event.target.checked ? selected.length >= sessionDeleteBatchLimit ? null : [...selected, row.sessionId] : selected.filter(id => id !== row.sessionId))} />
          {row.confirmedTitle} · {row.sessionId}</label>)}
      </fieldset>
      <button type="button" disabled={!editable || !selected.length || state.phase === "review"} onClick={() => {
        setConfirmation(""); if (!controls.review(candidates.filter(row => selected.includes(row.sessionId)))) setNotice("changed");
      }}>{t("Review exact deletion targets")}</button>
      {notice && <p role="status">{notice === "limit" ? t("Selection exceeds {limit}; selection unchanged, previous review canceled. Narrow visible results or select individually.", { limit: sessionDeleteBatchLimit }) : t("Capture changed or operation blocked. Nothing requested; reopen and review.")}</p>}
    </details>
    {state.phase === "idle" && <p role="status">{state.message}</p>}
    {state.items.length > 0 && <div className="session-batch-report" role="region" aria-label={t("Original batch deletion targets and outcomes")}>
      <p role="status">{state.message}</p>
      <ol>{state.items.map(item => <li key={item.request.sessionId}><strong>{item.request.confirmedTitle}</strong>
        <div>{t("ID: {id} · {scope} · project: {project} · path: {path} · host: {host}", { id: item.request.sessionId, scope: item.request.scope, project: item.request.projectId ?? "global", path: item.request.projectPath ?? "", host: item.request.expectedHostEpoch })}</div>
        <strong>{t(item.outcome)}</strong>{item.code && ` · ${item.code}`}</li>)}</ol>
      {state.phase === "review" && <><label>{t("Type {confirmation} to permanently delete exactly these reviewed session histories", { confirmation: `DELETE ${state.items.length}` })}
        <input aria-label={t("Confirm exact batch deletion")} value={confirmation} maxLength={16} autoComplete="off" onChange={event => setConfirmation(event.target.value)} /></label>
        <button type="button" disabled={confirmation !== `DELETE ${state.items.length}` || !controls.canReview}
          onClick={() => { void controls.owner.confirm(confirmation); setConfirmation(""); }}>{t("Delete reviewed sessions")}</button>
        <button type="button" onClick={() => { controls.owner.invalidate(); setConfirmation(""); }}>{t("Cancel review")}</button></>}
      {state.phase === "running" && <button type="button" onClick={() => controls.owner.invalidate()}>{t("Stop after pending original")}</button>}
      {state.phase === "settled" && (state.items.some(item => item.outcome === "uncertain")
        ? <p role="alert">{t("Deletion may have completed. Original outcome is uncertain; all local deletion remains blocked. Refresh cannot unlock or retry this batch. Inspect independently; reloading is not proof of completion.")}</p>
        : <button type="button" onClick={() => { controls.owner.clearSettled(); setSelected([]); }}>{t("Acknowledge and clear settled report (no continuation)")}</button>)}
      <p>{t("The loaded catalog may still show deleted rows. No automatic reconciliation, tab removal, draft clearing or workspace change is performed.")}</p>
    </div>}
  </section>;
}
