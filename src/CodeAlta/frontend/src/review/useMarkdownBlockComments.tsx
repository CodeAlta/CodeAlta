import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { sourceBlocks, sourceLinesAttribute } from "../markdownSourceLines";
import { useShellLanguage } from "../shellLanguage";
import { blockCommented, blockRange, neighbourThread, orderThreads, placementBlock, textLines, type ReviewThread, type SourceBlock } from "./reviewThreads";
import { ReviewCommentEditor, ReviewThreadCard, threadLabel, type ReviewThreadActions } from "./ReviewThreadCard";

type Lines = Readonly<{ startLine: number; endLine: number }>;
type Block = Readonly<{ element: HTMLElement; start: number; end: number }>;

export type MarkdownBlockCommentsOptions = Readonly<{
  /**
   * The element around the rendered document (a `MarkdownContent` with `sourceLines`), positioned, with room on its
   * left for the gutter; it may be the element that scrolls. It takes the keys: give it the keyboard (`tabIndex`). Null
   * draws nothing.
   */
  container: HTMLElement | null;
  /** The text that is rendered, for the text of the lines a suggestion starts from. */
  source: string;
  /** The file, relative to the checkout, for the labels. */
  path: string;
  /** The threads of the file to draw after their blocks: not resolved, not outdated (see `threadsOnLines`). */
  threads: readonly ReviewThread[];
  actions: ReviewThreadActions | null;
  /** Writes a new comment on lines of the file; null: no comment can be added here. */
  onAdd: ((lines: Lines, body: string, suggestion: string | null) => Promise<boolean>) | null;
  /** Why a comment cannot be added now; null when it can. */
  blocked?: string | null;
}>;

export type MarkdownBlockComments = Readonly<{
  /** The gutter (the hovered block's **+**, the bars of the commented blocks): render it inside the container. */
  gutter: ReactNode;
  /** The cards, drawn after their blocks: render them anywhere in the tree of the host. */
  portals: ReactNode;
  /** Opens the editor of a new comment on lines, or on the blocks of the selection, or the hovered block. */
  comment: (lines?: Lines) => void;
  reveal: (id: string) => boolean;
  step: (direction: 1 | -1) => boolean;
  composing: boolean;
}>;

const composerKey = "\u0000composer";
const editable = (target: EventTarget | null) => target instanceof HTMLElement && !!target.closest("input, textarea, select, [contenteditable=''], [contenteditable='true']");

/** The blocks of the document, without those of the cards drawn into it. */
function documentBlocks(root: HTMLElement): Block[] {
  return sourceBlocks(root).filter(block => !block.element.closest(".review-block-host"));
}
/** Where a card goes after a block: a row of a table is followed by the table, a line of a list item by the item. */
function cardPlace(element: HTMLElement): HTMLElement {
  return element.tagName === "TR" || element.tagName === "TD" || element.tagName === "TH" ? element.closest("table") ?? element : element;
}

/**
 * Comments on a rendered Markdown document. Each block knows its source lines (`data-source-lines`). Hovering a
 * block shows a **+** in the gutter on its left: a click comments the block, and a Shift-click on another block
 * extends the comment to the range of blocks between them. `c` comments the blocks of the selection or the hovered
 * block, `n` and `p` go from thread to thread. The editor of a block comment offers **Suggest edit** (the text of
 * the block to change) and **Delete block** (a suggestion with no text). A commented block has a colored bar in
 * the gutter, and each thread is a card after the block that holds its last line. The document is rendered again
 * often (a file read again, a diagram drawn): the cards are kept in nodes of their own, put back after their
 * blocks each time the rendered elements change.
 */
export function useMarkdownBlockComments({ container, source, path, threads, actions, onAdd, blocked = null }: MarkdownBlockCommentsOptions): MarkdownBlockComments {
  const { t } = useShellLanguage();
  const [composer, setComposer] = useState<Lines | null>(null);
  const [hovered, setHovered] = useState<Readonly<{ lines: SourceBlock; top: number; height: number }> | null>(null);
  const [bars, setBars] = useState<readonly Readonly<{ key: string; top: number; height: number; kind: "draft" | "thread" | "range" }>[]>([]);
  const hosts = useRef(new Map<string, HTMLDivElement>());
  const cards = useRef(new Map<string, HTMLElement>());
  const composerBox = useRef<HTMLTextAreaElement | null>(null);
  const anchor = useRef<SourceBlock | null>(null);
  const placed = useMemo(() => orderThreads(threads.filter(thread => thread.path === path && thread.side === "new" && thread.startLine > 0)), [threads, path]);
  const latest = useRef({ placed, composer, onAdd, blocked, hovered, source });
  latest.current = { placed, composer, onAdd, blocked, hovered, source };
  const able = () => !!latest.current.onAdd && !latest.current.blocked;

  function hostFor(key: string): HTMLDivElement {
    let host = hosts.current.get(key);
    if (!host) {
      host = document.createElement("div");
      host.className = "review-block-host";
      hosts.current.set(key, host);
    }
    return host;
  }
  const root = () => container?.querySelector<HTMLElement>(".markdown-content") ?? null;

  // Puts each card after its block, in the order of the threads, and draws the bars; moves nothing that is in place.
  function place() {
    const document = root();
    if (!container || !document) return;
    const blocks = documentBlocks(document);
    const items: { key: string; lines: Lines }[] = latest.current.placed.map(thread => ({ key: thread.id, lines: thread }));
    if (latest.current.composer) items.push({ key: composerKey, lines: latest.current.composer });
    const groups = new Map<HTMLElement, HTMLDivElement[]>();
    for (const item of items) {
      const index = placementBlock(blocks, item.lines);
      const after = index >= 0 ? cardPlace(blocks[index].element) : document;
      const group = groups.get(after) ?? [];
      group.push(hostFor(item.key));
      groups.set(after, group);
    }
    for (const [after, group] of groups) {
      // A thread past the last block (the text was cut) goes at the end of the document.
      if (after === document) { for (const host of group) if (host.parentElement !== document || host.nextSibling) document.append(host); continue; }
      let previous: Element = after;
      for (const host of group) { if (previous.nextSibling !== host) previous.after(host); previous = host; }
    }
    for (const [key, host] of hosts.current) if (!items.some(item => item.key === key)) { host.remove(); hosts.current.delete(key); }
    // The bars: one along the blocks of each thread, and the range being commented.
    const box = container.getBoundingClientRect();
    const span = (lines: Lines) => {
      const covered = blocks.filter(block => blockCommented(block, lines) && !blocks.some(outer => outer !== block && outer.element.contains(block.element) && blockCommented(outer, lines)));
      if (!covered.length) return null;
      const rects = covered.map(block => block.element.getBoundingClientRect());
      const top = Math.min(...rects.map(rect => rect.top)), bottom = Math.max(...rects.map(rect => rect.bottom));
      return { top: Math.round(top - box.top + container.scrollTop), height: Math.max(4, Math.round(bottom - top)) };
    };
    const next = [
      ...latest.current.placed.map(thread => { const at = span(thread); return at && { key: thread.id, kind: thread.state === "draft" ? "draft" as const : "thread" as const, ...at }; }),
      ...latest.current.composer ? [(() => { const at = span(latest.current.composer!); return at && { key: composerKey, kind: "range" as const, ...at }; })()] : [],
    ].filter((bar): bar is NonNullable<typeof bar> => !!bar);
    setBars(current => JSON.stringify(current) === JSON.stringify(next) ? current : next);
    // The blocks being commented are shown as one.
    for (const block of blocks) {
      const inRange = !!latest.current.composer && blockCommented(block, latest.current.composer);
      if (inRange !== block.element.hasAttribute("data-review-range")) block.element.toggleAttribute("data-review-range", inRange);
    }
  }
  const placeLatest = useRef(place); placeLatest.current = place;

  // A new render of the document, a change of its size: the cards go back after their blocks, the bars follow.
  useEffect(() => {
    if (!container) return;
    let frame = 0;
    const later = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => placeLatest.current()); };
    const observer = new MutationObserver(records => {
      // What changes inside a card is the card's own: nothing to place.
      if (records.every(record => record.target instanceof Element && record.target.closest(".review-block-host"))) return;
      placeLatest.current();
    });
    observer.observe(container, { childList: true, subtree: true });
    const sized = new ResizeObserver(later);
    sized.observe(container);
    return () => { observer.disconnect(); sized.disconnect(); cancelAnimationFrame(frame); };
  }, [container]);
  useLayoutEffect(() => { place(); });
  useEffect(() => { if (blocked) setComposer(null); }, [blocked]);
  useEffect(() => () => { for (const host of hosts.current.values()) host.remove(); hosts.current.clear(); }, []);

  function open(lines: Lines) {
    if (!able()) return;
    const next = { startLine: Math.min(lines.startLine, lines.endLine), endLine: Math.max(lines.startLine, lines.endLine) };
    setComposer(next);
    focusSoon(() => composerBox.current);
  }
  function focusSoon(find: () => HTMLElement | null, attempts = 12) {
    const element = find();
    if (element && element.offsetParent !== null) { element.scrollIntoView({ block: "nearest" }); element.focus({ preventScroll: true }); }
    else if (attempts > 0) requestAnimationFrame(() => focusSoon(find, attempts - 1));
  }
  // Back to the block a card is about: it takes the keyboard for a moment, as a line of the source would.
  function backToBlock(line: number) {
    const document = root();
    const block = document && documentBlocks(document).filter(item => item.start <= line && item.end >= line).sort((a, b) => (a.end - a.start) - (b.end - b.start))[0];
    const target = block?.element ?? container;
    if (!target) return;
    if (!target.hasAttribute("tabindex")) { target.tabIndex = -1; target.addEventListener("blur", () => target.removeAttribute("tabindex"), { once: true }); }
    target.focus({ preventScroll: false });
  }
  function clicked(lines: SourceBlock, extend: boolean) {
    const from = extend ? (latest.current.composer ? { start: latest.current.composer.startLine, end: latest.current.composer.endLine } : anchor.current) : null;
    const range = from ? blockRange(from, lines) : lines;
    anchor.current = from ?? lines;
    open({ startLine: range.start, endLine: range.end });
  }
  // The blocks of the selection of the page, when it is in the document.
  function selectedLines(): Lines | null {
    const document = root(), selection = window.getSelection();
    if (!document || !selection || selection.isCollapsed || !selection.anchorNode || !selection.focusNode
      || !document.contains(selection.anchorNode) || !document.contains(selection.focusNode)) return null;
    const blockOf = (node: Node) => (node instanceof Element ? node : node.parentElement)?.closest<HTMLElement>(`[${sourceLinesAttribute}]`);
    const first = blockOf(selection.anchorNode), second = blockOf(selection.focusNode);
    const lines = [first, second].map(element => element && documentBlocks(document).find(block => block.element === element)).filter((block): block is Block => !!block);
    if (!lines.length) return null;
    const range = lines.reduce<SourceBlock>((all, block) => blockRange(all, block), lines[0]);
    return { startLine: range.start, endLine: range.end };
  }
  function reveal(id: string): boolean {
    const card = cards.current.get(id);
    if (!card) return false;
    focusSoon(() => card);
    return true;
  }
  function step(direction: 1 | -1): boolean {
    const focused = [...cards.current].find(([, card]) => card.contains(document.activeElement))?.[0] ?? null;
    const line = latest.current.hovered?.lines.start ?? 0;
    const next = neighbourThread(latest.current.placed, focused ? { id: focused } : { path, line }, direction);
    return next ? reveal(next.id) : false;
  }
  const commands = useRef({ clicked, open, selectedLines, step });
  commands.current = { clicked, open, selectedLines, step };

  // The pointer over the blocks, and the keys of the document.
  useEffect(() => {
    if (!container) return;
    const moved = (event: PointerEvent) => {
      const target = event.target instanceof Element ? event.target : null;
      if (!target || target.closest(".review-block-gutter")) return;
      const document = root();
      const element = target.closest<HTMLElement>(`[${sourceLinesAttribute}]`);
      if (!document || !element || !document.contains(element) || element.closest(".review-block-host") || target.closest(".review-block-host")) {
        if (target.closest(".review-block-host")) setHovered(null);
        return;
      }
      const block = documentBlocks(document).find(item => item.element === element);
      if (!block) return;
      const box = container.getBoundingClientRect(), rect = element.getBoundingClientRect();
      const next = { lines: { start: block.start, end: block.end }, top: Math.round(rect.top - box.top + container.scrollTop), height: Math.round(rect.height) };
      setHovered(current => current && current.lines.start === next.lines.start && current.lines.end === next.lines.end && current.top === next.top ? current : next);
    };
    const left = () => setHovered(null);
    const typed = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || editable(event.target)
        || event.target instanceof Element && event.target.closest(".review-block-host")) return;
      const key = event.key.toLowerCase();
      if (key === "c" && able()) {
        const hover = latest.current.hovered;
        const lines = commands.current.selectedLines() ?? (hover ? { startLine: hover.lines.start, endLine: hover.lines.end } : null);
        if (!lines) return;
        commands.current.open(lines);
      } else if (key === "n" || key === "p") { if (!commands.current.step(key === "n" ? 1 : -1)) return; }
      else return;
      event.preventDefault(); event.stopPropagation();
    };
    container.addEventListener("pointermove", moved);
    container.addEventListener("pointerleave", left);
    container.addEventListener("keydown", typed);
    return () => { container.removeEventListener("pointermove", moved); container.removeEventListener("pointerleave", left); container.removeEventListener("keydown", typed); };
  }, [container]);

  const canAdd = !!onAdd && !blocked;
  const gutter = container ? <div className="review-block-gutter" aria-hidden={!canAdd || !hovered}>
    {bars.map(bar => <span key={bar.key} className="review-block-bar" data-kind={bar.kind} style={{ top: bar.top, height: bar.height }} />)}
    {hovered && canAdd && <Button className="review-block-add" size="small" variant="minimal" style={{ top: hovered.top }}
      aria-label={t("Comment on this block")} title={t("Comment on this block · Shift-click another block for a range")}
      icon={<AppIcon name="plus" size={13} />} onPointerDown={event => event.preventDefault()}
      onClick={event => clicked(hovered.lines, event.shiftKey)} />}
    {hovered && !canAdd && blocked && <span className="review-block-blocked" style={{ top: hovered.top }} title={blocked}><AppIcon name="info" size={12} /></span>}
  </div> : null;
  const portals = !container ? null : <>
    {placed.map(thread => createPortal(<ReviewThreadCard thread={thread} actions={actions} original={textLines(source, thread.startLine, thread.endLine)}
      cardRef={card => { if (card) cards.current.set(thread.id, card); else cards.current.delete(thread.id); }} onLeave={() => backToBlock(thread.endLine)} />,
      hostFor(thread.id), thread.id))}
    {composer && onAdd && createPortal(<ReviewCommentEditor key={`${composer.startLine}-${composer.endLine}`} label={threadLabel(t, composer, path)} lines={composer}
      editorRef={composerBox} original={textLines(source, composer.startLine, composer.endLine)} suggestButton={false}
      actions={controls => <>
        <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={12} />} disabled={controls.saving} onClick={controls.suggest}>{t("Suggest edit")}</Button>
        <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={12} />} disabled={controls.saving} title={t("Suggest deleting these lines")}
          onClick={() => controls.save("")}>{t("Delete block")}</Button>
      </>}
      onCancel={() => { const line = composer.startLine; setComposer(null); backToBlock(line); }}
      onSave={async (body, suggestion) => {
        const done = await onAdd(composer, body, suggestion);
        if (done) { const line = composer.endLine; setComposer(null); requestAnimationFrame(() => backToBlock(line)); }
        return done;
      }} />, hostFor(composerKey), composerKey)}
  </>;
  return { gutter, portals, comment: lines => { const chosen = lines ?? selectedLines() ?? (hovered ? { startLine: hovered.lines.start, endLine: hovered.lines.end } : null); if (chosen) open(chosen); },
    reveal, step, composing: composer !== null };
}
