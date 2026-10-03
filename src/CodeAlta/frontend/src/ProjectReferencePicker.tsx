import { createContext, useContext, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { usePromptPicker } from "./promptPicker";
import { InputGroup } from "@blueprintjs/core";
import { sessionOperations, type SessionReferenceSearchRequest, type SessionReferenceSearchResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { fileAppearance, splitProjectPath } from "./fileAppearance";
import { activeProjectReference, insertProjectReference } from "./projectReferences";
import { ProjectReferencePresentation } from "./ProjectReferencePresentation";
import { validReferenceSearch, type ReferencePopupLifetime } from "./referencePopup";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

export const ProjectReferenceContext = createContext<(Omit<SessionReferenceSearchRequest, "query"> & {
  observe?: (value: { status: string; epoch: string | null }) => void;
  lifetime?: number;
  capturePopup?: () => ReferencePopupLifetime;
}) | null>(null);

const pageStep = 8;

/**
 * The `@` file picker of a prompt editor. Typing `@` at a word start opens a search window listing the
 * project's files and folders (recently used first, then fuzzy-ranked as the query grows); Enter
 * replaces the `@query` with a Markdown link to the selected item, Escape leaves the text as typed.
 */
export function ProjectReferencePicker({ text, edit, input, compact = true }: {
  text: string; edit: (text: string) => void; input: RefObject<PromptInput | null>; compact?: boolean;
}) {
  const { t } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const latest = useRef({ scope }); latest.current = { scope };
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [page, setPage] = useState<SessionReferenceSearchResponse | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const { trigger, dialog, close } = usePromptPicker({ input, edit, enabled: !!scope, detect: activeProjectReference, focus: search,
    onOpen: value => { setQuery(value.query); setSelected(0); setPage(null); setFailed(false); } });
  const open = !!trigger;

  function choose(index: number) {
    const row = page?.items[index];
    const element = input.current;
    if (!row || !trigger || !element || element.value !== trigger.text) return;
    const next = insertProjectReference(trigger.text, trigger.start, trigger.end, row.path, row.directory);
    if (next) close(next);
  }

  // Search as the query changes; while the project is still being indexed, ask again for the growing result.
  useEffect(() => {
    const current = latest.current.scope;
    if (!open || !current) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const { observe, lifetime: _lifetime, capturePopup: _capture, ...request } = current;
      void sessionOperations.searchReferences({ ...request, query }, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(value => {
        if (controller.signal.aborted) return;
        observe?.(value);
        if (!validReferenceSearch(value, current.expectedEpoch) || !["ok", "indexing", "incomplete"].includes(value.status)) { setFailed(true); setPage(null); return; }
        setFailed(false); setPage(value);
        setSelected(index => Math.min(index, Math.max(0, value.items.length - 1)));
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, attempt === 0 ? 60 : 350);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [open, query, attempt]);
  useEffect(() => {
    if (!open || page?.status !== "indexing" || attempt >= 60) return;
    setAttempt(value => value + 1);
  }, [page]);
  useEffect(() => { setAttempt(0); }, [query, open]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, page]);

  const count = page?.items.length ?? 0;
  const move = (delta: number) => setSelected(index => Math.max(0, Math.min(count - 1, index + delta)));
  const matches = count === 1 ? t("1 match · {count} indexed", { count: page?.indexed ?? 0 })
    : t(count >= 64 ? "Top {shown} matches · {count} indexed" : "{shown} matches · {count} indexed", { shown: count, count: page?.indexed ?? 0 });
  const status = failed ? t("Project files could not be read.")
    : !page ? t("Loading project files…")
    : page.status === "indexing" ? t("Indexing project… {count} indexed", { count: page.indexed })
    : count === 0 ? t("No files or folders match.")
    : matches;
  const project = scope ? scope.projectPath.replace(/[\\/]+$/u, "").split(/[\\/]/u).at(-1) ?? "" : "";
  return <>{!compact && <ProjectReferencePresentation text={text} input={input} scope={scope} />}
    {trigger && <dialog ref={dialog} className="app-dialog reference-palette" aria-modal="true" aria-labelledby={`${listId}-title`}
      onCancel={event => { event.preventDefault(); close(); }}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.ctrlKey || event.altKey || event.metaKey) return;
        const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
          : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
          : event.key === "Home" && event.target !== search.current ? (setSelected(0), true)
          : event.key === "End" && event.target !== search.current ? (setSelected(Math.max(0, count - 1)), true)
          : event.key === "Enter" ? (choose(selected), true) : event.key === "Escape" ? (close(), true) : false;
        if (handled) event.preventDefault();
      }}>
      <AppWindowSurface storageKey="codealta.desktop.window.references.v1" titleId={`${listId}-title`}
        title={<><AppIcon name="folder" size={14} /> {t("Project files")}{project && <span className="reference-project"> · {project}</span>}</>}
        preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
        minimumSize={{ width: 380, height: 240 }} onClose={() => close()} closeLabel={t("Close")}
        headerActions={<span className="reference-status" role="status">{page?.status === "indexing" && <ActivitySpinner size={12} />}{status}</span>}>
        <InputGroup inputRef={search} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
          leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={t("Search files and folders…")}
          role="combobox" aria-expanded="true" aria-controls={listId} aria-label={t("Search files and folders…")}
          aria-activedescendant={page?.items[selected] ? `${listId}-${selected}` : undefined}
          onChange={event => { setQuery(event.target.value); setSelected(0); }} />
        <div id={listId} ref={list} role="listbox" aria-label={t("Project files")} className="reference-list">
          {page?.items.map((row, index) => { const look = fileAppearance(row.path, row.directory); const { name, parent } = splitProjectPath(row.path);
            return <div role="option" id={`${listId}-${index}`} key={row.path} aria-selected={index === selected} className="reference-row" title={row.path}
              onMouseMove={() => { if (index !== selected) setSelected(index); }} onClick={() => choose(index)}>
              <span className="reference-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={16} /></span>
              <span className="reference-name">{name}{row.directory ? "/" : ""}</span>
              {row.recent && <span className="reference-recent" title={t("Recent")}><AppIcon name="history" size={12} /></span>}
              <span className="reference-parent">{parent}</span>
            </div>; })}
          {page && count === 0 && page.status !== "indexing" && <p className="reference-empty">{t("No files or folders match.")}</p>}
        </div>
        <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert link")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
      </AppWindowSurface>
    </dialog>}
  </>;
}
