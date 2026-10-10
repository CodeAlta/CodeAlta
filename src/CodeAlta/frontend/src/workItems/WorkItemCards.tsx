import { useEffect, useId, useRef, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import { startChoices, startDetail, startIcon, startLabel, taskCategoryIcon, taskCategoryLabel, taskCategoryTone, workItemKey, workKindIcon,
  type WorkItem, type WorkStart } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";
import { useWorkCardsPlacement } from "./useWorkCardsPlacement";

/** The buttons that start the work of an item: the way the user prefers first, the others beside it. */
export function WorkStartButtons({ preferred, here, disabled, compact = false, onStart }: {
  preferred: string;
  /** A session shows the item: it can do the work itself. */
  here: boolean;
  disabled: boolean; compact?: boolean; onStart: (start: WorkStart) => void;
}) {
  const { t } = useShellLanguage();
  const [first, ...others] = startChoices(preferred, here);
  return <div className="work-starts" data-compact={compact || undefined}>
    <Button className="work-start-primary" intent="primary" disabled={disabled} icon={<AppIcon name={startIcon(first)} size={15} />} title={t(startDetail(first))}
      onClick={() => onStart(first)}>{t(startLabel(first))}</Button>
    <div className="work-start-others">
      {others.map(start => <Button key={start} variant="outlined" disabled={disabled} icon={<AppIcon name={startIcon(start)} size={14} />} title={t(startDetail(start))}
        onClick={() => onStart(start)}>{t(startLabel(start))}</Button>)}
    </div>
  </div>;
}

/** The text of an item, read when it is shown. */
export function WorkItemText({ hub, item }: { hub: Pick<WorkItemsHub, "read">; item: WorkItem }) {
  const { t } = useShellLanguage();
  const [text, setText] = useState<{ key: string; markdown: string | null; truncated: boolean } | null>(null);
  const key = workItemKey(item);
  // The status is part of what is read again: the file changed when it did.
  useEffect(() => {
    const controller = new AbortController();
    void hub.read(item, controller.signal).then(value => {
      if (!controller.signal.aborted) setText({ key, markdown: value?.markdown ?? null, truncated: value?.truncated ?? false });
    });
    return () => controller.abort();
  }, [hub, key, item.status]);
  if (text?.key !== key) return <p className="work-text-state"><ActivitySpinner size={16} /></p>;
  if (text.markdown === null) return <p className="work-text-state">{t("The file could not be read.")}</p>;
  return <div className="work-text">
    <MarkdownContent source={text.markdown} document />
    {text.truncated && <p className="work-text-more">{t("The file is longer than what is shown here. Open it in the editor to read the rest.")}</p>}
  </div>;
}

/** The tags that say what an item is: why a task exists, and where it comes from. */
export function WorkItemTags({ item, projectName }: { item: WorkItem; projectName?: string | null }) {
  const { t } = useShellLanguage();
  return <span className="work-tags">
    {item.kind === "task" && <Tag minimal round intent={taskCategoryTone(item.category)} icon={<AppIcon name={taskCategoryIcon(item.category)} size={12} />}>{t(taskCategoryLabel(item.category))}</Tag>}
    {projectName && <Tag minimal round icon={<AppIcon name="folder" size={12} />}>{projectName}</Tag>}
    {item.created && <span className="work-date">{item.created}</span>}
  </span>;
}

/**
 * What a session proposes to the user, as cards over its top right corner: the follow-up tasks it found, and
 * the plan it had approved. One card is shown at a time, with how many there are; each says what it is in a
 * few lines, opens its full text, and is decided there: started (in a new worktree, in this session, in a new
 * session), kept for later, or dismissed.
 */
export function WorkItemCards({ hub, items, preferredStart, busy, hidden = false, onStart, onOpenList }: {
  hub: WorkItemsHub;
  /** The items this session shows, in the order of the cards. */
  items: readonly WorkItem[];
  preferredStart: string;
  /** The keys of the items an action is running on. */
  busy: ReadonlySet<string>;
  /** Something else has the place (a file under review). */
  hidden?: boolean;
  onStart: (item: WorkItem, start: WorkStart) => void;
  /** Opens the Work items tab on an item. */
  onOpenList: (item: WorkItem) => void;
}) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const [selected, setSelected] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState(false);
  const [detail, setDetail] = useState(false);
  const [cards, setCards] = useState<HTMLElement | null>(null);
  useWorkCardsPlacement(cards, collapsed);
  const known = useRef<ReadonlySet<string>>(new Set());
  // A proposal that just arrived is the one shown, and brings the cards back when they were put away.
  useEffect(() => {
    const keys = new Set(items.map(workItemKey));
    const arrived = items.find(item => !known.current.has(workItemKey(item)));
    if (arrived && known.current.size > 0) { setSelected(workItemKey(arrived)); setCollapsed(false); }
    known.current = keys;
  }, [items]);
  if (items.length === 0 || hidden) return null;
  const index = Math.max(0, items.findIndex(item => workItemKey(item) === selected));
  const item = items[index];
  const key = workItemKey(item);
  const working = busy.has(key);
  const go = (delta: number) => setSelected(workItemKey(items[(index + delta + items.length) % items.length]));
  const plan = item.kind === "plan";
  const later = () => void hub.act(item, plan ? "acknowledge" : "later");
  const dismiss = () => void hub.act(item, "dismiss");
  const pager = items.length > 1 && <span className="work-pager">
    <Button variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={14} />} aria-label={t("Previous")} title={t("Previous")} onClick={() => go(-1)} />
    <span aria-live="polite">{t("{index} of {count}", { index: index + 1, count: items.length })}</span>
    <Button variant="minimal" size="small" icon={<AppIcon name="chevronRight" size={14} />} aria-label={t("Next")} title={t("Next")} onClick={() => go(1)} />
  </span>;
  const decisions = <>
    <Button variant="minimal" size="small" disabled={working} icon={<AppIcon name="later" size={14} />} title={t(plan ? "Keep the plan for later. It stays in Work items." : "Keep the task for later. It stays in Work items.")}
      onClick={later}>{t("Later")}</Button>
    {!plan && <Button variant="minimal" size="small" disabled={working} icon={<AppIcon name="close" size={14} />} title={t("This task is not worth doing.")} onClick={dismiss}>{t("Dismiss")}</Button>}
  </>;

  return <aside ref={setCards} className="work-cards" data-collapsed={collapsed || undefined} aria-label={t("Proposed work items")}>
    {collapsed
      ? <button type="button" className="work-cards-chip" title={t("Show the proposed work items")} onClick={() => setCollapsed(false)}>
          <AppIcon name="task" size={14} />{t(items.length === 1 ? "{count} proposal" : "{count} proposals", { count: items.length })}<AppIcon name="chevronDown" size={14} /></button>
      : <section className="work-card" data-kind={item.kind} aria-labelledby={titleId}>
          <header>
            <span className="work-card-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(plan ? "Plan ready" : "Proposed task")}</span>
            {pager}
            <Button variant="minimal" size="small" icon={<AppIcon name="minus" size={14} />} aria-label={t("Put away")} title={t("Put the proposals away")} onClick={() => setCollapsed(true)} />
          </header>
          <h3 id={titleId}>{item.title}</h3>
          <WorkItemTags item={item} />
          {item.summary && <p className="work-card-summary">{item.summary}</p>}
          <button type="button" className="work-card-more" onClick={() => setDetail(true)}>{t(plan ? "Read the plan" : "Details")}<AppIcon name="chevronRight" size={13} /></button>
          <WorkStartButtons preferred={preferredStart} here disabled={working} compact onStart={start => onStart(item, start)} />
          <footer>{working && <ActivitySpinner size={13} />}{decisions}</footer>
        </section>}
    {detail && <AppWindow storageKey="codealta.desktop.work-item-window.v1" title={item.title} titleId={`${titleId}-window`} className="work-item-window"
      preferredSize={viewport => ({ width: Math.min(860, viewport.width - 80), height: Math.min(720, viewport.height - 80) })}
      onClose={() => setDetail(false)} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); setDetail(false); }}
      headerActions={pager}>
      <div className="work-window">
        <div className="work-window-meta">
          <span className="work-card-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(plan ? "Plan ready" : "Proposed task")}</span>
          <WorkItemTags item={item} />
          <code title={item.file}>{item.file}</code>
        </div>
        <div className="work-window-text">{item.summary && <p className="work-window-summary">{item.summary}</p>}<WorkItemText hub={hub} item={item} /></div>
        <footer className="work-window-actions">
          <WorkStartButtons preferred={preferredStart} here disabled={working} onStart={start => { setDetail(false); onStart(item, start); }} />
          <span className="work-window-decisions">
            {decisions}
            <Button variant="minimal" size="small" icon={<AppIcon name="list" size={14} />} onClick={() => { setDetail(false); onOpenList(item); }}>{t("Show in Work items")}</Button>
          </span>
        </footer>
      </div>
    </AppWindow>}
  </aside>;
}
