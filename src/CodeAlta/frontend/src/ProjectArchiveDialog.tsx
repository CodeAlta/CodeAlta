import { useEffect, useRef, useState } from "react";
import { archiveConsequences, archiveScopeCurrent, type ArchiveScope, type ArchiveTarget, type createProjectArchive } from "./projectArchive";
import { useShellLanguage } from "./shellLanguage";
import { workflowNotice, type WorkflowNotice } from "./workflowNotice";

type Props = {
  owner: ReturnType<typeof createProjectArchive>; open: boolean; close: () => void;
  current: () => ArchiveScope | null; refresh: (epoch: string) => Promise<void>;
};

export function ProjectArchiveDialog({ owner, open, close, current, refresh }: Props) {
  const { t, locale } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const generation = useRef(0);
  const [target, setTarget] = useState<ArchiveTarget | null>(null);
  const [message, setMessage] = useState<WorkflowNotice>("");
  const [busy, setBusy] = useState(false);
  const [, render] = useState(0);
  const currentRef = useRef(current); currentRef.current = current;
  function dismiss() { generation.current++; setTarget(null); close(); }
  useEffect(() => {
    const version = ++generation.current;
    setTarget(null);
    if (!open) return;
    dialog.current?.showModal();
    const scope = currentRef.current();
    if (!scope) { setMessage({ key: "An exact saved project and owned host are required. No mutation was sent." }); return; }
    setBusy(true); setMessage({ key: "Reading exact catalog source and revision…" });
    void owner.prepare(scope).then(result => {
      if (generation.current !== version) return;
      if (typeof result === "string") setMessage(result);
      else if (!archiveScopeCurrent(scope, currentRef.current())) setMessage({ key: "Scope changed; close and begin a new confirmation." });
      else { setTarget(result); setMessage(""); }
    }).finally(() => { if (generation.current === version) setBusy(false); });
    return () => { generation.current++; };
  }, [open, owner]);

  async function confirm() {
    if (!target || busy || !archiveScopeCurrent(target, current())) return;
    const captured = target;
    setTarget(null); setBusy(true);
    const work = owner.confirm(captured, current());
    render(value => value + 1);
    const result = await work;
    setBusy(false); render(value => value + 1);
    if (result?.state === "confirmed") {
      // Exact terminal evidence remains confirmed even when a later display refresh fails.
      try { await refresh(captured.epoch); } catch { /* Never retry or reinterpret the write. */ }
    }
  }

  if (!open) return null;
  const stale = target && !archiveScopeCurrent(target, current());
  return <dialog ref={dialog} className="archive-dialog" aria-labelledby="archive-title" onCancel={event => { event.preventDefault(); dismiss(); }}
    onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) {
        if (event.key === "Enter" || event.key === "Escape") { event.preventDefault(); event.stopPropagation(); }
      }
    }}>
    <h2 id="archive-title">{t("Project archive / unarchive")}</h2>
    <p>{t(archiveConsequences)}</p>
    {message && <p role="status">{workflowNotice(locale, message)}</p>}
    {target && <><p>{t(target.archived ? "Unarchive project {id} at {path}?" : "Archive project {id} at {path}?", { id: target.id, path: target.path })}</p>
      <details><summary>{t("Exact confirmation evidence")}</summary><p>{t("Host:")} {target.epoch}</p><p>{t("Source:")} {target.source}</p><p>{t("Revision:")} {target.revision}</p></details>
      {stale && <p role="alert">{t("Scope changed. Close and begin a new confirmation.")}</p>}
      <button type="button" disabled={busy || owner.locked || !!stale} onClick={() => void confirm()}>{t(target.archived ? "Confirm unarchive" : "Confirm archive")}</button></>}
    {owner.records.length > 0 && <section aria-label={t("Retained archive operation evidence")} aria-live="polite">
      {owner.records.map((record, index) => <details key={index} open><summary>{record.status}</summary>
        <p>{t(record.target.archived ? "Unarchive" : "Archive")} · {record.target.id} · {record.target.path}</p>
        <p>{t("Host:")} {record.target.epoch}</p><p>{t("Source:")} {record.target.source}</p><p>{t("Expected revision:")} {record.target.revision}</p>
      </details>)}
    </section>}
    <button type="button" onClick={dismiss} autoFocus>{t("Close")}</button>
  </dialog>;
}
