import { useContext, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { Button, Checkbox, InputGroup } from "@blueprintjs/core";
import { keptOnOutsidePress } from "./modalDialogs";
import { gitIssues, type GitIssuesSearchResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { BrandIcon } from "./BrandIcon";
import { serviceBrand } from "./brands";
import { AppWindowSurface } from "./AppWindow";
import { activeIssueReference, insertIssueReferences } from "./issueReferences";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { usePromptPicker } from "./promptPicker";
import { sessionTime } from "./sessionTime";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

const pageStep = 8;
const issueLimit = 50;

/**
 * The `#` issue picker of a prompt editor: a search window listing the issues of the project's hosted repository
 * (GitHub or GitLab issues, Azure DevOps work items; most recently updated first).
 * - Typing `#` at a word start opens it to insert: a click on an issue, or Enter, replaces the `#query` with a Markdown
 *   link to it. Escape leaves the text as typed.
 * - **Add issues** of the attach menu (`openRequest`) opens it to attach several: a click on an issue, its box or
 *   Shift+Enter checks it, and **Attach** (Enter) gives the checked issues, or the selected one, to `onAttach`. Checked
 *   issues survive a press outside the window; Cancel (Escape) drops them.
 */
export function IssuePicker({ edit, input, openRequest = 0, onAttach }: { edit: (text: string) => void; input: RefObject<PromptInput | null>;
  /** Opens the picker for `onAttach` each time it changes from what it was: Add issues of the attach menu. */
  openRequest?: number;
  /** Takes the issues chosen in a picker opened by `openRequest`: they become attachments, the text is left as it is. */
  onAttach?: (issues: readonly Readonly<{ number: number; url: string; title: string }>[]) => void }) {
  const { t, locale } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const latest = useRef({ scope }); latest.current = { scope };
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [includeClosed, setIncludeClosed] = useState(true);
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

  useEffect(() => {
    const current = latest.current.scope;
    if (!open || !current) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void gitIssues.search({ expectedEpoch: current.expectedEpoch, projectId: current.projectId, query, limit: issueLimit },
        { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(value => {
        if (controller.signal.aborted) return;
        setFailed(false); setPage(value); setSelected(0);
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, page ? 220 : 0);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [open, query]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, page]);

  const issues = page?.status === "ok" ? page.issues.filter(issue => includeClosed || issue.open) : [];
  const count = issues.length;
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (delta: number) => setSelected(Math.max(0, Math.min(count - 1, index + delta)));
  const key = (issue: { kind: string; number: number }) => `${issue.kind}:${issue.number}`;
  function toggle(at: number) {
    const issue = issues[at];
    if (!issue) return;
    setChecked(current => {
      const next = new Map(current);
      if (!next.delete(key(issue))) next.set(key(issue), { number: issue.number, url: issue.url, title: issue.title });
      return next;
    });
  }
  // The checked issues, or the one at `at` when none is checked.
  function choose(at: number) {
    const chosen = checked.size ? [...checked.values()] : issues[at] ? [issues[at]] : [];
    const element = input.current;
    if (!chosen.length || !trigger) return;
    if (attaching && onAttach) { onAttach(chosen); close(); return; }
    if (!element || element.value !== trigger.text) return;
    const next = insertIssueReferences(trigger.text, trigger.start, trigger.end, chosen);
    if (next) close(next);
  }
  const problem = failed ? t("Issues could not be loaded.")
    : !page || page.status === "ok" ? null
    : page.status === "no_repository" ? t("This project has no GitHub, GitLab or Azure DevOps repository.")
    : page.message ?? t("Issues could not be loaded.");
  const heading = page?.provider === "github" ? t("GitHub issues") : page?.provider === "gitlab" ? t("GitLab issues")
    : page?.provider === "azure_devops" ? t("Azure DevOps work items") : t("Issues");
  const status = problem ? null : !page ? t("Loading issues…") : checked.size ? t("{count} checked", { count: checked.size })
    : count === 1 ? t("1 issue") : t("{count} issues", { count });
  const now = Date.now();
  // What Enter does, on the button that does it: attach or insert the checked issues, or the selected one.
  const chosenCount = checked.size || (issues[index] ? 1 : 0);
  const action = checked.size > 1 ? t("Attach {count} issues", { count: checked.size }) : t("Attach issue");
  // Checked issues are kept through a press outside the window: only Cancel, Escape or the close button drop them.
  return trigger && <dialog ref={dialog} className="app-dialog reference-palette" data-attaching={attaching || undefined} aria-modal="true" aria-labelledby={`${listId}-title`}
    {...(attaching && checked.size ? keptOnOutsidePress : {})}
    onCancel={event => { event.preventDefault(); close(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.metaKey) return;
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
      title={<>{serviceBrand(page?.provider) ? <BrandIcon name={serviceBrand(page?.provider)!} size={14} /> : <AppIcon name="issueOpen" size={14} />} {heading}{page?.repository && <span className="reference-project"> · {page.repository}</span>}</>}
      preferredSize={viewport => ({ width: Math.min(860, viewport.width - 40), height: Math.min(520, viewport.height - 40) })}
      minimumSize={{ width: 440, height: 260 }} onClose={() => close()} closeLabel={t("Close")}
      headerActions={status && <span className="reference-status" role="status">{!page && <ActivitySpinner size={12} />}{status}</span>}>
      <div className="issue-filter">
        <InputGroup inputRef={search} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
          leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={t("Search issues by number or title…")}
          role="combobox" aria-expanded="true" aria-controls={listId} aria-label={t("Search issues by number or title…")}
          aria-activedescendant={issues[index] ? `${listId}-${index}` : undefined}
          onChange={event => { setQuery(event.target.value); setSelected(0); }} />
        <Checkbox checked={includeClosed} label={t("Include closed")} onChange={event => { setIncludeClosed(event.currentTarget.checked); setSelected(0); }} />
      </div>
      {count > 0 && <div className="issue-head" aria-hidden="true">{attaching && <span />}<span /><span>{t("Issue")}</span><span>{t("Title")}</span><span>{t("State")}</span><span>{t("Updated")}</span></div>}
      <div id={listId} ref={list} role="listbox" aria-label={heading} className="reference-list">
        {issues.map((issue, at) => { const closed = !issue.open; const updated = sessionTime(issue.updatedAt, locale, now);
          return <div role="option" id={`${listId}-${at}`} key={key(issue)} aria-selected={at === index} aria-checked={checked.has(key(issue))} className="issue-row" title={issue.url}
            onMouseMove={() => { if (at !== index) setSelected(at); }} onClick={() => attaching ? toggle(at) : choose(at)}>
            {/* The box toggles through its change, once: a click on its label also clicks its input, and the row's click is stopped. */}
            {attaching && <span className="issue-check" onClick={event => event.stopPropagation()}>
              <Checkbox checked={checked.has(key(issue))} aria-label={t("Check #{number}", { number: issue.number })} tabIndex={-1}
                onChange={() => { setSelected(at); toggle(at); }} /></span>}
            <span className="issue-icon" data-file-tone={closed ? "purple" : "green"} title={issue.kind === "pull_request" ? t("Pull request") : undefined}>
              <AppIcon name={issue.kind === "pull_request" ? "pullRequest" : closed ? "issueClosed" : "issueOpen"} size={16} /></span>
            <span className="issue-number">#{issue.number}</span>
            <span className="issue-title">{issue.title}</span>
            <span className="issue-state">{issue.state}</span>
            <span className="issue-updated" title={updated.title}>{updated.label}</span>
          </div>; })}
        {problem && <p className="reference-empty">{problem}</p>}
        {!problem && page && count === 0 && <p className="reference-empty">{t("No issue matches.")}</p>}
      </div>
      {attaching ? <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Shift</kbd><kbd>Enter</kbd> {t("check")}</span>
        <span><kbd>Ctrl</kbd><kbd>I</kbd> {t("closed issues")}</span>
        <span className="reference-actions">
          <Button variant="minimal" size="small" onClick={() => close()} endIcon={<kbd>Esc</kbd>}>{t("Cancel")}</Button>
          <Button intent="primary" size="small" disabled={chosenCount === 0} onClick={() => choose(index)} endIcon={<kbd>Enter</kbd>}>{action}</Button>
        </span></footer>
        : <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert link")}</span>
          <span><kbd>Ctrl</kbd><kbd>I</kbd> {t("closed issues")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>}
    </AppWindowSurface>
  </dialog>;
}
