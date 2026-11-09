import { useEffect, useId, useRef, useState, useSyncExternalStore, type PointerEvent as ReactPointerEvent } from "react";
import { createPortal } from "react-dom";
import { Button, Menu, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { MarkdownContent } from "../MarkdownContent";
import { TagChips } from "./WorkItemTagViews";
import { useShellLanguage } from "../shellLanguage";
import { startChoices, startDetail, startIcon, startLabel, taskCategoryIcon, taskCategoryLabel, taskCategoryTone, workItemKey, workItemTone, workKindIcon,
  type WorkItem, type WorkStart } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";
import { notesSurface, useWorkCardsPlacement } from "./useWorkCardsPlacement";
import { dockSides, dockTargetAt, dockTargets, type DockPreference, type DockSide, type PaneRect } from "./proposalsDock";
import type { MessageKey } from "../localization";
import type { ProposalsOverlayMemory } from "../sessionOverlayMemory";

const dockIcons: Readonly<Record<DockSide, IconName>> = Object.freeze({ left: "arrowLeft", right: "arrowRight", above: "arrowUp", below: "arrowDown" });
const dockLabels: Readonly<Record<DockSide, MessageKey>> = Object.freeze({ left: "Left of Notes", right: "Right of Notes", above: "Above Notes", below: "Below Notes" });

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
export function WorkItemTags({ item, projectName, shortId, tags }: { item: WorkItem; projectName?: string | null; /** Shows the short id after the date. */ shortId?: boolean;
  /** Shows the tags of the task, after the date. */ tags?: boolean }) {
  const { t } = useShellLanguage();
  return <span className="work-tags">
    {item.kind === "task" && <Tag minimal round intent={taskCategoryTone(item.category)} icon={<AppIcon name={taskCategoryIcon(item.category)} size={12} />}>{t(taskCategoryLabel(item.category))}</Tag>}
    {projectName && <Tag minimal round icon={<AppIcon name="folder" size={12} />}>{projectName}</Tag>}
    {/* The short id is in the line of the date, so that their texts share a baseline. */}
    {(item.created || shortId && item.shortId) && <span className="work-date">{item.created}
      {shortId && item.shortId && <code className="work-tag-id" data-tone={workItemTone(item)}>{item.shortId}</code>}</span>}
    {tags && <TagChips tags={item.tags} max={5} />}
  </span>;
}

/**
 * What a session proposes to the user, as cards over its top right corner: the follow-up tasks it found, and
 * the plan it had approved. One card is shown at a time, with how many there are; each says what it is in a
 * few lines, opens its full text, and is decided there: started (in a new worktree, in this session, in a new
 * session), kept for later, or dismissed. A task the session started itself says so, until the user puts it away.
 */
export function WorkItemCards({ hub, items, preferredStart, busy, hidden = false, memory, dockPreference, onStart, onOpenList }: {
  hub: WorkItemsHub;
  /** The items this session shows, in the order of the cards. */
  items: readonly WorkItem[];
  preferredStart: string;
  /** The keys of the items an action is running on. */
  busy: ReadonlySet<string>;
  /** Something else has the place (a file under review). */
  hidden?: boolean;
  /** What the cards of this session remember when the pane is mounted again (a switch of space). */
  memory?: ProposalsOverlayMemory;
  /** The side of Notes the user chose for the cards, owned by the window: the same for every pane. */
  dockPreference: DockPreference;
  onStart: (item: WorkItem, start: WorkStart) => void;
  /** Opens the Work items tab on an item. */
  onOpenList: (item: WorkItem) => void;
}) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const [selected, setSelected] = useState<string | null>(null);
  const [remembered] = useState<ProposalsOverlayMemory>(() => memory ?? { collapsed: false, known: null });
  const [collapsed, setCollapsed] = useState(remembered.collapsed);
  useEffect(() => { remembered.collapsed = collapsed; }, [remembered, collapsed]);
  const [detail, setDetail] = useState(false);
  const [cards, setCards] = useState<HTMLElement | null>(null);
  // The side of Notes the user chose for the cards, for the whole window; null leaves it to the room around Notes.
  const dock = useSyncExternalStore(dockPreference.subscribe, dockPreference.get, dockPreference.get);
  const chooseDock = dockPreference.set;
  useWorkCardsPlacement(cards, collapsed, dock);
  // Dragging the chip or the header of the card shows a cross of targets around Notes; dropping on one docks the cards there.
  const [guide, setGuide] = useState<{ targets: Record<DockSide, PaneRect>; over: DockSide | null } | null>(null);
  const drag = useRef<{ pointer: number; x: number; y: number; origin: { x: number; y: number }; targets: Record<DockSide, PaneRect> | null } | null>(null);
  const dragged = useRef(false);
  // A proposal that just arrived is the one shown, and brings the cards back when they were put away. What was
  // seen once stays known: the list is empty or partial for a moment after a switch of space mounts the pane
  // again, and the proposals that come back then are not new. Put away, they stay away.
  useEffect(() => {
    const known = remembered.known;
    const arrived = known && items.find(item => !known.has(workItemKey(item)));
    if (arrived) { setSelected(workItemKey(arrived)); setCollapsed(false); }
    if (known || items.length > 0) remembered.known = new Set([...known ?? [], ...items.map(workItemKey)]);
  }, [remembered, items]);
  if (items.length === 0 || hidden) return null;
  const index = Math.max(0, items.findIndex(item => workItemKey(item) === selected));
  const item = items[index];
  const key = workItemKey(item);
  const working = busy.has(key);
  const go = (delta: number) => setSelected(workItemKey(items[(index + delta + items.length) % items.length]));
  const plan = item.kind === "plan";
  // The session works on it: there is nothing to start or to decide, only to see.
  const running = item.stage === "running";
  const acknowledge = () => void hub.act(item, "acknowledge");
  const later = () => void hub.act(item, plan ? "acknowledge" : "later");
  const dismiss = () => void hub.act(item, "dismiss");
  const pager = items.length > 1 && <span className="work-pager">
    <Button variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={14} />} aria-label={t("Previous")} title={t("Previous")} onClick={() => go(-1)} />
    <span aria-live="polite">{t("{index} of {count}", { index: index + 1, count: items.length })}</span>
    <Button variant="minimal" size="small" icon={<AppIcon name="chevronRight" size={14} />} aria-label={t("Next")} title={t("Next")} onClick={() => go(1)} />
  </span>;
  const over = (event: ReactPointerEvent, targets: Record<DockSide, PaneRect>, origin: { x: number; y: number }) =>
    dockTargetAt(targets, event.clientX - origin.x, event.clientY - origin.y);
  const handle = {
    onPointerDown(event: ReactPointerEvent<HTMLElement>) {
      // The buttons of the header keep their clicks; the chip itself is a handle. React passes on the presses
      // in its portals too: those of the placement menu, which is no part of the handle, are not a drag.
      const target = event.target as Element;
      if (event.button !== 0 || !event.currentTarget.contains(target) || (!collapsed && target.closest("button"))) return;
      // No text selection starts from the handle. The click of the chip still comes.
      event.preventDefault();
      drag.current = { pointer: event.pointerId, x: event.clientX, y: event.clientY, origin: { x: 0, y: 0 }, targets: null };
      dragged.current = false;
      // A quick move leaves the handle before the drag starts: the handle keeps the pointer from the press.
      event.currentTarget.setPointerCapture(event.pointerId);
    },
    onPointerMove(event: ReactPointerEvent<HTMLElement>) {
      const current = drag.current;
      if (!current || current.pointer !== event.pointerId) return;
      if (!current.targets) {
        if (Math.abs(event.clientX - current.x) + Math.abs(event.clientY - current.y) < 5) return;
        const pane = cards?.parentElement, notes = pane ? notesSurface(pane) : null;
        if (!pane || !notes) { drag.current = null; return; }
        const area = pane.getBoundingClientRect();
        current.origin = { x: area.left, y: area.top };
        current.targets = dockTargets({ width: area.width, height: area.height }, notes);
        dragged.current = true;
        pane.ownerDocument.getSelection()?.removeAllRanges();
      }
      const targets = current.targets, side = over(event, targets, current.origin);
      setGuide(previous => previous?.targets === targets && previous.over === side ? previous : { targets, over: side });
    },
    onPointerUp(event: ReactPointerEvent<HTMLElement>) {
      const current = drag.current;
      drag.current = null;
      setGuide(null);
      if (!current?.targets || current.pointer !== event.pointerId) return;
      const side = over(event, current.targets, current.origin);
      if (side) chooseDock(side);
      // The click of this release, on the chip, comes before this timer: it must not also open the cards.
      setTimeout(() => { dragged.current = false; });
    },
    onPointerCancel() { drag.current = null; dragged.current = false; setGuide(null); },
  };
  const pane = cards?.parentElement;
  const guideView = guide && pane && createPortal(<div className="work-dock-guide" aria-hidden="true">
    <span className="work-dock-center" style={{ left: (guide.targets.left.left + guide.targets.right.left) / 2, top: guide.targets.left.top }}>
      <AppIcon name="notes" size={14} /></span>
    {dockSides.map(side => <span key={side} className="work-dock-target" data-side={side} data-over={guide.over === side || undefined} data-current={dock === side || undefined}
      style={{ left: guide.targets[side].left, top: guide.targets[side].top, width: guide.targets[side].width, height: guide.targets[side].height }}>
      <AppIcon name={dockIcons[side]} size={16} /></span>)}
  </div>, pane);
  // A side chosen here gets room: Notes move out of its way. Automatic puts the cards where Notes leave room.
  const placement = <PopoverNext placement="bottom-end" content={<Menu aria-label={t("Place the proposals")}>
    <MenuItem icon={<AppIcon name="move" size={14} />} text={t("Automatic")} active={dock === null} onClick={() => chooseDock(null)} />
    {dockSides.map(side => <MenuItem key={side} icon={<AppIcon name={dockIcons[side]} size={14} />} text={t(dockLabels[side])} active={dock === side}
      onClick={() => chooseDock(side)} />)}
  </Menu>}>
    <Button variant="minimal" size="small" icon={<AppIcon name="move" size={14} />} aria-haspopup="menu" aria-label={t("Place the proposals")}
      title={t("Place the proposals around Notes. You can also drag them onto an arrow.")} />
  </PopoverNext>;
  const decisions = running
    ? <Button intent="primary" size="small" disabled={working} icon={<AppIcon name="check" size={14} />} title={t("Put the card away. The task stays in Work items.")}
      onClick={acknowledge}>{t("OK")}</Button>
    : <>
    <Button variant="minimal" size="small" disabled={working} icon={<AppIcon name="later" size={14} />} title={t(plan ? "Keep the plan for later. It stays in Work items." : "Keep the task in the Backlog of Work items.")}
      onClick={later}>{t(plan ? "Later" : "Backlog")}</Button>
    {!plan && <Button variant="minimal" size="small" disabled={working} icon={<AppIcon name="close" size={14} />} title={t("This task is not worth doing.")} onClick={dismiss}>{t("Dismiss")}</Button>}
  </>;

  return <aside ref={setCards} className="work-cards" data-collapsed={collapsed || undefined} data-dragging={guide ? true : undefined} aria-label={t("Proposed work items")}>
    {guideView}
    {collapsed
      ? <button type="button" className="work-cards-chip" title={t("Show the proposed work items")} {...handle} onClick={() => { if (!dragged.current) setCollapsed(false); }}>
          <AppIcon name="task" size={14} />{t(items.length === 1 ? "{count} proposal" : "{count} proposals", { count: items.length })}<AppIcon name="chevronDown" size={14} /></button>
      : <section className="work-card" data-kind={item.kind} aria-labelledby={titleId}>
          <header {...handle} title={t("Drag onto an arrow around Notes to place the proposals there.")}>
            <span className="work-card-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(plan ? "Plan ready" : running ? "Task in progress" : "Proposed task")}</span>
            {pager}
            {placement}
            <Button variant="minimal" size="small" icon={<AppIcon name="minus" size={14} />} aria-label={t("Put away")} title={t("Put the proposals away")} onClick={() => setCollapsed(true)} />
          </header>
          <h3 id={titleId}>{item.title}</h3>
          <WorkItemTags item={item} shortId tags />
          {item.summary && <p className="work-card-summary">{item.summary}</p>}
          <button type="button" className="work-card-more" onClick={() => setDetail(true)}>{t(plan ? "Read the plan" : "Details")}<AppIcon name="chevronRight" size={13} /></button>
          {running ? <p className="work-card-running" role="status"><ActivitySpinner size={13} />{t("This session is working on this task.")}</p>
            : <WorkStartButtons preferred={preferredStart} here disabled={working} compact onStart={start => onStart(item, start)} />}
          <footer>{working && <ActivitySpinner size={13} />}{decisions}</footer>
        </section>}
    {detail && <AppWindow storageKey="codealta.desktop.work-item-window.v1" title={item.title} titleId={`${titleId}-window`} className="work-item-window"
      preferredSize={viewport => ({ width: Math.min(860, viewport.width - 80), height: Math.min(720, viewport.height - 80) })}
      onClose={() => setDetail(false)} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); setDetail(false); }}
      headerActions={pager}>
      <div className="work-window">
        <div className="work-window-meta">
          <span className="work-card-kind"><AppIcon name={workKindIcon(item.kind)} size={14} />{t(plan ? "Plan ready" : running ? "Task in progress" : "Proposed task")}</span>
          <WorkItemTags item={item} shortId tags />
          <code title={item.file}>{item.file}</code>
        </div>
        <div className="work-window-text">{item.summary && <p className="work-window-summary">{item.summary}</p>}<WorkItemText hub={hub} item={item} /></div>
        <footer className="work-window-actions">
          {running ? <p className="work-card-running" role="status"><ActivitySpinner size={13} />{t("This session is working on this task.")}</p>
            : <WorkStartButtons preferred={preferredStart} here disabled={working} onStart={start => { setDetail(false); onStart(item, start); }} />}
          <span className="work-window-decisions">
            {decisions}
            <Button variant="minimal" size="small" icon={<AppIcon name="list" size={14} />} onClick={() => { setDetail(false); onOpenList(item); }}>{t("Show in Work items")}</Button>
          </span>
        </footer>
      </div>
    </AppWindow>}
  </aside>;
}
