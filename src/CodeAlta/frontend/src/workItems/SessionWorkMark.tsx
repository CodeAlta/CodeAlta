import { useEffect, useRef, useState, type SyntheticEvent } from "react";
import { Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { WorkItemsCard } from "./TaskReferenceCard";
import { taskCategoryIcon, workKindIcon, type WorkItem } from "./workItems";

const sessionWorkCardId = "session-work-card";

// The pointer rests on the mark a moment before its card shows: a pointer that crosses the list shows none.
const hoverDelay = 300;

/**
 * The mark of a session row that carries out work items: the glyph of a task, then the short id of the item, or
 * their count when there are several. The pointer on it shows their card; a click opens the item in Work items, or
 * a menu of them. It is a part of the session row, which it does not select.
 */
export function SessionWorkMark({ items, onOpen }: { items: readonly WorkItem[]; onOpen(item: WorkItem): void }) {
  const { t } = useShellLanguage();
  const mark = useRef<HTMLSpanElement>(null);
  const [card, setCard] = useState(false);
  const [menu, setMenu] = useState(false);
  const timer = useRef(0);
  useEffect(() => () => clearTimeout(timer.current), []);
  useEffect(() => {
    if (!card) return;
    // The card stays where it was placed: a scroll puts it away.
    const hide = () => setCard(false);
    window.addEventListener("scroll", hide, true);
    window.addEventListener("resize", hide);
    return () => { window.removeEventListener("scroll", hide, true); window.removeEventListener("resize", hide); };
  }, [card]);
  if (!items.length) return null;
  const single = items.length === 1 ? items[0] : null;
  const hide = () => { clearTimeout(timer.current); setCard(false); };
  // The row is a button that selects its session: what happens on the mark stays there, the menu of the items included.
  const keep = (event: SyntheticEvent) => event.stopPropagation();
  const glyph = <span ref={mark} className="session-work-mark" data-open={menu || undefined} aria-label={t("Carries out a work item")}
    // An empty title keeps the tooltip of the row away: the card says more.
    title="" onPointerEnter={() => { if (!menu) { clearTimeout(timer.current); timer.current = window.setTimeout(() => setCard(true), hoverDelay); } }}
    onPointerLeave={hide} onClick={() => { hide(); if (single) onOpen(single); }}>
    <AppIcon name="task" size={12} />
    {single ? single.shortId && <code className="session-work-id">{single.shortId}</code> : <span className="session-work-count">{items.length}</span>}
  </span>;
  return <span className="session-work-mark-host" onClick={keep} onDoubleClick={keep} onKeyDown={keep}>
    {single ? glyph : <PopoverNext isOpen={menu} onInteraction={next => { hide(); setMenu(next); }} placement="bottom-end" content={<Menu aria-label={t("Carries out a work item")}>
      {items.map(item => <MenuItem key={`${item.kind}:${item.id}`} icon={<AppIcon name={item.kind === "task" ? taskCategoryIcon(item.category) : workKindIcon(item.kind)} size={15} />}
        text={item.title} label={item.shortId ?? undefined} onClick={() => onOpen(item)} />)}
    </Menu>}>{glyph}</PopoverNext>}
    {card && mark.current && <WorkItemsCard items={items} anchor={mark.current} id={sessionWorkCardId}
      hint={single ? "Click to open it in Work items" : "Click to choose one to open in Work items"} />}
  </span>;
}
