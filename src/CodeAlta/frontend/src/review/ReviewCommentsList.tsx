import { useRef, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { filterThreads, groupThreads, listedThreads, neighbourThread, reviewFilters, threadChip, threadCounts, wholeFile, type ReviewChip, type ReviewFilter, type ReviewThread } from "./reviewThreads";

const filterLabels: Readonly<Record<ReviewFilter, MessageKey>> = { active: "Not resolved", all: "All", draft: "Drafts", open: "Sent", addressed: "Addressed",
  resolved: "Resolved", outdated: "Outdated" };
const chipIcons: Readonly<Record<ReviewChip, IconName>> = { draft: "edit", sent: "send", addressed: "assistant", resolved: "check", outdated: "warning" };
const chipLabels: Readonly<Record<ReviewChip, MessageKey>> = { draft: "draft", sent: "sent", addressed: "addressed", resolved: "resolved", outdated: "outdated" };

/** "L5", "L15–18", "File"; "old" after the lines of the old side of a diff. */
function shortLines(thread: ReviewThread, old: string, file: string): string {
  const lines = wholeFile(thread) ? file : thread.startLine === thread.endLine ? `L${thread.startLine}` : `L${thread.startLine}–${thread.endLine}`;
  return thread.side === "old" && thread.target !== "file" ? `${lines} ${old}` : lines;
}
const firstLine = (text: string) => text.split(/\r\n|\r|\n/u).find(line => line.trim())?.trim() ?? "";

/**
 * Every thread of a review, grouped by file, with a filter by state; outdated threads come after the files. A
 * click reveals the thread where it is drawn; `n` and `p` go to the next and previous thread across files, and
 * reveal it too. An addressed thread shows the start of the agent's last reply.
 */
export function ReviewCommentsList({ threads, filter, onFilter, onReveal, selected = null, showPaths = true }: {
  threads: readonly ReviewThread[];
  filter: ReviewFilter;
  onFilter: (filter: ReviewFilter) => void;
  onReveal: (thread: ReviewThread) => void;
  /** The thread shown last, which `n` and `p` start from. */
  selected?: string | null;
  /** Whether files are named: a review of one file needs no heading. */
  showPaths?: boolean;
}) {
  const { t } = useShellLanguage();
  const list = useRef<HTMLDivElement>(null);
  const shown = filterThreads(threads, filter);
  const groups = groupThreads(shown);
  const order = listedThreads(shown);
  const counts = threadCounts(threads);
  const go = (thread: ReviewThread | null) => {
    if (!thread) return;
    list.current?.querySelector<HTMLElement>(`[data-thread-row="${CSS.escape(thread.id)}"]`)?.focus();
    onReveal(thread);
  };
  const keys = (event: ReactKeyboardEvent) => {
    if (event.ctrlKey || event.metaKey || event.altKey || event.target instanceof HTMLSelectElement) return;
    const key = event.key.toLowerCase();
    if (key !== "n" && key !== "p" && key !== "arrowdown" && key !== "arrowup") return;
    const row = (event.target as HTMLElement).closest<HTMLElement>("[data-thread-row]")?.dataset.threadRow ?? selected;
    go(neighbourThread(order, { id: row }, key === "n" || key === "arrowdown" ? 1 : -1));
    event.preventDefault(); event.stopPropagation();
  };
  const row = (thread: ReviewThread) => {
    const chip = threadChip(thread);
    const reply = thread.state === "addressed" ? [...thread.replies].reverse().find(item => item.author === "agent") : undefined;
    return <li key={thread.id}><button type="button" className="review-list-row" data-thread-row={thread.id} data-chip={chip} aria-current={thread.id === selected || undefined}
      onClick={() => onReveal(thread)}>
      <AppIcon name={chipIcons[chip]} size={12} />
      <span className="review-list-lines">{shortLines(thread, t("old"), t("File"))}</span>
      <span className="review-list-text">{firstLine(thread.body) || (thread.suggestion !== null ? t(thread.suggestion === "" ? "Suggestion: delete these lines" : "Suggested change") : "")}
        {reply && <small> · “{firstLine(reply.body)}”</small>}</span>
      <span className="review-chip" data-chip={chip}>{t(chipLabels[chip])}</span>
    </button></li>;
  };
  return <div className="review-list" ref={list} onKeyDown={keys}>
    <header className="review-list-head">
      <strong>{t("Comments")}</strong><span className="review-list-count">{counts.active}</span>
      <HTMLSelect className="review-list-filter" value={filter} aria-label={t("Show")} onChange={event => onFilter(event.target.value as ReviewFilter)}>
        {reviewFilters.map(value => <option key={value} value={value}>{t(filterLabels[value])}</option>)}
      </HTMLSelect>
    </header>
    {shown.length === 0 ? <p className="review-list-empty">{t(threads.length ? "No comment matches this filter." : "No comment yet.")}</p> : <>
      {groups.files.map(group => <section key={group.path} className="review-list-file" aria-label={group.path}>
        {showPaths && <h4 title={group.path}><AppIcon name="file" size={12} />{group.path}</h4>}
        <ul>{group.threads.map(row)}</ul>
      </section>)}
      {groups.outdated.length > 0 && <section className="review-list-file" data-outdated="" aria-label={t("Outdated")}>
        <h4><AppIcon name="warning" size={12} />{t("Outdated {count}", { count: groups.outdated.length })}</h4>
        <ul>{groups.outdated.map(row)}</ul>
      </section>}
    </>}
  </div>;
}
