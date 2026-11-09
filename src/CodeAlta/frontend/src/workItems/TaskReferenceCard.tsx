import { useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { closedTask } from "./taskReferences";
import { taskCategoryIcon, taskCategoryLabel, taskCategoryTone, workKindIcon, workStatusLabel, workStatusTone, type WorkItem } from "./workItems";

/** The id of the card a code span that names a task describes, for `aria-describedby`. */
export const taskReferenceCardId = "task-reference-card";

/**
 * What a code span that names a task says of it while the pointer or the focus is on it: why the task exists, where
 * it is, its title and its summary. It is placed under the span, or above it when the window has no room below.
 */
export function TaskReferenceCard({ item, anchor }: { item: WorkItem; anchor: HTMLElement }) {
  return <WorkItemsCard items={[item]} anchor={anchor} id={taskReferenceCardId} hint="Click to open it in Work items" />;
}

/**
 * The card of the work items an element names: for each, what it is (the category of a task, or a plan), where it
 * is, its title and its summary; then what a click on the element does.
 */
export function WorkItemsCard({ items, anchor, id, hint }: { items: readonly WorkItem[]; anchor: HTMLElement; id: string; hint: MessageKey }) {
  const { t } = useShellLanguage();
  const card = useRef<HTMLDivElement>(null);
  const [place, setPlace] = useState<Readonly<{ left: number; top: number }> | null>(null);
  useLayoutEffect(() => {
    const element = card.current;
    if (!element) return;
    const margin = 6;
    const box = anchor.getBoundingClientRect();
    const size = element.getBoundingClientRect();
    const top = box.bottom + margin + size.height <= window.innerHeight ? box.bottom + margin : Math.max(margin, box.top - margin - size.height);
    setPlace({ left: Math.max(margin, Math.min(box.left, window.innerWidth - size.width - margin)), top });
  }, [anchor, items]);
  // Placed in the dialog of a text inside one: outside it, the card would be under the modal layer.
  return createPortal(<div ref={card} id={id} role="tooltip" className="task-reference-card" style={place ?? { left: 0, top: 0, visibility: "hidden" }}>
    {items.map(item => {
      const task = item.kind === "task";
      const name = item.shortId ?? (task ? item.id : null);
      return <div key={`${item.kind}:${item.id}`} className="task-reference-card-item" data-closed={task && closedTask(item) || undefined}>
        <div className="task-reference-card-kind">
          <span className="task-reference-card-glyph" data-tone={task ? taskCategoryTone(item.category) : "primary"}>
            <AppIcon name={task ? taskCategoryIcon(item.category) : workKindIcon(item.kind)} size={13} /></span>
          {t(task ? taskCategoryLabel(item.category) : "Plan")}
          <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>
          {name && <code>{name}</code>}
        </div>
        <strong>{item.title}</strong>
        {item.summary && <p>{item.summary}</p>}
      </div>;
    })}
    <small>{t(hint)}</small>
  </div>, anchor.closest("dialog") ?? document.body);
}
