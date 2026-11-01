import { approvalWaiting } from "./approval";
import { askWaiting } from "./ask";
import { createCalmOrder, type CalmClock, type CalmTiming } from "./calmOrder";
import { activityLine, el, icon } from "./dom";
import { safeColor, sessionMeta, showSpaces } from "./sessionMeta";
import { ago, dotLabels, dotOf, type SessionStore, type SessionView } from "./sessionStore";
import { createFolds, sessionTree, subAgentWords, type FoldStore, type SubAgents, type TreeRow } from "./sessionTree";

/**
 * The list of sessions of the home page, drawn in `list`: a card per session, the sub-agents of a session under it (folded
 * under a summary row until opened), and an order that stays put while the user looks at it (`createCalmOrder`): a live update
 * changes a card in place; the cards slide to their new places once the list is quiet, keeping the card the user looks at
 * where it is on the screen.
 */
export function createSessionList(options: Readonly<{
  window: Window & typeof globalThis;
  list: HTMLUListElement;
  sessions: SessionStore;
  store: FoldStore;
  /** Whether the list is on screen: a touch or a scroll elsewhere does not hold its order. */
  visible: () => boolean;
  clock?: CalmClock;
  timing?: CalmTiming;
}>) {
  const { window, list, sessions } = options;
  const document = window.document;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  const folds = createFolds(options.store);
  const clock: CalmClock = options.clock ?? {
    now: () => Date.now(), setTimer: (callback, ms) => window.setTimeout(callback, ms), clearTimer: timer => window.clearTimeout(timer as number),
  };
  const calm = createCalmOrder({ clock, timing: options.timing, onSettle: () => render() });
  const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // A card: its title and the time, its facts on one line, then what it waits for or does. A sub-agent under its parent does
  // not repeat the space and the project of the parent; one shown on its own names its parent.
  function card(row: TreeRow, now: number): { element: HTMLDivElement; version: string } {
    const view = row.view, live = view.live;
    const state = dotOf(view, now), approving = approvalWaiting(live), waiting = approving || askWaiting(live);
    const when = live?.at ?? view.lastActivity;
    const parent = row.depth > 0 && view.parentId ? sessions.view(view.parentId) : null;
    const sameProject = !!parent && parent.projectId === view.projectId;
    const space = showSpaces(sessions.spaces) && !sameProject;
    const meta = sessionMeta(view, { space, live: true, project: !sameProject, child: false });
    const lineText = live?.line ?? view.summary;
    const version = JSON.stringify([state, waiting, approving, view.title, ago(when, now), live?.kind, lineText, live?.activity, meta, row.parentElsewhere,
      row.subAgents && [summaryVersion(row.subAgents, now), row.open]]);

    const element = make("div", `card ${state}${waiting ? " waiting" : ""}`);
    const anchor = make("a", "card-link");
    anchor.href = `#/session/${encodeURIComponent(view.id)}`;
    if (row.parentElsewhere) {
      const from = make("div", "parent-line");
      from.append(icon(document, "child"), make("span", null, row.parentElsewhere.title ? `Sub-agent of ${row.parentElsewhere.title}` : "Sub-agent"));
      anchor.append(from);
    }
    const head = make("div", "head");
    head.append(make("div", "title", view.title || "(untitled session)"));
    if (when) head.append(make("span", "ago", ago(when, now)));
    anchor.append(head);
    const facts = make("div", "meta");
    facts.title = dotLabels[state];
    facts.append(make("span", "dot"));
    for (const item of meta) {
      const span = make("span", `meta-item meta-${item.kind}`);
      if (item.kind === "space") {
        const mark = make("span", "space-dot");
        const color = safeColor(item.color);
        if (color) mark.style.setProperty("--space", color);
        span.append(mark);
      }
      if (item.kind === "worktree") span.append(icon(document, "branch"));
      if (item.kind === "agent") span.append(icon(document, "bot"));
      span.append(document.createTextNode(item.text));
      facts.append(document.createTextNode(" "), span);
    }
    anchor.append(facts);
    if (waiting) {
      const badge = make("span", "waiting-badge");
      badge.append(icon(document, approving ? "shield" : "ask"), make("span", null, approving ? "Waits for your approval" : "Waits for your answer"));
      anchor.append(badge);
    }
    if (live?.kind === "activity" && live.activity) anchor.append(activityLine(document, live.activity, live.activity.state === "running", now));
    else if (lineText) {
      const line = make("div", "line", lineText);
      line.dataset.kind = live?.line ? (live.kind ?? "assistant") : "summary";
      anchor.append(line);
    }
    // The archive of a swipe, for a keyboard and a screen reader: shown when it has the focus.
    const archive = make("button", "card-archive");
    archive.type = "button";
    archive.setAttribute("aria-label", `Archive “${view.title || "untitled session"}”`);
    archive.append(icon(document, "archive"));
    element.append(anchor, archive);
    if (row.subAgents) element.append(summaryRow(view.id, row.subAgents, row.open, now));
    return { element, version };
  }

  const summaryVersion = (subAgents: SubAgents, now: number) =>
    [subAgents.count, subAgents.running, subAgents.waiting, subAgents.children.map(child => [dotOf(child, now), askWaiting(child.live) || approvalWaiting(child.live)])];

  // The sub-agents of a session, in one row at the bottom of its card: how many, a dot for each, and what they do; a tap shows
  // them under it or folds them.
  function summaryRow(id: string, subAgents: SubAgents, open: boolean, now: number): HTMLButtonElement {
    const words = subAgentWords(subAgents);
    const button = make("button", "subagents");
    button.type = "button";
    button.dataset.parent = id;
    button.dataset.asks = subAgents.asks;
    button.setAttribute("aria-expanded", String(open));
    button.setAttribute("aria-label", `${open ? "Hide" : "Show"} ${words.count}${words.states.length ? `: ${words.states.join(", ")}` : ""}`);
    button.append(icon(document, "chevron", "subagents-chevron"), make("span", "subagents-count", words.count));
    const dots = make("span", "subagents-dots");
    dots.setAttribute("aria-hidden", "true");
    for (const child of subAgents.children.slice(0, 8)) dots.append(make("span", `dot ${askWaiting(child.live) || approvalWaiting(child.live) ? "waiting" : dotOf(child, now)}`));
    if (subAgents.children.length > 8) dots.append(make("span", "subagents-more", `+${subAgents.children.length - 8}`));
    button.append(dots);
    if (words.states.length) {
      const states = make("span", "subagents-states");
      words.states.forEach((text, index) => {
        if (index) states.append(document.createTextNode(" · "));
        states.append(make("span", text.includes("wait") ? "state-waiting" : "state-running", text));
      });
      button.append(states);
    }
    return button;
  }

  // The row of a card: what a swipe shows under it, and the lines that join a sub-agent to its parent, drawn in the room of
  // its indent and in the gap over it, so that they touch the card above.
  function rails(item: HTMLLIElement, row: TreeRow) {
    item.querySelector(".rails")?.remove();
    item.classList.toggle("sub", row.depth > 0);
    item.style.setProperty("--depth", String(row.depth));
    if (row.depth > 0) item.setAttribute("aria-level", String(row.depth + 1)); else item.removeAttribute("aria-level");
    if (row.depth === 0) return;
    const lines = make("span", "rails");
    lines.setAttribute("aria-hidden", "true");
    row.rails.forEach((on, level) => { if (on) { const rail = make("span", "rail"); rail.style.setProperty("--level", String(level)); lines.append(rail); } });
    const own = make("span", row.more ? "elbow goes-on" : "elbow");
    own.style.setProperty("--level", String(row.depth - 1));
    lines.append(own);
    item.prepend(lines);
  }

  function newRow(row: TreeRow, now: number): HTMLLIElement {
    const item = make("li", "row");
    item.dataset.id = row.view.id;
    const under = make("div", "swipe-under");
    under.setAttribute("aria-hidden", "true");
    const action = make("span", "swipe-act archive");
    action.append(icon(document, "archive"), make("span", null, "Archive"));
    under.append(action);
    const { element, version } = card(row, now);
    item.dataset.version = version;
    item.append(under, element);
    return item;
  }

  // The card the user looks at: the first one under the top bar, which stays where it is on the screen when cards come or
  // go above it. At the top of the page with no finger down, a new session simply comes in at the top.
  function anchorOf(touching: boolean): { item: HTMLElement; top: number } | null {
    if (!touching && window.scrollY <= 0) return null;
    const bar = document.querySelector(".bar")?.getBoundingClientRect().bottom ?? 0;
    for (const item of Array.from(list.children) as HTMLElement[]) {
      const rect = item.getBoundingClientRect();
      if (rect.bottom > bar) return { item, top: rect.top };
    }
    return null;
  }

  let touching = false;

  /** Draws the list: rows are kept by id, a card is drawn again only when what it shows changed, and moved rows slide. */
  function render() {
    const now = Date.now();
    const views = sessions.list();
    const open = (id: string, asks: string) => folds.open(id, asks);
    const latest = sessionTree(views, { open });
    const rows = calm.held ? sessionTree(views, { open, held: calm.held }) : latest;
    const shown = options.visible() && list.children.length > 0;
    const before = new Map<HTMLElement, number>();
    if (shown) for (const item of Array.from(list.children) as HTMLElement[]) if (!item.classList.contains("collapsing")) before.set(item, item.getBoundingClientRect().top);
    const anchor = shown ? anchorOf(touching) : null;

    const old = new Map(Array.from(list.children as HTMLCollectionOf<HTMLLIElement>).map(item => [item.dataset.id!, item]));
    const items = rows.map(row => {
      let item = old.get(row.view.id);
      if (!item) item = newRow(row, now);
      else {
        const fresh = card(row, now);
        if (item.dataset.version !== fresh.version) {
          const current = item.querySelector<HTMLElement>(".card")!;
          current.className = fresh.element.className + (current.classList.contains("springing") ? " springing" : "");
          current.replaceChildren(...Array.from(fresh.element.childNodes));
          item.dataset.version = fresh.version;
        }
      }
      rails(item, row);
      return item;
    });
    const leaving = Array.from(list.children).filter(item => !items.includes(item as HTMLLIElement));
    items.forEach((item, index) => { if (list.children[index] !== item) list.insertBefore(item, list.children[index] ?? null); });
    for (const item of leaving) item.remove();
    calm.shown(rows.map(row => row.view.id), latest.map(row => row.view.id));

    if (anchor && anchor.item.isConnected) {
      const moved = anchor.item.getBoundingClientRect().top - anchor.top;
      if (Math.abs(moved) >= 1) window.scrollBy(0, moved);
    }
    if (!shown || reducedMotion()) return;
    // FLIP: each row that moved starts where it was and slides to its place; a new one fades in.
    for (const item of items) {
      const from = before.get(item);
      if (from === undefined) { item.classList.remove("entering"); void item.offsetWidth; item.classList.add("entering"); continue; }
      const dy = from - item.getBoundingClientRect().top;
      if (Math.abs(dy) < 1) continue;
      item.style.transition = "none";
      item.style.transform = `translateY(${dy}px)`;
      void item.offsetWidth;
      item.style.transition = "transform .32s cubic-bezier(.2, .8, .2, 1)";
      item.style.transform = "";
      item.addEventListener("transitionend", () => { item.style.transition = ""; }, { once: true });
    }
  }

  // A tap on the summary row of a session shows its sub-agents or folds them, and the choice is kept.
  list.addEventListener("click", event => {
    const button = (event.target as Element).closest<HTMLButtonElement>(".subagents");
    if (!button?.dataset.parent) return;
    event.preventDefault();
    folds.set(button.dataset.parent, button.getAttribute("aria-expanded") !== "true", button.dataset.asks ?? "");
    render();
  });
  // Nothing moves under a finger, nor while the list scrolls.
  list.addEventListener("pointerdown", () => { if (options.visible()) { touching = true; calm.down(); } });
  const lift = () => { if (touching) { touching = false; calm.up(); } };
  window.addEventListener("pointerup", lift);
  window.addEventListener("pointercancel", lift);
  window.addEventListener("scroll", () => { if (options.visible()) calm.scrolled(); }, { passive: true });

  return {
    render,
    /** Takes the latest order at once (the user came back to the list, or refreshed it), and draws it. */
    settle() { calm.settle(); touching = false; render(); },
    /** Whether a new order waits for the list to be quiet. */
    get waiting() { return calm.waiting; },
  };
}
