import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { canImportCheckedFolder, projectOpeningMessage, type createProjectOpening } from "./projectOpening";
import { savedProjectSelection, type SavedProjectIdentity } from "./savedProjectSelection";
import { AppIcon } from "./AppIcon";

export function OpenProjectDialog({ snapshot, getCurrentSnapshot, epoch, capability, opening, onOpen, onRefresh, onImported, onClose }: {
  snapshot: WorkspaceSnapshot | undefined; getCurrentSnapshot: () => WorkspaceSnapshot | undefined; epoch: string | undefined;
  capability: ReturnType<typeof createMutationCapability> | undefined;
  opening: ReturnType<typeof createProjectOpening>;
  onOpen: (project: SavedProjectIdentity) => boolean; onRefresh: (signal: AbortSignal) => Promise<{ configured: boolean } | undefined>;
  onImported: (id: string, path: string, signal: AbortSignal) => Promise<boolean>;
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState("");
  const [active, setActive] = useState(0);
  const [preview, setPreview] = useState<{ requestedPath: string; path: string }>();
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [notice, setNotice] = useState("");
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [, notifyCapability] = useState(0);
  const canImport = !!epoch && !!capability?.canMutate();
  const alive = useRef(true);
  const origin = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);
  const importAttempted = useRef(false);
  const busyNow = useRef(false);
  const results = useRef<HTMLDivElement>(null);
  const followUp = useRef(new AbortController());
  useEffect(() => {
    alive.current = true;
    followUp.current = new AbortController();
    return () => { alive.current = false; followUp.current.abort(); };
  }, []);
  useEffect(() => {
    return capability?.subscribe(() => notifyCapability(value => value + 1));
  }, [capability]);
  const restoreFocus = () => requestAnimationFrame(() => {
    if (origin.current?.isConnected && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.current.focus();
  });
  const close = () => {
    alive.current = false; followUp.current.abort(); onClose();
    restoreFocus();
  };
  const normalized = filter.trim().toLowerCase();
  const matches = (snapshot?.projects ?? []).filter(project => !normalized || project.name.toLowerCase().includes(normalized)
    || project.path.toLowerCase().includes(normalized))
    .sort((a, b) => a.name.toLowerCase() < b.name.toLowerCase() ? -1 : a.name.toLowerCase() > b.name.toLowerCase() ? 1
      : a.id < b.id ? -1 : a.id > b.id ? 1 : a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  const index = Math.min(active, matches.length - 1);
  useLayoutEffect(() => {
    const list = results.current;
    const option = list?.querySelector<HTMLElement>('[role="option"][aria-selected="true"]');
    if (!list || !option) return;
    const viewport = list.getBoundingClientRect();
    const selected = option.getBoundingClientRect();
    if (selected.top < viewport.top) list.scrollTop += selected.top - viewport.top;
    else if (selected.bottom > viewport.bottom) list.scrollTop += selected.bottom - viewport.bottom;
  });
  function choose(project: SavedProjectIdentity) {
    if (!alive.current) return;
    if (busyNow.current || busy || refreshFailed || importAttempted.current || !snapshot?.configured
      || getCurrentSnapshot() !== snapshot || !savedProjectSelection(project, getCurrentSnapshot())) {
      setMessage("The saved project is no longer verified in this list. Refresh and select its current entry.");
      return;
    }
    if (onOpen(project)) restoreFocus();
    else setMessage("The saved project changed before navigation. Refresh and select its current entry.");
  }
  function savedKeys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
    if ((event.key === "ArrowDown" || event.key === "ArrowUp") && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      if (matches.length) setActive((index + (event.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length);
    } else if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && index >= 0) {
      event.preventDefault(); choose(matches[index]);
    }
  }
  async function checkPath() {
    if (busyNow.current || busy || importAttempted.current || !canImport) return;
    const requested = query.trim();
    busyNow.current = true;
    setBusy(true);
    setPreview(undefined);
    setConfirmed(false);
    setMessage("");
    setNotice("");
    const result = await opening.preview(epoch, requested, capability);
    if (!alive.current) return;
    busyNow.current = false;
    setBusy(false);
    if (result.kind === "ready") setPreview(result);
    else setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "invalid_response"));
  }
  async function importPath() {
    if (busyNow.current || importAttempted.current || !canImportCheckedFolder(preview, confirmed, busy, canImport) || !preview) return;
    importAttempted.current = true;
    busyNow.current = true;
    setBusy(true);
    setMessage("");
    setNotice("");
    const result = await opening.import(epoch, preview, capability);
    if (!alive.current) return;
    busyNow.current = false;
    if (result.kind === "imported") {
      if (await onImported(result.id, result.path, followUp.current.signal)) { if (alive.current) close(); return; }
      if (!alive.current) return;
      setMessage("The project may have been imported, but the refreshed catalog did not show it. Inspect the project list before retrying.");
    } else {
      if (result.kind === "error" && ["unconfigured", "invalid_request", "missing_directory", "stale_epoch", "busy", "closed"].includes(result.code))
        importAttempted.current = false; // A definitive refusal did not leave an uncertain import to preserve.
      setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "import_unconfirmed"));
    }
    setPreview(undefined);
    setConfirmed(false);
    setBusy(false);
  }
  async function refreshList() {
    if (busyNow.current || busy) return;
    busyNow.current = true;
    setBusy(true);
    const fresh = await onRefresh(followUp.current.signal);
    if (!alive.current) return;
    busyNow.current = false;
    setBusy(false);
    setRefreshFailed(!fresh);
    if (fresh) { if (!importAttempted.current) setMessage(""); setNotice("Project list refreshed. Check the entries before requesting another import."); }
    else if (importAttempted.current) setNotice("Could not refresh the project list. The import outcome remains unconfirmed.");
    else setMessage("Could not refresh the project list. No import was requested.");
  }
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close(); }}>
    <section className="app-dialog" role="dialog" aria-modal="true" aria-labelledby="open-project-title"
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
        if (event.key === "Escape") { event.preventDefault(); close(); }
        else if (event.key === "Tab") {
          const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled])'));
          if (event.shiftKey && document.activeElement === controls[0]) { event.preventDefault(); controls.at(-1)?.focus(); }
          else if (!event.shiftKey && document.activeElement === controls.at(-1)) { event.preventDefault(); controls[0]?.focus(); }
        }
      }}>
      <header><div><span className="eyebrow">Workspace</span><h2 id="open-project-title">Open project</h2></div><button type="button" className="icon-button" aria-label="Close" title="Close" onClick={close}><AppIcon name="close" size={16} /></button></header>
      <label htmlFor="saved-project-filter">Find a saved project by name or full path</label>
      <input autoFocus id="saved-project-filter" type="search" role="combobox" aria-autocomplete="list"
        aria-expanded={matches.length > 0} aria-controls="saved-project-results"
        aria-activedescendant={index >= 0 ? `saved-project-${index}` : undefined} value={filter}
        onChange={event => { setFilter(event.target.value); setActive(0); }} onKeyDown={savedKeys} placeholder="Saved project name or path" />
      <div ref={results} id="saved-project-results" className="dialog-list" role="listbox" aria-label="Saved projects (name order)">
        {matches.map((project, i) => <button type="button" role="option" id={`saved-project-${i}`} key={`${project.id}:${project.path}:${i}`}
          aria-selected={i === index} onFocus={() => setActive(i)} onMouseEnter={() => setActive(i)} onClick={() => choose(project)}
          onKeyDown={event => { if (event.key === "Enter" && !event.repeat && !event.defaultPrevented && !event.nativeEvent.isComposing
            && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
            event.preventDefault(); choose(project); } }}
          disabled={busy || refreshFailed || importAttempted.current}>
          <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}{project.archived ? " (archived, read-only)" : ""}</strong><small title={project.path}>{project.path}</small></span>
        </button>)}
        {matches.length === 0 && <p role="status">{filter.trim() ? "No saved projects match this name or path." : "No saved projects in this snapshot."}</p>}
      </div>
      {snapshot?.projectsTruncated && <p role="status" className="muted-text">Saved project snapshot is truncated; omitted projects cannot be searched here. Refresh to inspect the current bounded list.</p>}
      {!snapshot && <p role="alert">The saved project list is unavailable. Refresh projects before selecting.</p>}
      {refreshFailed && <p role="alert">Saved selection is paused after a failed refresh. Retry Refresh projects before selecting.</p>}
      {importAttempted.current && <p role="status">Saved selection and new folder requests are unavailable after an import attempt in this dialog. Close and inspect the project list before selecting.</p>}
      {!canImport && <p className="muted-text">Adding a folder requires an owned host. Catalog-only browsing never changes the project list.</p>}
      {canImport && <div className="project-import">
        <label htmlFor="project-folder-path">Add a different existing folder (requires trust confirmation)</label>
        <input id="project-folder-path" aria-label="Absolute folder path to check" value={query} disabled={busy || importAttempted.current}
          onChange={event => { setQuery(event.target.value); setPreview(undefined); setConfirmed(false); setMessage(""); setNotice(""); }}
          onKeyDown={event => { if (event.key === "Enter" && !event.defaultPrevented && !event.nativeEvent.isComposing
            && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
            event.preventDefault(); void checkPath(); } }} placeholder="Absolute folder path" />
        <button type="button" className="quiet-button" disabled={busy || importAttempted.current || !query.trim()} onClick={() => void checkPath()}>Check folder</button>
        {preview && <><p>Existing folder: <code>{preview.path}</code></p>
          <label><input type="checkbox" checked={confirmed} disabled={busy || importAttempted.current} onChange={event => setConfirmed(event.target.checked)} /> I trust this folder and want to add it to the active project catalog.</label>
          <button type="button" className="quiet-button" disabled={importAttempted.current || !canImportCheckedFolder(preview, confirmed, busy, canImport)} onClick={() => void importPath()}>Import and open folder</button></>}
      </div>}
      {busy && <p role="status">Checking or importing the folder…</p>}
      {notice && <p role="status">{notice}</p>}
      {message && <p role="alert" className="error-text">{message}</p>}
      <footer><span><kbd>Ctrl</kbd>+<kbd>O</kbd> · <kbd>Esc</kbd></span><span>
        <button type="button" className="quiet-button" disabled={busy} onClick={() => void refreshList()}>Refresh projects</button>{" "}
        <button type="button" className="quiet-button" onClick={close}>Cancel</button></span></footer>
    </section>
  </div>;
}
