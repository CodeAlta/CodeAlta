import { pageFindScope, type FindScope } from "../findInPage/findInPage";
import { createFindBar } from "./findBar";
import type { ActivityBlock, TimelineBlock } from "./timeline";

/**
 * What is searched in the timeline of a session: what it shows. The line of a folded activity, the title of each call and
 * what it works on, and the heads of the cards are each a place of their own; the labels and times of the messages, the
 * facts of an activity and what an open call says (its arguments and its output, which the phone holds only a part of) are
 * not looked at, nor the buttons that are not the head of a card.
 */
export const timelineFindScope: FindScope = Object.freeze({
  blocks: `${pageFindScope.blocks}, .act-headline, .act-headline > small, .tool-row > strong, .tool-subject, .host-name, .host-preview, .host-kind, .wf-kind, .wf-name, .wf-sub, .wf-step-title, .attach-chip`,
  skipped: "button:not(.act-toggle, .tool-row, .host-head, .wf-head), .msg-label, .day-mark, .act-facts, .tool-phase, .tool-detail, .wf-progress, .pending-actions, script, style, [aria-hidden='true']",
});

/** The fewest characters of a text for which the folded activities that hold it open. */
export const findOpensFrom = 2;
/** How many pages of older messages a tap on "Search older messages" reads at most. */
export const searchedOlderPages = 20;

/** Whether the rows of an activity hold what is looked for: its thoughts, the titles of its calls and what they work on. */
export function activityHolds(block: ActivityBlock, pattern: RegExp): boolean {
  // `search` starts from the start of the text whatever the last index of a global pattern.
  return block.rows.some(row => row.type === "thought" ? row.text.search(pattern) >= 0 : row.title.search(pattern) >= 0 || (row.subject ?? "").search(pattern) >= 0);
}

/**
 * The search in the timeline of a session, on the pages it holds: its bar at the top of the screen, in place of the
 * composer, which comes back once it closes. The folded activities that hold what is looked for open while it is, in a set
 * of their own that leaves those the user opened as they are, and fold again when it closes. The older pages are read on
 * request, a few at a time.
 */
export function createSessionFind(options: Readonly<{
  window: Window & typeof globalThis;
  timeline: HTMLElement;
  /** Whether the session has older messages than those held. */
  hasOlder: () => boolean;
  /** Reads the page of older messages, or waits for the read that runs; the place shown stays. */
  readOlder: () => Promise<void>;
  /** The first sequence number held, which a page read moves. */
  firstHeld: () => number | null;
  /** Draws the timeline again: the folds open or close. */
  redraw: () => void;
  /** The search opened or closed: what sits on the bottom of the screen shows or hides. */
  layout: () => void;
}>) {
  const { window } = options, document = window.document;
  let looked: RegExp | null = null;
  // The activities that hold what is looked for that the user folded while it is.
  let folded = new Set<string>();
  const bar = createFindBar({
    window, label: "Find in this session", historyKey: "sessionFind", className: "session-find",
    page: () => options.timeline,
    scroller: () => (document.scrollingElement ?? document.documentElement) as HTMLElement,
    seen: element => {
      const viewport = window.visualViewport;
      return { top: element.getBoundingClientRect().bottom, bottom: viewport ? viewport.offsetTop + viewport.height : window.innerHeight };
    },
    scope: timelineFindScope, endFirst: true, refreshDelay: 200,
    partial: options.hasOlder,
    more: { label: "Search older messages", busy: "Reading older messages…", available: options.hasOlder, run: readOlder },
    onPattern: (pattern, text) => {
      looked = pattern && text.trim().length >= findOpensFrom ? pattern : null;
      folded = new Set();
      options.redraw();
    },
    onOpen: () => { document.body.classList.add("session-finding"); place(); options.layout(); },
    onClose: () => { document.body.classList.remove("session-finding"); looked = null; folded = new Set(); options.redraw(); options.layout(); },
  });
  document.body.append(bar.element);

  async function readOlder() {
    for (let pages = 0; pages < searchedOlderPages && bar.isOpen() && options.hasOlder(); pages++) {
      const first = options.firstHeld();
      await options.readOlder();
      if (options.firstHeld() === first) break;
    }
  }

  // The bar stays on the top of what is seen, where a browser that does not shrink the page for its keyboard has moved it.
  function place() {
    if (bar.isOpen()) bar.element.style.top = `${Math.max(0, window.visualViewport?.offsetTop ?? 0)}px`;
  }
  window.visualViewport?.addEventListener("resize", place);
  window.visualViewport?.addEventListener("scroll", place);

  // Whether the search opens an activity: it holds what is looked for, and the user neither opened it nor folded it since.
  const holds = (block: TimelineBlock) => looked !== null && block.kind === "activity" && activityHolds(block, looked);

  return {
    bar,
    /** Whether the search opens a block that the user did not open. */
    opens: (block: TimelineBlock, expanded: ReadonlySet<string>): boolean => !expanded.has(block.key) && !folded.has(block.key) && holds(block),
    /**
     * Answers a tap on the line of an activity that the search opens, or that the user folded while it is: it folds or
     * opens it again in the set of the search. False for any other, which the user's own set has.
     */
    toggle: (block: TimelineBlock, expanded: ReadonlySet<string>): boolean => {
      if (expanded.has(block.key) || !holds(block)) return false;
      if (!folded.delete(block.key)) folded.add(block.key);
      return true;
    },
  };
}

/** The search in the timeline of a session. */
export type SessionFind = ReturnType<typeof createSessionFind>;
