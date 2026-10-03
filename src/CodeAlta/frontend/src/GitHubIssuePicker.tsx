import { useContext, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { Checkbox, InputGroup } from "@blueprintjs/core";
import { githubIssues, type GitHubIssuesSearchResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { activeIssueReference, insertIssueReference } from "./issueReferences";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { usePromptPicker } from "./promptPicker";
import { sessionTime } from "./sessionTime";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

const pageStep = 8;
const issueLimit = 50;

/**
 * The `#` issue picker of a prompt editor. Typing `#` at a word start opens a search window listing the
 * issues of the project's GitHub repository (most recently updated first); Enter replaces the `#query`
 * with a Markdown link to the selected issue, Escape leaves the text as typed.
 */
export function GitHubIssuePicker({ edit, input }: { edit: (text: string) => void; input: RefObject<PromptInput | null> }) {
  const { t, locale } = useShellLanguage();
  const scope = useContext(ProjectReferenceContext);
  const latest = useRef({ scope }); latest.current = { scope };
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  const [includeClosed, setIncludeClosed] = useState(true);
  const [page, setPage] = useState<GitHubIssuesSearchResponse | null>(null);
  const [failed, setFailed] = useState(false);
  const { trigger, dialog, close } = usePromptPicker({ input, edit, enabled: !!scope, detect: activeIssueReference, focus: search,
    onOpen: value => { setQuery(value.query); setSelected(0); setPage(null); setFailed(false); } });
  const open = !!trigger;

  useEffect(() => {
    const current = latest.current.scope;
    if (!open || !current) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      void githubIssues.search({ expectedEpoch: current.expectedEpoch, projectId: current.projectId, query, limit: issueLimit },
        { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(value => {
        if (controller.signal.aborted) return;
        setFailed(false); setPage(value); setSelected(0);
      }).catch(() => { if (!controller.signal.aborted) { setFailed(true); setPage(null); } });
    }, page ? 220 : 0);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [open, query]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, page]);

  const issues = page?.status === "ok" ? page.issues.filter(issue => includeClosed || issue.state.toLowerCase() === "open") : [];
  const count = issues.length;
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (delta: number) => setSelected(Math.max(0, Math.min(count - 1, index + delta)));
  function choose(at: number) {
    const issue = issues[at];
    const element = input.current;
    if (!issue || !trigger || !element || element.value !== trigger.text) return;
    const next = insertIssueReference(trigger.text, trigger.start, trigger.end, issue.number, issue.url);
    if (next) close(next);
  }
  const problem = failed ? t("GitHub issues could not be loaded.")
    : !page || page.status === "ok" ? null
    : page.status === "not_github" ? t("This project has no GitHub repository.")
    : page.message ?? t("GitHub issues could not be loaded.");
  const status = problem ? null : !page ? t("Loading issues…") : count === 1 ? t("1 issue") : t("{count} issues", { count });
  const now = Date.now();
  return trigger && <dialog ref={dialog} className="app-dialog reference-palette" aria-modal="true" aria-labelledby={`${listId}-title`}
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
        : event.key === "Enter" ? (choose(index), true) : event.key === "Escape" ? (close(), true) : false;
      if (handled) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.issues.v1" titleId={`${listId}-title`}
      title={<><AppIcon name="issueOpen" size={14} /> {t("GitHub issues")}{page?.repository && <span className="reference-project"> · {page.repository}</span>}</>}
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
      {count > 0 && <div className="issue-head" aria-hidden="true"><span /><span>{t("Issue")}</span><span>{t("Title")}</span><span>{t("State")}</span><span>{t("Updated")}</span></div>}
      <div id={listId} ref={list} role="listbox" aria-label={t("GitHub issues")} className="reference-list">
        {issues.map((issue, at) => { const closed = issue.state.toLowerCase() !== "open"; const updated = sessionTime(issue.updatedAt, locale, now);
          return <div role="option" id={`${listId}-${at}`} key={issue.number} aria-selected={at === index} className="issue-row" title={issue.url}
            onMouseMove={() => { if (at !== index) setSelected(at); }} onClick={() => choose(at)}>
            <span className="issue-icon" data-file-tone={closed ? "purple" : "green"}><AppIcon name={closed ? "issueClosed" : "issueOpen"} size={16} /></span>
            <span className="issue-number">#{issue.number}</span>
            <span className="issue-title">{issue.title}</span>
            <span className="issue-state">{issue.state}</span>
            <span className="issue-updated" title={updated.title}>{updated.label}</span>
          </div>; })}
        {problem && <p className="reference-empty">{problem}</p>}
        {!problem && page && count === 0 && <p className="reference-empty">{t("No issue matches.")}</p>}
      </div>
      <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert link")}</span>
        <span><kbd>Ctrl</kbd><kbd>I</kbd> {t("closed issues")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
    </AppWindowSurface>
  </dialog>;
}
