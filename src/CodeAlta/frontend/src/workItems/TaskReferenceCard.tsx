import { useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { closedTask } from "./taskReferences";
import { taskCategoryIcon, taskCategoryLabel, taskCategoryTone, workStatusLabel, workStatusTone, type WorkItem } from "./workItems";

/** The id of the card a code span that names a task describes, for `aria-describedby`. */
export const taskReferenceCardId = "task-reference-card";

/**
 * What a code span that names a task says of it while the pointer or the focus is on it: why the task exists, where
 * it is, its title and its summary. It is placed under the span, or above it when the window has no room below.
 */
export function TaskReferenceCard({ item, anchor }: { item: WorkItem; anchor: HTMLElement }) {
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
  }, [anchor, item]);
  // Placed in the dialog of a text inside one: outside it, the card would be under the modal layer.
  return createPortal(<div ref={card} id={taskReferenceCardId} role="tooltip" className="task-reference-card" data-closed={closedTask(item) || undefined}
    style={place ?? { left: 0, top: 0, visibility: "hidden" }}>
    <div className="task-reference-card-kind">
      <span className="task-reference-card-glyph" data-tone={taskCategoryTone(item.category)}><AppIcon name={taskCategoryIcon(item.category)} size={13} /></span>
      {t(taskCategoryLabel(item.category))}
      <Tag minimal round intent={workStatusTone(item)}>{t(workStatusLabel(item))}</Tag>
      <code>{item.shortId ?? item.id}</code>
    </div>
    <strong>{item.title}</strong>
    {item.summary && <p>{item.summary}</p>}
    <small>{t("Click to open it in Work items")}</small>
  </div>, anchor.closest("dialog") ?? document.body);
}
