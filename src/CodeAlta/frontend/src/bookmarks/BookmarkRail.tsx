import { useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type KeyboardEvent, type RefObject } from "react";
import { PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { bookmarkStep, bookmarkTitle, sessionBookmarks, type Bookmark } from "./bookmarks";
import type { BookmarksHub } from "./bookmarksHub";
import type { TimelineBookmarks } from "./timelineBookmarks";

/** Where a bookmark is from the rows in view: before them, among them or after them. */
type Position = -1 | 0 | 1;

/** The date of a message as its card says it: the day and the time, in the language of the window. */
export function bookmarkMoment(value: string | null | undefined, locale: string): string {
  const date = value ? new Date(value) : null;
  if (!date || Number.isNaN(date.getTime())) return "";
  return date.toLocaleString(locale, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/**
 * The bookmarks of the messages of a session beside its timeline: one mark for each, in the order of the conversation.
 * A mark says what it leads to when the pointer or the keyboard is on it, and goes there when pressed: to a message of
 * the window at once, to an older one after its history is read. The marks of the messages in view are lit.
 * It also gives the timeline the bookmarks it marks its rows with, and steps from one bookmark to the next.
 */
export function BookmarkRail({ sessionId, hub, marks, scroll, onGo, step }: {
  sessionId: string; hub: BookmarksHub; marks: TimelineBookmarks;
  /** The element the timeline scrolls in. */
  scroll: RefObject<HTMLElement | null>;
  /** Goes to the message of a bookmark. */
  onGo: (bookmark: Bookmark) => void;
  /** Receives what steps to the bookmark before or after the rows in view; it answers false when there is none that way. */
  step?: RefObject<((direction: -1 | 1) => boolean) | null>;
}) {
  const { t, locale } = useShellLanguage();
  const held = () => hub.getSnapshot().bookmarks;
  const all = useSyncExternalStore(hub.subscribe, held, held);
  const messages = useMemo(() => sessionBookmarks(all, sessionId).messages, [all, sessionId]);
  // The identity of the list changes with every answer of the host; what it holds for this session rarely does.
  const signature = messages.map(bookmark => `${bookmark.id}:${bookmark.runId}:${bookmark.contentId}`).join("|");
  useLayoutEffect(() => { marks.setMessages(messages); }, [marks, signature]);
  const rows = useSyncExternalStore(marks.subscribe, marks.rows, marks.rows);
  const [positions, setPositions] = useState<readonly Position[]>([]);
  // The mark whose card is shown: the one under the pointer or with the focus.
  const [shown, setShown] = useState<string | null>(null);
  const latest = useRef({ messages, rows, positions, onGo }); latest.current = { messages, rows, positions, onGo };

  // Where each bookmark is from the rows in view. A message the window does not hold is older than it: before.
  function measure(): readonly Position[] {
    const element = scroll.current, { messages, rows } = latest.current;
    if (!element || messages.length === 0) return [];
    const keys = new Map<string, string>();
    for (const [key, id] of rows) keys.set(id, key);
    const view = element.getBoundingClientRect();
    return messages.map(bookmark => {
      const key = keys.get(bookmark.id);
      const row = key ? element.querySelector<HTMLElement>(`[data-row-offset="${key}"]`) : null;
      if (!row) return -1;
      const box = row.getBoundingClientRect();
      return box.bottom <= view.top + 8 ? -1 : box.top >= view.bottom - 8 ? 1 : 0;
    });
  }
  // Read when the timeline scrolls or changes size, once a frame at most, and only while the session has bookmarks.
  useEffect(() => {
    const element = scroll.current;
    if (!element || messages.length === 0) { setPositions([]); return; }
    let frame = 0;
    const read = () => {
      frame = 0;
      // A timeline that is no longer drawn (its tab went behind another) leaves no card of a mark behind.
      if (element.clientWidth === 0) setShown(null);
      const next = measure();
      setPositions(current => current.length === next.length && current.every((value, index) => value === next[index]) ? current : next);
    };
    const ask = () => { if (!frame) frame = requestAnimationFrame(read); };
    ask();
    element.addEventListener("scroll", ask, { passive: true });
    const observer = new ResizeObserver(ask);
    observer.observe(element);
    for (const child of Array.from(element.children)) observer.observe(child);
    return () => { if (frame) cancelAnimationFrame(frame); element.removeEventListener("scroll", ask); observer.disconnect(); };
  }, [scroll, signature, rows]);

  useLayoutEffect(() => {
    if (!step) return;
    const go = (direction: -1 | 1) => {
      const { messages, onGo } = latest.current;
      const target = bookmarkStep(messages, measure(), direction);
      if (!target) return false;
      onGo(target);
      return true;
    };
    step.current = go;
    return () => { if (step.current === go) step.current = null; };
  }, [step]);

  // Up and Down move through the marks as they do in a list; Home and End go to its ends.
  function moveFocus(event: KeyboardEvent<HTMLElement>) {
    const buttons = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>("button.bookmark-rail-mark"));
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = event.key === "ArrowDown" ? index + 1 : event.key === "ArrowUp" ? index - 1 : event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : -2;
    if (next === -2 || index < 0) return;
    event.preventDefault();
    buttons[Math.min(buttons.length - 1, Math.max(0, next))]?.focus();
  }

  if (messages.length === 0) return null;
  return <nav className="bookmark-rail" aria-label={t("Bookmarks of this session")} onKeyDown={moveFocus}>
    {messages.map((bookmark, index) => {
      const title = bookmarkTitle(bookmark);
      const card = <div className="bookmark-rail-card">
        <span className="bookmark-rail-card-kind"><AppIcon name="bookmark" size={13} />{t(bookmark.role === "user" ? "Your message" : "Assistant message")}</span>
        <strong>{title}</strong>
        <time dateTime={bookmark.messageAt ?? undefined}>{bookmarkMoment(bookmark.messageAt, locale)}</time>
      </div>;
      // The card says what the mark leads to, as a tooltip does: it is no menu, and it leaves with the pointer.
      return <PopoverNext key={bookmark.id} content={card} interactionKind="hover-target" placement="left" hoverOpenDelay={80} hoverCloseDelay={60}
        isOpen={shown === bookmark.id} onInteraction={open => setShown(current => open ? bookmark.id : current === bookmark.id ? null : current)}
        popoverClassName="bookmark-rail-popover" autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false}>
        <button type="button" className="bookmark-rail-mark" data-bookmark-id={bookmark.id} data-in-view={positions[index] === 0 ? "true" : undefined}
          aria-label={t("Go to the bookmark: {title}", { title })} aria-current={positions[index] === 0 ? "true" : undefined}
          onClick={() => onGo(bookmark)}><span aria-hidden="true" /></button>
      </PopoverNext>;
    })}
  </nav>;
}
