import { useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import { Button, Checkbox, InputGroup } from "@blueprintjs/core";
import { keptOnOutsidePress } from "./modalDialogs";
import { gitIssues, type GitIssuesSearchResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { BrandIcon } from "./BrandIcon";
import { serviceBrand } from "./brands";
import { AppWindowSurface } from "./AppWindow";
import { activeIssueReference } from "./issueReferences";
import type { MessageKey } from "./localization";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { usePromptPicker } from "./promptPicker";
import { defaultReferenceKinds, insertReferences, issueReference, referenceKindOfKey, referenceKinds, referenceRows, ReferenceSourcesContext,
  sessionReference, toggledKinds, workReference, type ReferenceItem, type ReferenceKind } from "./referenceItems";
import { sessionTime } from "./sessionTime";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

const pageStep = 8;
const issueLimit = 50;

const kindLabels: Readonly<Record<ReferenceKind, MessageKey>> = { issue: "Issues", task: "Tasks", plan: "Plans", session: "Sessions" };
const kindNames: Readonly<Record<ReferenceKind, MessageKey>> = { issue: "Issue", task: "Task", plan: "Plan", session: "Session" };
const kindIcons = { issue: "issueOpen", task: "task", plan: "plan", session: "chat" } as const;
const isMac = typeof navigator !== "undefined" && /Mac/u.test(navigator.platform);
const control = isMac ? "⌘" : "Ctrl";

// The state of a row, as the user reads it.
function stateKey(item: ReferenceItem): MessageKey | null {
  if (item.kind === "task") return item.state === "done" ? "Done" : item.state === "dismissed" ? "Dismissed" : item.state === "later" ? "Backlog" : "Inbox";
  if (item.kind === "plan") return item.state === "done" ? "Done" : item.state === "blocked" ? "Blocked" : item.state === "in-progress" ? "In progress"
    : item.state === "approved" ? "Ready" : "Draft";
  if (item.kind === "session") return item.state === "running" ? "Running" : "Idle";
  return null;
}

/**
 * The `#` picker of a prompt editor: a search window that lists, the most recent first, the issues of the project's
 * hosted repository (GitHub or GitLab issues, Azure DevOps work items), its tasks and plans, and, when turned on, its
 * sessions. A toggle for each kind, or Ctrl+1 to Ctrl+4, shows or hides it; with Shift (or Alt+click) it shows that
 * kind alone.
 * - Typing `#` at a word start opens it to insert: a click on a row, or Enter, replaces the `#query` with the reference:
 *   a link to an issue, the short id of a task in code, a link to the file of a plan, a `session:` link. Escape leaves the
 *   text as typed.
 * - **Add issues** of the attach menu (`openRequest`) opens it on the issues alone, to attach several: a click on an
 *   issue, its box or Shift+Enter checks it, and **Attach** (Enter) gives the checked issues, or the selected one, to
 *   `onAttach`. Checked issues survive a press outside the window; Cancel (Escape) drops them.
 */
export function ReferencePicker({ edit, input, openRequest = 0, onAttach }: { edit: (text: string) => void; input: RefObject<PromptInput | null>;
  /** Opens the picker for `onAttach` each time it changes from what it was: Add issues of the attach menu. */
  openRequest?: number;
  /** Takes the issues chosen in a picker opened by `openRequest`: they become attachments, the text is left as it is. */
  onAttach?: (issues: readonly Readonly<{ number: number; url: string; title: string }>[]) => void }) {
  const { t, locale } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const sources = useContext(ReferenceSourcesContext);
  const latest = useRef({ scope }); latest.current = { scope };
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [includeClosed, setIncludeClosed] = useState(true);
  // The kinds shown: they stay as they were left from one `#` to the next while the prompt is there.
  const [kinds, setKinds] = useState<ReadonlySet<ReferenceKind>>(defaultReferenceKinds);
  const [page, setPage] = useState<GitIssuesSearchResponse | null>(null);
  const [failed, setFailed] = useState(false);
  // The checked issues, in the order they were checked: they stay checked across searches.
  const [checked, setChecked] = useState<ReadonlyMap<string, Readonly<{ number: number; url: string; title: string }>>>(new Map());
  // Opened by the attach menu: the chosen issues go to `onAttach`, not into the text.
  const [attaching, setAttaching] = useState(false);
  const { trigger, dialog, close, openAt } = usePromptPicker({ input, edit, enabled: !!scope, detect: activeIssueReference, focus: search,
    onOpen: value => { setQuery(value.query); setSelected(0); setPage(null); setFailed(false); setChecked(new Map()); } });
  const open = !!trigger;
  // Only a request made while the picker is mounted: one it mounts with (the composer hid it during a Send) is already handled.
  const handledRequest = useRef(openRequest);
  useEffect(() => {
    if (openRequest === handledRequest.current) return;
    handledRequest.current = openRequest;
    if (onAttach) setAttaching(openAt());
  }, [openRequest]);
  useEffect(() => { if (!open) setAttaching(false); }, [open]);
  // What is shown: the issues alone when attaching.
  const shown: ReadonlySet<ReferenceKind> = attaching || !sources ? issueOnly : kinds;
  const wantsIssues = shown.has("issue");

  useEffect(() => {
    const current = latest.current.scope;
    if (!open || !current || !wantsIssues) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void gitIssues.search({ expectedEpoch: current.expectedEpoch, projectId: current.projectId, query: query.replace(/^#/u, ""), limit: issueLimit },
        { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(value => {
        if (controller.signal.aborted) return;
        setFailed(false); setPage(value); setSelected(0);
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, page ? 220 : 0);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [open, query, wantsIssues]);

  // The tasks, the plans and the sessions of the project, read when the picker opens.
  const local = useMemo(() => {
    if (!open || !scope || !sources) return [];
    const items: ReferenceItem[] = [];
    for (const item of sources.workItems(scope.projectId)) { const row = workReference(item); if (row) items.push(row); }
    for (const session of sources.sessions(scope.projectId)) {
      if (session.id === scope.sessionId) continue;
      const row = sessionReference(session); if (row) items.push(row);
    }
    return items;
  }, [open, scope?.projectId, scope?.sessionId, sources]);
  const issueItems = useMemo(() => page?.status === "ok" ? page.issues.map(issueReference).filter((item): item is ReferenceItem => item !== null) : [],
    [page]);
  const all = useMemo(() => [...issueItems, ...local], [issueItems, local]);
  const rows = useMemo(() => referenceRows(all, shown, includeClosed, query), [all, shown, includeClosed, query]);
  // How many rows each kind would add, for its toggle.
  const counts = useMemo(() => {
    const result = new Map<ReferenceKind, number>();
    for (const item of referenceRows(all, new Set(referenceKinds), includeClosed, query)) result.set(item.kind, (result.get(item.kind) ?? 0) + 1);
    return result;
  }, [all, includeClosed, query]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, rows]);

  const count = rows.length;
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (delta: number) => setSelected(Math.max(0, Math.min(count - 1, index + delta)));
  function toggleKind(kind: ReferenceKind, alone: boolean) {
    setKinds(current => toggledKinds(current, kind, alone));
    setSelected(0);
  }
  function toggle(at: number) {
    const issue = rows[at]?.issue;
    if (!issue) return;
    const key = rows[at].key;
    setChecked(current => {
      const next = new Map(current);
      if (!next.delete(key)) next.set(key, issue);
      return next;
    });
  }
  // The checked issues, or the row at `at` when none is checked.
  function choose(at: number) {
    const element = input.current;
    if (!trigger) return;
    // Closed first: the prompt of a new session refuses attachments while a modal window is open.
    if (attaching && onAttach) {
      const chosen = checked.size ? [...checked.values()] : rows[at]?.issue ? [rows[at].issue!] : [];
      if (chosen.length) { close(); onAttach(chosen); }
      return;
    }
    const row = rows[at];
    if (!row || !element || element.value !== trigger.text) return;
    const next = insertReferences(trigger.text, trigger.start, trigger.end, [row.insert]);
    if (next) close(next);
  }
  const issueProblem = !wantsIssues ? null : failed ? t("Issues could not be loaded.")
    : !page || page.status === "ok" ? null
    : page.status === "no_repository" ? t("This project has no GitHub, GitLab or Azure DevOps repository.")
    : page.message ?? t("Issues could not be loaded.");
  const loading = wantsIssues && !page && !failed;
  const heading = attaching ? (page?.provider === "github" ? t("GitHub issues") : page?.provider === "gitlab" ? t("GitLab issues")
    : page?.provider === "azure_devops" ? t("Azure DevOps work items") : t("Issues")) : t("Insert a reference");
  const status = loading && !count ? t("Loading issues…") : checked.size ? t("{count} checked", { count: checked.size })
    : count === 1 ? t("1 reference") : t("{count} references", { count });
  const now = Date.now();
  // What Enter does, on the button that does it: attach the checked issues, or the selected one.
  const chosenCount = checked.size || (rows[index]?.issue ? 1 : 0);
  const action = checked.size > 1 ? t("Attach {count} issues", { count: checked.size }) : t("Attach issue");
  const searchLabel = attaching ? t("Search issues by number or title…") : t("Search issues, tasks, plans and sessions…");
  // Checked issues are kept through a press outside the window: only Cancel, Escape or the close button drop them.
  return trigger && <dialog ref={dialog} className="app-dialog reference-palette" data-attaching={attaching || undefined} aria-modal="true" aria-labelledby={`${listId}-title`}
    {...(attaching && checked.size ? keptOnOutsidePress : {})}
    onCancel={event => { event.preventDefault(); close(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      const kind = !attaching && sources ? referenceKindOfKey(event.nativeEvent) : null;
      if (kind) { event.preventDefault(); toggleKind(kind, event.shiftKey); return; }
      if (event.metaKey) return;
      if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "i") { event.preventDefault(); setIncludeClosed(value => !value); setSelected(0); return; }
      if (event.ctrlKey || event.altKey) return;
      const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
        : event.key === "Home" && event.target !== search.current ? (setSelected(0), true)
        : event.key === "End" && event.target !== search.current ? (setSelected(Math.max(0, count - 1)), true)
        : event.key === "Enter" && event.shiftKey && attaching ? (toggle(index), move(1), true)
        : event.key === "Enter" ? (choose(index), true) : event.key === "Escape" ? (close(), true) : false;
      if (handled) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.issues.v1" titleId={`${listId}-title`}
      title={<>{attaching && serviceBrand(page?.provider) ? <BrandIcon name={serviceBrand(page?.provider)!} size={14} /> : <AppIcon name={attaching ? "issueOpen" : "goto"} size={14} />} {heading}
        {page?.repository && wantsIssues && <span className="reference-project"> · {page.repository}</span>}</>}
      preferredSize={viewport => ({ width: Math.min(900, viewport.width - 40), height: Math.min(560, viewport.height - 40) })}
      minimumSize={{ width: 460, height: 280 }} onClose={() => close()} closeLabel={t("Close")}
      headerActions={status && <span className="reference-status" role="status">{loading && <ActivitySpinner size={12} />}{status}</span>}>
      <div className="issue-filter">
        <InputGroup inputRef={search} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
          leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={searchLabel}
          role="combobox" aria-expanded="true" aria-controls={listId} aria-label={searchLabel}
          aria-activedescendant={rows[index] ? `${listId}-${index}` : undefined}
          onChange={event => { setQuery(event.target.value); setSelected(0); }} />
        <Checkbox checked={includeClosed} label={t("Include closed")} onChange={event => { setIncludeClosed(event.currentTarget.checked); setSelected(0); }} />
      </div>
      {!attaching && sources && <div className="reference-kinds" role="group" aria-label={t("Show")}>
        {referenceKinds.map((kind, at) => {
          const on = kinds.has(kind);
          const tip = t("{kind}: {key} shows or hides them; with Shift, or Alt+click, them alone", { kind: t(kindLabels[kind]), key: `${control}+${at + 1}` });
          return <button key={kind} type="button" className="reference-kind" data-kind={kind} aria-pressed={on} title={tip} tabIndex={-1}
            onClick={event => toggleKind(kind, event.altKey || event.shiftKey)}>
            <AppIcon name={kindIcons[kind]} size={13} /><span>{t(kindLabels[kind])}</span>
            {on && counts.get(kind) ? <span className="reference-kind-count">{counts.get(kind)}</span> : null}
            <kbd>{isMac ? `⌘${at + 1}` : `Ctrl+${at + 1}`}</kbd></button>;
        })}
      </div>}
      {count > 0 && <div className="issue-head" aria-hidden="true">{attaching && <span />}<span /><span>{t("Reference")}</span><span>{t("Title")}</span><span>{t("State")}</span><span>{t("Updated")}</span></div>}
      <div id={listId} ref={list} role="listbox" aria-label={heading} className="reference-list">
        {rows.map((row, at) => {
          const updated = row.time ? sessionTime(new Date(row.time).toISOString(), locale, now) : null;
          const state = stateKey(row);
          return <div role="option" id={`${listId}-${at}`} key={row.key} aria-selected={at === index} aria-checked={checked.has(row.key)}
            className="issue-row reference-item" data-kind={row.kind} data-closed={row.closed || undefined} title={row.issue?.url}
            onMouseMove={() => { if (at !== index) setSelected(at); }} onClick={() => attaching ? toggle(at) : choose(at)}>
            {/* The box toggles through its change, once: a click on its label also clicks its input, and the row's click is stopped. */}
            {attaching && <span className="issue-check" onClick={event => event.stopPropagation()}>
              <Checkbox checked={checked.has(row.key)} aria-label={t("Check #{number}", { number: row.issue?.number ?? 0 })} tabIndex={-1}
                onChange={() => { setSelected(at); toggle(at); }} /></span>}
            <span className="issue-icon" data-tone={row.tone} title={t(kindNames[row.kind])}><AppIcon name={row.icon} size={16} /></span>
            <span className="issue-number">{row.label || <span className="reference-kind-name">{t(kindNames[row.kind])}</span>}</span>
            <span className="issue-title">{row.title}</span>
            <span className="issue-state">{state ? t(state) : row.state}</span>
            <span className="issue-updated" title={updated?.title}>{updated?.label ?? ""}</span>
          </div>; })}
        {!count && !loading && <p className="reference-empty">{query.trim() ? t("Nothing matches.") : t("Nothing to show.")}</p>}
        {issueProblem && <p className="reference-empty reference-note"><AppIcon name="issueOpen" size={13} />{issueProblem}</p>}
      </div>
      {attaching ? <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Shift</kbd><kbd>Enter</kbd> {t("check")}</span>
        <span><kbd>Ctrl</kbd><kbd>I</kbd> {t("closed issues")}</span>
        <span className="reference-actions">
          <Button variant="minimal" size="small" onClick={() => close()} endIcon={<kbd>Esc</kbd>}>{t("Cancel")}</Button>
          <Button intent="primary" size="small" disabled={chosenCount === 0} onClick={() => choose(index)} endIcon={<kbd>Enter</kbd>}>{action}</Button>
        </span></footer>
        : <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert")}</span>
          {sources && <span><kbd>{control}</kbd><kbd>1</kbd>–<kbd>4</kbd> {t("kinds")}</span>}
          <span><kbd>Ctrl</kbd><kbd>I</kbd> {t("closed")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>}
    </AppWindowSurface>
  </dialog>;
}

const issueOnly: ReadonlySet<ReferenceKind> = new Set<ReferenceKind>(["issue"]);
