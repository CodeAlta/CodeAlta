import { createContext, useContext, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { sessionOperations, type SessionReferenceSearchRequest, type SessionReferenceSearchResponse } from "#neoastra";
import { activeProjectReference, insertProjectReference } from "./projectReferences";
import { ProjectReferencePresentation } from "./ProjectReferencePresentation";
import { useShellLanguage } from "./shellLanguage";

export const ProjectReferenceContext = createContext<(Omit<SessionReferenceSearchRequest, "query"> & {
  observe?: (value: { status: string; epoch: string | null }) => void;
  lifetime?: number;
}) | null>(null);

export function ProjectReferencePicker({ text, edit, input }: {
  text: string; edit: (text: string) => void; input: RefObject<HTMLTextAreaElement | null>;
}) {
  const { t } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const identity = JSON.stringify(scope);
  const [caret, setCaret] = useState(0);
  const [engaged, setEngaged] = useState(false);
  const [revision, setRevision] = useState(0);
  const [composing, setComposing] = useState(false);
  const listId = useId();
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [page, setPage] = useState<{ key: string; value: SessionReferenceSearchResponse } | null>(null);
  const [selected, setSelected] = useState(0);
  const editGeneration = useRef(0);
  const focusWork = useRef<{ frame: number; text: string } | null>(null);
  function cancelFocus() {
    if (focusWork.current) cancelAnimationFrame(focusWork.current.frame);
    focusWork.current = null;
  }
  useLayoutEffect(() => cancelFocus, [identity, input]);
  useLayoutEffect(() => { if (focusWork.current && focusWork.current.text !== text) cancelFocus(); }, [text]);
  const active = activeProjectReference(text, caret);
  const key = JSON.stringify([identity, text, caret, revision]);
  const current = useRef(key); current.current = key;
  const eligible = engaged && !!scope && !!active && !composing && dismissed !== key;
  const visible = eligible && page?.key === key ? page.value : null;
  useLayoutEffect(() => { setEngaged(false); }, [identity]);
  useLayoutEffect(() => {
    const element = input.current;
    if (!element) return;
    const observe = () => setCaret(element.selectionStart === element.selectionEnd ? element.selectionStart : 0);
    const interact = () => { setEngaged(true); observe(); };
    const edited = () => { editGeneration.current++; cancelFocus(); setRevision(value => value + 1); interact(); };
    const beginComposition = () => { editGeneration.current++; cancelFocus(); setComposing(true); };
    const endComposition = () => { setComposing(false); observe(); };
    element.addEventListener("compositionstart", beginComposition);
    element.addEventListener("compositionend", endComposition);
    element.addEventListener("input", edited);
    for (const name of ["click", "keyup", "select"]) element.addEventListener(name, interact);
    observe();
    return () => { for (const name of ["click", "keyup", "select"]) element.removeEventListener(name, interact);
      element.removeEventListener("input", edited);
      element.removeEventListener("compositionstart", beginComposition); element.removeEventListener("compositionend", endComposition); };
  }, [input, text]);
  useLayoutEffect(() => {
    setPage(null); setSelected(0);
    if (!eligible || !scope || !active) return;
    const abort = new AbortController();
    const timer = setTimeout(() => {
      const { observe, lifetime: _lifetime, ...request } = scope;
      void sessionOperations.searchReferences({ ...request, query: active.query }, { signal: abort.signal, timeoutMilliseconds: 3000 })
        .then(value => {
          if (typeof value.status === "string" && (value.epoch === null || typeof value.epoch === "string")) observe?.(value);
          if (abort.signal.aborted || current.current !== key || value.epoch !== scope.expectedEpoch) return;
          if (value.items.length > 64) return;
          setPage({ key, value });
        }).catch(() => { if (!abort.signal.aborted && current.current === key)
          setPage({ key, value: { status: "read_error", epoch: scope.expectedEpoch, items: [], omitted: true } }); });
    }, 150);
    return () => { clearTimeout(timer); abort.abort(); };
  }, [key, eligible]);
  function choose(index: number) {
    if (!active || !visible || current.current !== key || !["ok", "incomplete", "read_error"].includes(visible.status)) return;
    const row = visible.items[index];
    if (!row || input.current?.value !== text) return;
    const next = insertProjectReference(text, active.start, active.end, row.path, row.directory);
    if (!next) return;
    cancelFocus();
    const element = input.current;
    const generation = editGeneration.current;
    const work = { frame: 0, text: next.text };
    focusWork.current = work;
    setDismissed(key); edit(next.text);
    work.frame = requestAnimationFrame(() => {
      if (focusWork.current !== work) return;
      focusWork.current = null;
      if (element && element === input.current && element.isConnected && generation === editGeneration.current && element.value === next.text) {
        element.focus(); element.setSelectionRange(next.caret, next.caret);
      }
    });
  }
  useLayoutEffect(() => {
    const element = input.current;
    if (!element || !eligible) return;
    element.setAttribute("aria-controls", listId);
    if (visible?.items.length) element.setAttribute("aria-activedescendant", `${listId}-${selected}`);
    const handle = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      if (event.key === "Escape") setDismissed(key);
      else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        const count = visible?.items.length ?? 0;
        if (count) setSelected(index => (index + (event.key === "ArrowDown" ? 1 : -1) + count) % count);
      } else if (event.key === "Enter" || event.key === "Tab") { if (visible?.items.length) choose(selected); }
      else return;
      // Enter while loading/no match never falls through into Send.
      event.preventDefault(); event.stopImmediatePropagation();
    };
    element.addEventListener("keydown", handle, true);
    return () => { element.removeEventListener("keydown", handle, true); element.removeAttribute("aria-controls"); element.removeAttribute("aria-activedescendant"); };
  }, [key, eligible, visible, selected]);
  return <><ProjectReferencePresentation text={text} input={input} scope={scope} />{eligible && <aside className="project-reference-picker" aria-label={t("Project references")}>
    <p role="status">{visible ? <>{visible.status}{visible.omitted ? ` — ${t("results omitted")}` : ""}</> : t("Searching bounded project metadata…")}. {t("References resolve on normal Send; queue/steer remain literal.")}</p>
    <div id={listId} role="listbox" aria-label={t("Project file matches")}>{visible?.items.map((row, index) => <button id={`${listId}-${index}`} type="button" role="option"
      key={row.path} aria-selected={index === selected} onMouseDown={event => event.preventDefault()} onClick={() => choose(index)}>
      {row.recent ? `${t("Recent")} · ` : ""}<span className="reference-path">{row.path}</span>{row.directory ? "/" : ""}</button>)}</div>
    <button type="button" onClick={() => setDismissed(key)}>{t("Close references")}</button>
  </aside>}</>;
}
