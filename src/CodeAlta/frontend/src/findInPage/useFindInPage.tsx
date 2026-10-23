import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { Button, InputGroup } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { clearFound, defaultFindOptions, findPatternInvalid, findRanges, isFindKey, markFound, noFoundMarks, rangeShown, revealRange, type FindOptions } from "./findInPage";

const mac = typeof navigator !== "undefined" && navigator.platform.startsWith("Mac");
/** The key that finds in a page, as the window names it. */
export const findKeyLabel = mac ? "Cmd+F" : "Ctrl+F";

/** The search in a rendered page: whether it is open, what opens and closes it, its keys, and its box. */
export type FindInPage = Readonly<{
  finding: boolean;
  /** Whether the page can be searched now. */
  available: boolean;
  open: () => void;
  close: (refocus?: boolean) => void;
  /** Whether an element is in the box of the search. */
  inBar: (target: EventTarget | null) => boolean;
  /**
   * Takes Ctrl+F (Cmd+F on macOS) to open the search, and Escape outside its box to close it; true when it took the
   * key. The element that has these keys tells the window with `data-find-keys`, or Ctrl+F searches the sessions.
   */
  keyDown: (event: ReactKeyboardEvent) => boolean;
  /** The box of the search, rendered over the page by its host, which positions it. */
  bar: ReactNode;
}>;

// The element that scrolls a place of the page into view: the page itself, or the first of its parents that scrolls.
function scrollerOf(element: HTMLElement): HTMLElement {
  for (let node: HTMLElement | null = element; node; node = node.parentElement) {
    const overflow = getComputedStyle(node).overflowY;
    if ((overflow === "auto" || overflow === "scroll") && node.scrollHeight > node.clientHeight) return node;
  }
  return element.ownerDocument.scrollingElement as HTMLElement ?? element;
}

/**
 * Finds a text in a rendered page, as it is typed: the first place from where the reader is is shown, Enter and
 * Shift+Enter go to the next and the previous one, Escape closes. The places are found again, without moving the
 * page, when the page is drawn again (another file, new colors).
 */
export function useFindInPage({ page, available = true, className, returnTo }: {
  /** The element whose text is searched; it stays in the document while the search is open. */
  page: () => HTMLElement | null;
  /** Whether the page can be searched now: it is read and shown. The search closes when it no longer can. */
  available?: boolean;
  /** The class of the box, for its host to place it. */
  className?: string;
  /** What takes the keyboard when the search closes; else what had it when it opened, else the page. */
  returnTo?: () => HTMLElement | null;
}): FindInPage {
  const { t } = useShellLanguage();
  const pageOf = useRef(page), returnOf = useRef(returnTo);
  pageOf.current = page;
  returnOf.current = returnTo;
  const input = useRef<HTMLInputElement>(null), box = useRef<HTMLDivElement>(null), opener = useRef<HTMLElement | null>(null);
  const [finding, setFinding] = useState(false);
  const [text, setText] = useState("");
  const [options, setOptions] = useState<FindOptions>(defaultFindOptions);
  const invalid = findPatternInvalid(text, options);
  const [found, setFound] = useState<Readonly<{ index: number; count: number }>>({ index: -1, count: 0 });
  const ranges = useRef<Range[]>([]), shown = useRef(-1), marks = useRef(noFoundMarks());
  const open = finding && available;
  // The page searched: another element (the page drawn again in another place) is searched again.
  const searched = open ? page() : null;

  useEffect(() => {
    const host = searched;
    if (!host) { ranges.current = []; shown.current = -1; clearFound(marks.current); setFound({ index: -1, count: 0 }); return; }
    let frame = 0;
    const run = (typed: boolean) => {
      const next = host.isConnected ? findRanges(host, text, options) : [];
      const view = scrollerOf(host), top = view.getBoundingClientRect().top;
      const index = next.length === 0 ? -1 : typed ? Math.max(0, next.findIndex(range => range.getBoundingClientRect().bottom > top))
        : Math.max(0, Math.min(shown.current, next.length - 1));
      ranges.current = next;
      shown.current = index;
      markFound(marks.current, next, index);
      setFound({ index, count: next.length });
      if (typed && index >= 0 && !rangeShown(next[index], view)) revealRange(next[index], view);
    };
    run(true);
    const observer = new MutationObserver(() => { frame ||= requestAnimationFrame(() => { frame = 0; run(false); }); });
    observer.observe(host, { childList: true, subtree: true, characterData: true });
    return () => { observer.disconnect(); cancelAnimationFrame(frame); };
  }, [searched, text, options]);
  useEffect(() => () => clearFound(marks.current), []);
  useEffect(() => { if (!available) setFinding(false); }, [available]);

  function start() {
    if (!available) return;
    // A few words selected in the page are what is looked for.
    const selection = window.getSelection(), selected = selection?.toString().trim() ?? "", host = pageOf.current();
    if (selected && selected.length <= 200 && !selected.includes("\n") && selection?.anchorNode && host?.contains(selection.anchorNode)) setText(selected);
    if (!box.current?.contains(document.activeElement)) opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setFinding(true);
    requestAnimationFrame(() => { input.current?.focus(); input.current?.select(); });
  }
  function close(refocus = true) {
    setFinding(false);
    // The keyboard goes back where it was, and the keys scroll the page again.
    const back = opener.current;
    opener.current = null;
    if (refocus) (returnOf.current?.() ?? (back?.isConnected ? back : pageOf.current()))?.focus({ preventScroll: true });
  }
  function step(by: 1 | -1) {
    const list = ranges.current, host = pageOf.current();
    if (list.length === 0) return;
    const index = (shown.current + by + list.length) % list.length;
    shown.current = index;
    markFound(marks.current, list, index);
    setFound({ index, count: list.length });
    if (host) revealRange(list[index], scrollerOf(host));
  }
  const inBar = (target: EventTarget | null) => target instanceof Node && !!box.current?.contains(target);
  function keyDown(event: ReactKeyboardEvent) {
    if (event.defaultPrevented || event.nativeEvent.isComposing) return false;
    if (isFindKey(event, mac) && !event.repeat) {
      if (!available) return false;
      event.preventDefault();
      start();
      return true;
    }
    if (event.key === "Escape" && open && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      close(inBar(event.target));
      return true;
    }
    return false;
  }

  // The options of the search, as the text editor shows them, with its keys in the box: Alt+C, Alt+W, Alt+R.
  const toggles: readonly (readonly [keyof FindOptions, IconName, "Match case" | "Match whole word" | "Use regular expression", string])[] = [
    ["matchCase", "caseSensitive", "Match case", "c"], ["wholeWord", "wholeWord", "Match whole word", "w"], ["regex", "regex", "Use regular expression", "r"]];
  const toggle = (option: keyof FindOptions) => setOptions(current => ({ ...current, [option]: !current[option] }));
  const bar = open ? <div className={className ? `find-in-page ${className}` : "find-in-page"} role="search" aria-label={t("Find in this page")} ref={box}
    onKeyDown={event => {
      if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const chosen = toggles.find(entry => entry[3] === event.key.toLowerCase());
      if (!chosen) return;
      event.preventDefault();
      toggle(chosen[0]);
    }}>
    <InputGroup inputRef={input} size="small" className="find-in-page-query" value={text} maxLength={200} autoComplete="off" spellCheck={false}
      placeholder={t("Find in this page")} aria-label={t("Find in this page")} aria-invalid={invalid || undefined} intent={invalid ? "danger" : undefined}
      title={invalid ? t("The regular expression is not valid.") : undefined} onChange={event => setText(event.target.value)}
      onKeyDown={event => {
        // Enter goes to the next place, Shift+Enter to the one before. A text being composed is left to its input method.
        if (event.key !== "Enter" || event.altKey || event.ctrlKey || event.metaKey || event.nativeEvent.isComposing) return;
        event.preventDefault();
        step(event.shiftKey ? -1 : 1);
      }}
      rightElement={<span className="find-in-page-options">
        {toggles.map(([option, icon, label, key]) => <Button key={option} variant="minimal" size="small" className="find-in-page-option" icon={<AppIcon name={icon} size={15} />}
          active={options[option]} aria-pressed={options[option]} aria-label={t(label)} title={`${t(label)} (Alt+${key.toUpperCase()})`}
          onClick={() => { toggle(option); input.current?.focus(); }} />)}
      </span>} />
    <span className="find-in-page-count" role="status" data-empty={text.trim() && found.count === 0 || undefined}>
      {text.trim() ? t("{index} of {count}", { index: found.index + 1, count: found.count }) : ""}</span>
    <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={15} />} aria-label={t("Previous match")} title={`${t("Previous match")} (Shift+Enter)`}
      disabled={found.count === 0} onClick={() => step(-1)} />
    <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={15} />} aria-label={t("Next match")} title={`${t("Next match")} (Enter)`}
      disabled={found.count === 0} onClick={() => step(1)} />
    <Button variant="minimal" size="small" icon={<AppIcon name="close" size={15} />} aria-label={t("Close")} title={`${t("Close")} (Esc)`} onClick={() => close()} />
  </div> : null;
  return { finding: open, available, open: start, close, inBar, keyDown, bar };
}
