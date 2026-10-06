import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { InputGroup } from "@blueprintjs/core";
import { sessionOperations, type SessionReferenceSearchResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { fileAppearance, splitProjectPath } from "../fileAppearance";
import { validReferenceSearch } from "../referencePopup";
import { useShellLanguage } from "../shellLanguage";

const pageStep = 8;

// The files of a search result page: the `@` search also returns folders, which cannot be edited.
function searchedFiles(page: SessionReferenceSearchResponse | null) {
  return page ? page.items.filter(row => !row.directory) : [];
}

/**
 * The file picker of the editor (Ctrl+E, `/edit`): the `@` search of a project limited to files, recently
 * used first and fuzzy-ranked as the query grows. Enter opens the selected file in the code editor of the
 * project, Escape closes the window. Ctrl+E again opens the editor with the files of the project instead.
 */
export function OpenFileDialog({ epoch, project, search = sessionOperations.searchReferences, observe, onOpen, onOpenEditor, onClose }: {
  epoch: string; project: Readonly<{ id: string; name: string; path: string }>;
  search?: typeof sessionOperations.searchReferences;
  observe?: (value: { status: string; epoch: string | null }) => void;
  onOpen: (path: string) => void;
  /** The second Ctrl+E: the code editor of the project, with its files shown. */
  onOpenEditor?: () => void;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [page, setPage] = useState<SessionReferenceSearchResponse | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const latest = useRef({ observe }); latest.current = { observe };

  // Search as the query changes; while the project is still being indexed, ask again for the growing result.
  useEffect(() => {
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void search({ expectedEpoch: epoch, projectId: project.id, projectPath: project.path, sessionId: null, query },
        { signal: controller.signal, timeoutMilliseconds: 8000 }).then(value => {
        if (controller.signal.aborted) return;
        latest.current.observe?.(value);
        if (!validReferenceSearch(value, epoch) || !["ok", "indexing", "incomplete"].includes(value.status)) { setFailed(true); setPage(null); return; }
        setFailed(false); setPage(value);
        setSelected(index => Math.min(index, Math.max(0, searchedFiles(value).length - 1)));
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, attempt === 0 ? 60 : 350);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [search, epoch, project.id, project.path, query, attempt]);
  useEffect(() => {
    if (page?.status !== "indexing" || attempt >= 60) return;
    setAttempt(value => value + 1);
  }, [page]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, page]);

  const files = searchedFiles(page);
  const count = files.length;
  const move = (delta: number) => setSelected(index => Math.max(0, Math.min(count - 1, index + delta)));
  const choose = (index: number) => { const row = files[index]; if (row) onOpen(row.path); };
  const status = failed ? t("Project files could not be read.")
    : !page ? t("Loading project files…")
    : page.status === "indexing" ? t("Indexing project… {count} indexed", { count: page.indexed })
    : count === 0 ? t("No files match.")
    : count === 1 ? t("1 match · {count} indexed", { count: page.indexed })
    : t("{shown} matches · {count} indexed", { shown: count, count: page.indexed });
  return <AppWindow storageKey="codealta.desktop.window.open-file.v1" className="reference-palette open-file-dialog" titleId="open-file-title"
    title={<><AppIcon name="edit" size={14} /> {t("Open file")}<span className="reference-project"> · {project.name}</span></>}
    preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
    minimumSize={{ width: 380, height: 240 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => input.current?.focus()}
    headerActions={<span className="reference-status" role="status">{page?.status === "indexing" && <ActivitySpinner size={12} />}{status}</span>}
    onClick={event => { if (event.target === event.currentTarget) onClose(); }}
    onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      // Ctrl+E Ctrl+E: the picker gives way to the editor of the project.
      if (onOpenEditor && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && event.key.toLowerCase() === "e") {
        event.preventDefault();
        if (!event.repeat) onOpenEditor();
        return;
      }
      if (event.ctrlKey || event.altKey || event.metaKey) return;
      const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
        : event.key === "Home" && event.target !== input.current ? (setSelected(0), true)
        : event.key === "End" && event.target !== input.current ? (setSelected(Math.max(0, count - 1)), true)
        : event.key === "Enter" ? (choose(selected), true) : event.key === "Escape" ? (onClose(), true) : false;
      if (handled) event.preventDefault();
    }}>
    <InputGroup inputRef={input} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
      leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={t("Search files…")}
      role="combobox" aria-expanded="true" aria-controls="open-file-results" aria-label={t("Search files…")}
      aria-activedescendant={files[selected] ? `open-file-option-${selected}` : undefined}
      onChange={event => { setQuery(event.target.value); setSelected(0); setAttempt(0); }} />
    <div id="open-file-results" ref={list} role="listbox" aria-label={t("Project files")} className="reference-list">
      {files.map((row, index) => { const look = fileAppearance(row.path, false); const { name, parent } = splitProjectPath(row.path);
        return <div role="option" id={`open-file-option-${index}`} key={row.path} aria-selected={index === selected} className="reference-row" title={row.path}
          onMouseMove={() => { if (index !== selected) setSelected(index); }} onClick={() => choose(index)}>
          <span className="reference-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={16} /></span>
          <span className="reference-name">{name}</span>
          {row.recent && <span className="reference-recent" title={t("Recent")}><AppIcon name="history" size={12} /></span>}
          <span className="reference-parent">{parent}</span>
        </div>; })}
      {page && count === 0 && page.status !== "indexing" && <p className="reference-empty">{t("No files match.")}</p>}
    </div>
    <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("open")}</span>
      {onOpenEditor && <span><kbd>Ctrl+E</kbd> {t("project files")}</span>}<span><kbd>Esc</kbd> {t("close")}</span></footer>
  </AppWindow>;
}
