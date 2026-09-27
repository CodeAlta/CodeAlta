import { createContext, useContext, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { sessionOperations, type SessionReferenceSearchRequest, type SessionReferenceSearchResponse } from "#neoastra";
import { captureReferenceInput, closeReferencePopup, createReferenceSearchFence, referencePopupReadiness, referencePopupKey, validReferenceSearch, type ReferencePopupLifetime } from "./referencePopup";
import { ProjectReferencePresentation } from "./ProjectReferencePresentation";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

export const ProjectReferenceContext = createContext<(Omit<SessionReferenceSearchRequest, "query"> & {
  observe?: (value: { status: string; epoch: string | null }) => void;
  lifetime?: number;
  capturePopup?: () => ReferencePopupLifetime;
}) | null>(null);

export function ProjectReferencePicker({ text, edit, input, compact = true }: {
  text: string; edit: (text: string) => void; input: RefObject<HTMLTextAreaElement | null>; compact?: boolean;
}) {
  const { t } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const identity = JSON.stringify(scope && [scope.expectedEpoch, scope.projectId, scope.projectPath, scope.sessionId]);
  const latest = useRef({ text, scope, identity }); latest.current = { text, scope, identity };
  const revision = useRef(0);
  const [interaction, setInteraction] = useState(0);
  const engaged = useRef(false);
  const composing = useRef(false);
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const listId = useId();
  const dismissed = useRef("");
  const frame = useRef<number | null>(null);
  const controller = useRef<AbortController | null>(null);
  const [searchFence] = useState(createReferenceSearchFence);
  const handoff = useRef<"open" | "closed" | null>(null);
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [page, setPage] = useState<SessionReferenceSearchResponse | null>(null);
  const pageCurrent = useRef<(() => boolean) | null>(null);
  const [failed, setFailed] = useState(false);
  type Review = { source: NonNullable<ReturnType<typeof captureReferenceInput>>; lifetime: ReferencePopupLifetime;
    scope: NonNullable<typeof scope>; parent: HTMLDialogElement | null; identity: string; key: string };
  const active = useRef<Review | null>(null);
  const [review, setReview] = useState<Review | null>(null);
  function cancelFocus() { if (frame.current !== null) cancelAnimationFrame(frame.current); frame.current = null; }
  function abortRead() { pageCurrent.current = null; searchFence.cancel(); controller.current?.abort(); controller.current = null; }
  function readInput() {
    const element = input.current;
    return { node: element, connected: !!element?.isConnected, disabled: !element || element.disabled,
      text: element?.value ?? "", start: element?.selectionStart ?? 0, end: element?.selectionEnd ?? 0, revision: revision.current };
  }
  function foreignModal(value: Review) {
    return Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]'))
      .some(element => element !== dialog.current && element !== value.parent);
  }
  function readiness(value: Review) {
    return referencePopupReadiness(() => active.current === value && value.identity === latest.current.identity && value.source.current()
      && latest.current.text === value.source.original.text && !foreignModal(value)
      && (!value.parent || value.parent.open && value.parent.isConnected), () => composing.current);
  }
  function current(value: Review) { return readiness(value).current(); }
  function ready(value: Review) { return readiness(value).ready(); }
  function finish(next?: { text: string; caret: number }) {
    const value = active.current;
    if (!value) return;
    const restore = ready(value);
    abortRead(); cancelFocus(); engaged.current = false; dismissed.current = value.key;
    let closed = false;
    handoff.current = "closed";
    if (dialog.current?.open) {
      closed = restore && closeReferencePopup(value.lifetime, () => ready(value), () => dialog.current?.close());
      if (dialog.current.open) dialog.current.close();
    }
    handoff.current = null;
    active.current = null; setReview(null); setPage(null);
    if (!restore || !closed) { value.lifetime.retire(); return; }
    const expectedText = next?.text ?? value.source.original.text;
    if (next) edit(next.text);
    frame.current = requestAnimationFrame(() => {
      frame.current = null;
      const element = input.current;
      if (!active.current && !composing.current && value.lifetime.current() && value.identity === latest.current.identity
        && element === value.source.original.node && element?.isConnected && !element.disabled && !element.closest("[inert]")
        && revision.current === value.source.original.revision && element.value === expectedText && !foreignModal(value)
        && (!value.parent || value.parent.open && value.parent.isConnected)) {
        element.focus(); element.setSelectionRange(next?.caret ?? value.source.original.start, next?.caret ?? value.source.original.end);
      }
      value.lifetime.retire();
    });
  }
  function choose(index: number) {
    const value = active.current;
    if (!value || !ready(value) || !pageCurrent.current?.() || !page || !["ok", "incomplete", "read_error"].includes(page.status)) return;
    const row = page.items[index];
    const next = row && value.source.choose(row.path, row.directory);
    if (next) finish(next);
  }
  useLayoutEffect(() => {
    const element = input.current;
    if (!element) return;
    let selection = `${element.selectionStart}:${element.selectionEnd}`;
    const interact = () => {
      const next = `${element.selectionStart}:${element.selectionEnd}`;
      if (next !== selection) { selection = next; revision.current++; cancelFocus(); }
      if (document.activeElement === element) { engaged.current = true; setInteraction(value => value + 1); }
    };
    const edited = () => { revision.current++; cancelFocus(); interact(); };
    const begin = () => { composing.current = true; edited(); };
    const end = () => { composing.current = false; interact(); };
    element.addEventListener("input", edited); element.addEventListener("compositionstart", begin); element.addEventListener("compositionend", end);
    for (const name of ["click", "keyup", "select"]) element.addEventListener(name, interact);
    return () => {
      element.removeEventListener("input", edited); element.removeEventListener("compositionstart", begin); element.removeEventListener("compositionend", end);
      for (const name of ["click", "keyup", "select"]) element.removeEventListener(name, interact);
    };
  }, [input]);
  useLayoutEffect(() => {
    const transition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement)) return;
      const own = event.target === dialog.current && (event as ToggleEvent).newState === (handoff.current === "open" ? "open" : handoff.current === "closed" ? "closed" : "");
      if (own) return;
      cancelFocus(); abortRead();
      if (active.current) { active.current.lifetime.retire(); setFailed(true); setPage(null); }
    };
    document.addEventListener("beforetoggle", transition, true);
    return () => { cancelFocus(); abortRead(); active.current?.lifetime.retire(); active.current = null;
      document.removeEventListener("beforetoggle", transition, true); };
  }, []);
  useLayoutEffect(() => {
    if (active.current || !engaged.current || composing.current || !scope?.capturePopup) return;
    const original = readInput();
    const element = input.current;
    const key = JSON.stringify([identity, original.text, original.start, original.end, original.revision]);
    if (!element || document.activeElement !== element || original.text !== text || dismissed.current === key) return;
    const parent = element.closest("dialog");
    if (Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')).some(node => node !== parent)) return;
    const lifetime = scope.capturePopup();
    const source = captureReferenceInput(original, readInput, lifetime);
    if (!source) return;
    cancelFocus(); engaged.current = false;
    const value = { source, scope, lifetime, parent, identity, key };
    active.current = value; setReview(value); setQuery(source.span.query); setSelected(0); setPage(null); setFailed(false);
  }, [text, interaction, identity]);
  useLayoutEffect(() => {
    if (!review) return;
    const element = dialog.current!;
    handoff.current = "open";
    const opened = review.lifetime.open(() => element.showModal());
    handoff.current = null;
    if (opened && current(review)) { if (ready(review)) search.current?.focus(); }
    else { review.lifetime.retire(); setFailed(true); }
    return () => { if (element.open) element.close(); };
  }, [review]);
  useLayoutEffect(() => {
    if (review && !failed && !current(review)) { abortRead(); review.lifetime.retire(); setFailed(true); setPage(null); }
  });
  useLayoutEffect(() => {
    abortRead(); setPage(null); setSelected(0);
    if (!review || failed || !ready(review)) return;
    const abort = new AbortController(); controller.current = abort;
    const valid = searchFence.capture(() => !abort.signal.aborted && ready(review));
    const timer = setTimeout(() => {
      if (!valid()) return;
      const { observe, lifetime: _lifetime, capturePopup: _capture, ...request } = review.scope;
      void sessionOperations.searchReferences({ ...request, query }, { signal: abort.signal, timeoutMilliseconds: 3000 }).then(value => {
        if (!valid()) return;
        observe?.(value);
        if (!valid()) return;
        pageCurrent.current = valid;
        setPage(validReferenceSearch(value, review.scope.expectedEpoch) ? value
          : { status: "read_error", epoch: review.scope.expectedEpoch, items: [], omitted: true });
      }).catch(() => { if (valid()) setPage({ status: "read_error", epoch: review.scope.expectedEpoch, items: [], omitted: true }); });
    }, 150);
    return () => { clearTimeout(timer); abort.abort(); };
    // Locale/chrome renders do not refresh a query or reissue metadata reads.
  }, [review, query, failed, interaction]);
  const presentation = <ProjectReferencePresentation text={text} input={input} scope={scope} />;
  return <>{compact ? <details className="reference-metadata-disclosure"><summary aria-label={t("Project references")} title={t("Project references")}><AppIcon name="folder" size={16} /></summary>{presentation}</details> : presentation}
    {!scope && !compact && <p role="status">{t("@ search requires an owned, verified project. References resolve only on normal Send after creation and transfer; file contents are not uploaded.")}</p>}
    {review && <dialog ref={dialog} className="app-dialog reference-palette" aria-modal="true" aria-labelledby={`${listId}-title`}
      onClose={() => { if (active.current === review) finish(); }} onCancel={event => { event.preventDefault(); if (!composing.current) finish(); }}
      onCompositionStart={() => { composing.current = true; abortRead(); setPage(null); }}
      onCompositionEnd={() => { composing.current = false; setInteraction(value => value + 1); }}
      onKeyDown={event => {
        event.stopPropagation();
        const action = referencePopupKey({ ...event, isComposing: composing.current || event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode });
        if (event.key === "Enter" && action === "choose" && event.target !== search.current
          && !(event.target as HTMLElement).closest('[role="option"]')) return;
        if (event.key === "Enter" || event.key === "Escape" || action === "next" || action === "previous") event.preventDefault();
        if (action === "cancel") finish();
        else if ((action === "next" || action === "previous") && page?.items.length) setSelected(value => (value + (action === "next" ? 1 : -1) + page.items.length) % page.items.length);
        else if (action === "choose") choose(selected);
      }}>
      <header><h2 id={`${listId}-title`}>{t("Project references")}</h2><button type="button" onClick={() => finish()}>{t("Close references")}</button></header>
      <p className="reference-context"><code>{review.scope.projectPath}</code> · <code>{review.scope.sessionId ?? t("Prompt draft")}</code></p>
      <label>{t("Project file matches")}<input ref={search} type="search" maxLength={256} value={query} role="combobox" aria-expanded="true"
        aria-controls={listId} aria-activedescendant={page?.items[selected] ? `${listId}-${selected}` : undefined}
        onChange={event => { abortRead(); setPage(null); setQuery(event.target.value); }} /></label>
      <p role="status">{failed ? t("Reference source changed. Close and reopen from the original input.") : !page ? t("Searching bounded project metadata…")
        : <>{page.status}{page.omitted ? ` · ${t("results omitted")}` : ""}{page.status === "ok" && !page.items.length ? ` · ${t("No reference matches.")}` : ""}</>}</p>
      <div id={listId} role="listbox" aria-label={t("Project file matches")} className="reference-palette-list">
        {!failed && page?.items.map((row, index) => <button type="button" role="option" id={`${listId}-${index}`} key={row.path}
          aria-selected={index === selected} disabled={!["ok", "incomplete", "read_error"].includes(page.status)} className={row.directory ? "reference-folder" : "reference-file"}
          onFocus={() => setSelected(index)} onClick={() => choose(index)}>
          <AppIcon name={row.directory ? "folder" : "file"} size={16} /><span className="reference-path">{row.path}{row.directory ? "/" : ""}</span>
          {row.recent && <small>{t("Recent")}</small>}</button>)}
      </div>
      <p>{t("References resolve on normal Send; queue/steer remain literal.")}</p>
    </dialog>}
  </>;
}
