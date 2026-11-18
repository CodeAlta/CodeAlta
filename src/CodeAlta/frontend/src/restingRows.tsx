import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { flushSync } from "react-dom";
import { inHiddenPane } from "./keptPanes";

// The transcript of a long session is 20,000 elements when every row is drawn, and everything the window does to its
// document costs with them: showing its tab again, a change of the focus, a change of a style high in the page.
// A row far from what the reader sees is put to rest instead: a stand-in of exactly its height takes its place, so the
// timeline keeps its length and its scroll position, and the row is drawn again before the reader reaches it.
//
// Rows are drawn once before they rest, so the height of a stand-in is the one the row had: nothing is estimated.
// A row that comes back above the view with another height (the pane changed its width meanwhile) moves the view by
// the difference, so what the reader looks at stays where it is.
//
// What looks a row up in the document still finds it: a stand-in holds, at the place they had, an empty element for
// each element of the row that carries an attribute rows are looked up by (`mirrored`). What needs the row itself
// (its text, its controls) asks for it first with `wakeTimelineRows`.

/** The number of rows from which the rows far from the view are put to rest. */
export const restThreshold = 80;
/** A row further than this from the view is put to rest, in pixels. */
const restBeyond = 3000;
/** A row at rest nearer than this to the view is drawn again, in pixels. */
const wakeWithin = 1500;
/** The last rows are never put to rest: the timeline grows there, and follows its end. */
const keptLast = 4;
/** The rows put to rest in one pass: the rest follows in the next frames, so no frame pays for a whole transcript. */
const restStep = 60;

/** The attributes rows are looked up by in the document: a stand-in mirrors the elements that carry one. */
const mirrored = ["data-row-offset", "data-prompt-offset", "data-persisted-message", "data-tool-call", "data-tool-calls", "data-run-id", "data-workflow-row"] as const;
const mirroredSelector = mirrored.map(name => `[${name}]`).join(", ");
/** What is never put to rest: it holds a state of its own that drawing it again would lose. */
const keptAwake = "iframe, video, audio";

const wakeEvent = "codealta:wake-rows";
type WakeRequest = Readonly<{ selector?: string; hold: number; later: boolean }>;

/**
 * Draws the rows at rest of the timelines under `root`: the ones that hold an element `selector` matches, or all of
 * them. With `hold`, no row is put to rest for that many milliseconds, for what goes on working on the rows it asked for.
 * The rows are in the document when this returns, unless `later` is asked: an effect cannot have them drawn at once,
 * and finds them there in the next frame.
 */
export function wakeTimelineRows(root: ParentNode, selector?: string, hold = 0, later = false): void {
  const detail: WakeRequest = { selector, hold, later };
  for (const messages of Array.from(root.querySelectorAll(".messages"))) messages.dispatchEvent(new CustomEvent(wakeEvent, { detail }));
}

/** Whether an element found in the document stands in for an element of a row at rest. */
export function isRestingRow(element: Element): boolean {
  return element.classList.contains("timeline-rest-stub");
}

type Stub = Readonly<{ top: number; attributes: Readonly<Record<string, string>> }>;
type Rest = Readonly<{ height: number; stubs: readonly Stub[] }>;

/** The rows at rest of one timeline: each row follows its own, so putting one to rest renders no other. */
export function createRestingRows() {
  const rests = new Map<string, Rest>();
  const listeners = new Map<string, Set<() => void>>();
  const tell = (key: string) => { for (const listener of listeners.get(key) ?? []) listener(); };
  return {
    get: (key: string): Rest | undefined => rests.get(key),
    resting: (): string[] => [...rests.keys()],
    subscribe(key: string, listener: () => void) {
      let set = listeners.get(key);
      if (!set) listeners.set(key, set = new Set());
      set.add(listener);
      return () => { set.delete(listener); if (set.size === 0) listeners.delete(key); };
    },
    apply(rest: readonly (readonly [string, Rest])[], wake: readonly string[]) {
      for (const [key, value] of rest) { rests.set(key, value); tell(key); }
      for (const key of wake) if (rests.delete(key)) tell(key);
    },
    /** A row that left the timeline keeps nothing. */
    keep(keys: ReadonlySet<string>) { for (const key of rests.keys()) if (!keys.has(key)) rests.delete(key); },
  };
}

export type RestingRows = ReturnType<typeof createRestingRows>;

/** A row of the timeline, or its stand-in while it rests. */
export function RestingRow({ rows, rowKey, children }: { rows: RestingRows; rowKey: string; children: ReactNode }) {
  const subscribe = useCallback((listener: () => void) => rows.subscribe(rowKey, listener), [rows, rowKey]);
  const rest = useSyncExternalStore(subscribe, () => rows.get(rowKey));
  if (!rest) return children;
  return <div className="timeline-rest" role="presentation" style={{ height: rest.height }}>
    {rest.stubs.map((stub, index) => <i key={index} className="timeline-rest-stub" style={{ top: stub.top }} {...stub.attributes} />)}
  </div>;
}

type Box = Readonly<{ top: number; bottom: number }>;

// The box of a row. A row that is `display: contents` has none of its own: it is the one of what it holds.
function boxOf(row: Element): Box | null {
  const own = row.getBoundingClientRect();
  if (own.height > 0 || row.childElementCount === 0) return own.height > 0 ? own : null;
  const first = row.firstElementChild!.getBoundingClientRect(), last = row.lastElementChild!.getBoundingClientRect();
  return last.bottom > first.top ? { top: first.top, bottom: last.bottom } : null;
}

function capture(row: Element, top: number, height: number): Rest {
  const stubs: Stub[] = [];
  for (const element of [row, ...Array.from(row.querySelectorAll(mirroredSelector))]) {
    const attributes: Record<string, string> = {};
    for (const name of mirrored) { const value = element.getAttribute(name); if (value !== null) attributes[name] = value; }
    if (Object.keys(attributes).length === 0) continue;
    const box = element === row ? null : element.getBoundingClientRect();
    stubs.push({ top: box ? Math.max(0, box.top - top) : 0, attributes });
  }
  return { height, stubs };
}

function editable(target: EventTarget | null): boolean {
  return target instanceof Element && !!target.closest("input, textarea, select, [contenteditable]:not([contenteditable='false'])");
}

/**
 * Puts the rows of a timeline that are far from the view to rest and draws them again as the reader comes near.
 * `keys` are the keys of its rows in the order they are drawn, one element of the timeline each. With `whole`, every row
 * is drawn (a search reads the text of them all). The timeline puts `messages` on the element that holds its rows, and
 * draws each row in a `RestingRow` of `rows`.
 */
export function useRestingRows(keys: readonly string[], whole: boolean) {
  const [rows] = useState(createRestingRows);
  const element = useRef<HTMLDivElement>(null);
  const latest = useRef({ keys, whole }); latest.current = { keys, whole };
  // The rows the reader acted on (a press, a key): what they opened in one stays open, so it is not put to rest.
  const touched = useRef(new Set<string>());
  const holdUntil = useRef(0);
  const frame = useRef(0);

  const pass = useCallback(() => {
    frame.current = 0;
    const messages = element.current, scroller = messages?.closest<HTMLElement>(".timeline-scroll");
    const { keys, whole } = latest.current;
    if (!messages || !scroller || messages.children.length !== keys.length) return;
    if (whole) return;
    // A timeline that is not drawn has no view to be far from.
    if (scroller.clientHeight === 0 || inHiddenPane(messages)) return;
    const view = scroller.getBoundingClientRect();
    const children = Array.from(messages.children);
    const boxes = children.map(boxOf);
    const gap = Number.parseFloat(getComputedStyle(messages).rowGap) || 0;
    // The height a row takes in the timeline: up to where the next one starts.
    const heightOf = (index: number) => {
      const box = boxes[index], next = boxes[index + 1];
      return !box ? 0 : next ? next.top - box.top - gap : box.bottom - box.top;
    };
    const selection = messages.ownerDocument.getSelection();
    const selecting = !!selection && !selection.isCollapsed && !!selection.anchorNode && messages.contains(selection.anchorNode);
    const resting = keys.length >= restThreshold && !selecting && performance.now() >= holdUntil.current;
    const focused = messages.ownerDocument.activeElement;
    const rest: [string, Rest][] = [], wake: { key: string; index: number; height: number; above: boolean }[] = [];
    for (let index = 0; index < children.length; index++) {
      const child = children[index], box = boxes[index];
      if (!box) continue;
      const distance = box.bottom < view.top ? view.top - box.bottom : box.top > view.bottom ? box.top - view.bottom : 0;
      if (child.classList.contains("timeline-rest")) {
        if (distance < wakeWithin) wake.push({ key: keys[index], index, height: heightOf(index), above: box.bottom <= view.top });
      } else if (resting && distance > restBeyond && index < children.length - keptLast && rest.length < restStep && boxes[index + 1]
        && !touched.current.has(keys[index]) && !child.contains(focused) && !child.querySelector(keptAwake)) {
        const height = heightOf(index);
        if (height > 0) rest.push([keys[index], capture(child, box.top, height)]);
      }
    }
    if (rest.length === 0 && wake.length === 0) return;
    flushSync(() => rows.apply(rest, wake.map(row => row.key)));
    // A row drawn again above the view with another height would move what the reader looks at: the view follows it.
    const above = wake.filter(row => row.above);
    if (above.length) {
      const now = Array.from(messages.children).map(boxOf);
      let moved = 0;
      for (const row of above) {
        const box = now[row.index], next = now[row.index + 1];
        if (box) moved += (next ? next.top - box.top - gap : box.bottom - box.top) - row.height;
      }
      if (Math.abs(moved) >= 0.5) scroller.scrollTop += moved;
    }
    if (rest.length === restStep) frame.current = requestAnimationFrame(pass);
  }, [rows]);
  const ask = useCallback(() => { frame.current ||= requestAnimationFrame(pass); }, [pass]);

  useEffect(() => {
    const messages = element.current, scroller = messages?.closest<HTMLElement>(".timeline-scroll");
    if (!messages || !scroller) return;
    const wakeRows = (event: Event) => {
      const { selector, hold, later } = (event as CustomEvent<WakeRequest>).detail;
      if (hold > 0) holdUntil.current = Math.max(holdUntil.current, performance.now() + hold);
      const { keys } = latest.current;
      if (messages.children.length !== keys.length) return;
      const wake = Array.from(messages.children).flatMap((child, index) => child.classList.contains("timeline-rest")
        && (!selector || child.matches(selector) || child.querySelector(selector)) ? [keys[index]] : []);
      if (!wake.length) return;
      if (later) rows.apply([], wake);
      else flushSync(() => rows.apply([], wake));
    };
    const touch = (event: Event) => {
      const { keys } = latest.current;
      const row = event.target instanceof Element ? Array.from(messages.children).findIndex(child => child.contains(event.target as Element)) : -1;
      if (row >= 0 && messages.children.length === keys.length) touched.current.add(keys[row]);
    };
    // A selection of everything takes the text of every row.
    const selectAll = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === "a" && !editable(event.target)
        && scroller.clientHeight > 0 && !inHiddenPane(messages)) wakeTimelineRows(scroller, undefined, 60_000);
    };
    const resized = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(ask);
    resized?.observe(scroller);
    scroller.addEventListener("scroll", ask, { passive: true });
    messages.addEventListener(wakeEvent, wakeRows);
    messages.addEventListener("pointerdown", touch, true);
    messages.addEventListener("keydown", touch, true);
    messages.ownerDocument.addEventListener("keydown", selectAll, true);
    return () => {
      resized?.disconnect();
      scroller.removeEventListener("scroll", ask);
      messages.removeEventListener(wakeEvent, wakeRows);
      messages.removeEventListener("pointerdown", touch, true);
      messages.removeEventListener("keydown", touch, true);
      messages.ownerDocument.removeEventListener("keydown", selectAll, true);
      cancelAnimationFrame(frame.current); frame.current = 0;
    };
  }, [rows, ask]);

  // The rows changed (more of them, others): the ones that left keep nothing, and the new ones are looked at.
  const count = keys.length;
  useEffect(() => {
    const present = new Set(latest.current.keys);
    rows.keep(present);
    for (const key of touched.current) if (!present.has(key)) touched.current.delete(key);
    ask();
  }, [rows, ask, count, whole]);
  // Every row is asked for: they are in the document before the window is drawn and before anything reads it.
  useLayoutEffect(() => { if (whole) rows.apply([], rows.resting()); }, [rows, whole]);

  return { rows, messages: element };
}
