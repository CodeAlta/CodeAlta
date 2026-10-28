// The blocks of the timeline of a session on the phone, drawn as compact cards for a touch screen: a message, a message
// the host sent (a background job, a message of another agent, a reminder...), the folded activity of a turn with the
// images its tools gave the model and the runs of its scripts, and the images of a message. A card is one line that opens
// on a tap; what is open stays open while the page shows the session.
import { activityDuration } from "../timelineActivity/activityText";
import { activityLine, el, icon } from "./dom";
import { previewLine, type HostPrompt } from "./hostPrompt";
import { linkedImageUrl, matchLinkedImages, savedImageUrl, splitFiles, type ImageItem } from "./imageModel";
import type { ImageViewer } from "./imageViewer";
import { activityFacts, type ActivityBlock, type MessageBlock, type PendingPrompt, type ToolRow, type WorkflowBlock } from "./timeline";
import { timelineIcon, type TimelineIconName } from "./timelineIcons";
import type { WorkflowRun, WorkflowState } from "./workflowRun";

/** A block as the page holds it: its element, the version it was drawn at, and whether it has a diagram (drawn again with the theme). */
export type DrawnBlock = { element: HTMLLIElement; version: string; diagram: boolean };

/** What the view needs of the page. */
export type TimelineViewOptions = Readonly<{
  document: Document;
  /** Renders Markdown into an element; `redraw` is called once a diagram of it is drawn. Says whether it has a diagram. */
  fill: (element: HTMLElement, text: string, redraw: () => void) => boolean;
  /** The session the timeline shows. */
  session: () => string | null;
  /** The title of a session, when the page knows it. */
  sessionTitle: (id: string) => string | null;
  /** Opens a session on the phone. */
  openSession: (id: string) => void;
  viewer: ImageViewer;
}>;

const pendingLabels: Readonly<Record<PendingPrompt["mode"], string>> = { send: "You", steer: "You · steering", queue: "You · queued" };

const stateIcons: Readonly<Record<WorkflowState, TimelineIconName>> = {
  pending: "circle", running: "spinner", waiting: "pause", completed: "check", failed: "close", canceled: "stop", interrupted: "stop",
};
const stateWords: Readonly<Record<WorkflowState, string>> = {
  pending: "Not started", running: "Running", waiting: "Waiting", completed: "Done", failed: "Failed", canceled: "Stopped", interrupted: "Interrupted",
};

const duration = (milliseconds: number | null | undefined): string | null => milliseconds == null ? null : activityDuration(milliseconds);

/** Draws the blocks of the timeline, and answers the taps on what it drew. */
export function createTimelineView(options: TimelineViewOptions) {
  const { document } = options;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  // The cards open on this page, by key: a message of the host, a run of a script.
  let open = new Set<string>();
  // The images of each group (a message, an activity), which the viewer goes through.
  let groups = new Map<string, ImageItem[]>();

  /** Forgets what was open: another session is shown. */
  function reset() { open = new Set(); groups = new Map(); }

  function button(className: string, label?: string): HTMLButtonElement {
    const item = make("button", className);
    item.type = "button";
    if (label) item.setAttribute("aria-label", label);
    return item;
  }

  // ---- Images: a fixed box each, so the timeline does not move while they load; a tap shows them full screen ----

  function thumbnails(group: string, items: readonly ImageItem[], className: string): HTMLElement {
    groups.set(group, [...items]);
    const strip = make("div", `thumbs ${className}`);
    items.forEach((item, index) => {
      const thumb = button("thumb", `Show the image ${item.title}`);
      thumb.dataset.group = group;
      thumb.dataset.index = String(index);
      const image = make("img");
      image.alt = item.title;
      image.loading = "lazy";
      image.decoding = "async";
      image.src = item.url;
      image.addEventListener("error", () => thumb.classList.add("failed"), { once: true });
      thumb.append(image, timelineIcon(document, "image", "thumb-fallback"));
      strip.append(thumb);
    });
    return strip;
  }

  // The pictures of the disk an answer shows: those its event lists are read from the server, the others stay links.
  function dressLinkedImages(content: HTMLElement, block: MessageBlock) {
    const session = options.session();
    const links = Array.from(content.querySelectorAll<HTMLAnchorElement>("a:has(> img.markdown-local-image)"));
    if (!session || !links.length || !block.images?.length || block.seq === undefined) return;
    const matched = matchLinkedImages(links.map(link => link.getAttribute("href")), block.images);
    const items: ImageItem[] = [];
    links.forEach((link, index) => {
      const image = matched[index];
      if (!image) return;
      const title = link.querySelector("img")?.alt || image.title || "Image";
      const item = { key: `${block.key}-${image.id}`, title, url: linkedImageUrl(session, block.seq!, image.id) };
      const figure = button("md-image", `Show the image ${title}`);
      figure.dataset.group = block.key;
      figure.dataset.index = String(items.length);
      const picture = make("img");
      picture.alt = title;
      picture.loading = "lazy";
      picture.decoding = "async";
      picture.src = item.url;
      picture.addEventListener("error", () => figure.classList.add("failed"), { once: true });
      figure.append(picture, timelineIcon(document, "image", "thumb-fallback"));
      if (title) figure.append(make("span", "md-image-alt", title));
      link.replaceWith(figure);
      items.push(item);
    });
    if (items.length) groups.set(block.key, items);
  }

  function fillMessage(content: HTMLElement, block: MessageBlock, text: string): boolean {
    const redraw = () => { options.fill(content, text, redraw); dressLinkedImages(content, block); };
    const diagram = options.fill(content, text, redraw);
    dressLinkedImages(content, block);
    return diagram;
  }

  // ---- Messages ----

  /** A message of the timeline, or a prompt the session took that the journal does not hold yet. */
  function messageBlock(block: MessageBlock, pending?: PendingPrompt): DrawnBlock {
    if (block.host && !pending) return hostBlock(block, block.host);
    const item = make("li", `msg msg-${block.role}${pending ? " pending" : ""}`);
    item.dataset.key = block.key;
    item.append(make("div", "msg-label", pending ? pendingLabels[pending.mode] : block.role === "user" ? "You" : block.role === "assistant" ? "Agent" : "Error"));
    const content = make("div", "markdown-content");
    const diagram = fillMessage(content, block, block.text);
    if (block.text.trim()) item.append(content);
    files(item, block);
    return { element: item, version: block.key, diagram };
  }

  // The files of a message: its images as pictures, the others as chips.
  function files(item: HTMLElement, block: MessageBlock) {
    const session = options.session();
    if (!block.attachments?.length) return;
    const { images, others } = session ? splitFiles(block.attachments) : { images: [], others: block.attachments };
    if (images.length) item.append(thumbnails(block.key, images.map((file, index) => ({ key: `${block.key}-${index}`, title: file.title, url: savedImageUrl(session!, file.id) })), "msg-images"));
    if (others.length) {
      const chips = make("div", "msg-files");
      for (const file of others) chips.append(make("span", "attach-chip", file.title));
      item.append(chips);
    }
  }

  // A message nobody typed: one line that says what it is, which opens on its body.
  function hostBlock(block: MessageBlock, host: HostPrompt): DrawnBlock {
    const item = make("li", "msg msg-host");
    item.dataset.key = block.key;
    item.dataset.host = host.kind;
    const head = button("host-head");
    head.setAttribute("aria-expanded", "false");
    const mark = timelineIcon(document, host.icon, "host-icon");
    const text = make("span", "host-text");
    const title = make("span", "host-title");
    let preview: string | null = null;
    let chip: HTMLElement | null = null;
    if (host.kind === "job") {
      item.dataset.state = host.outcome.state;
      const primary = host.job.title ?? host.job.command ?? host.job.jobId;
      title.append(make("span", host.job.title ? "host-name" : "host-name host-code", primary));
      chip = make("span", `chip chip-${host.outcome.state}`);
      chip.append(timelineIcon(document, host.outcome.state === "succeeded" ? "check" : host.outcome.state === "stopped" ? "stop" : "close"));
      const facts = [host.outcome.exitCode !== null && host.outcome.state !== "succeeded" ? `exit ${host.outcome.exitCode}` : host.outcome.state !== "succeeded" ? host.outcome.label : null, host.outcome.duration]
        .filter((part): part is string => !!part);
      chip.append(make("span", null, facts.join(" · ") || host.outcome.label));
      chip.title = host.job.result;
      head.setAttribute("aria-label", `Background job ${primary}: ${host.job.result}`);
    } else if (host.kind === "agent") {
      title.append(make("span", "host-name", host.label ?? host.title));
      preview = previewLine(host.body);
    } else if (host.kind === "workflow") {
      item.dataset.state = host.result.phase === "completed" ? "succeeded" : host.result.phase === "canceled" ? "stopped" : "failed";
      title.append(make("span", "host-name", host.result.title ?? host.title));
      chip = make("span", `chip chip-${item.dataset.state}`);
      chip.append(timelineIcon(document, host.result.phase === "completed" ? "check" : host.result.phase === "canceled" ? "stop" : "close"),
        make("span", null, host.result.phase === "completed" ? "done" : host.result.phase === "canceled" ? "stopped" : "failed"));
      preview = previewLine(host.body);
    } else {
      title.append(make("span", "host-name", host.title));
      preview = previewLine(host.body);
    }
    if (host.kind === "workflow") title.prepend(make("span", "host-kind", "Workflow"));
    text.append(title);
    if (preview) text.append(make("span", "host-preview", preview));
    head.append(mark, text);
    if (chip) head.append(chip);
    head.append(icon(document, "chevron", "host-chevron"));
    const row = make("div", "host-row");
    row.append(head);
    // The session a message of an agent comes from: a tap opens it.
    if (host.kind === "agent" && host.sourceSessionId) {
      const from = button("host-source", "Open the session that sent it");
      from.dataset.session = host.sourceSessionId;
      from.append(timelineIcon(document, "branch"), make("span", null, options.sessionTitle(host.sourceSessionId) ?? "Session"), timelineIcon(document, "open"));
      row.append(from);
    }
    item.append(row);
    if (open.has(block.key)) openHost(item, block, host);
    return { element: item, version: block.key, diagram: false };
  }

  function openHost(item: HTMLElement, block: MessageBlock, host: HostPrompt) {
    const head = item.querySelector<HTMLButtonElement>(".host-head")!;
    head.setAttribute("aria-expanded", "true");
    item.dataset.open = "true";
    if (item.querySelector(":scope > .host-body")) return;
    const body = make("div", "host-body markdown-content");
    const text = host.kind === "job" ? host.job.body || host.job.result : host.body;
    if (host.kind === "job" && host.job.title) body.append(make("p", "host-result", host.job.result));
    const content = make("div");
    fillMessage(content, block, text);
    body.append(...Array.from(content.childNodes));
    item.append(body);
    files(item, block);
  }

  // ---- The activity of a turn: its line, the images of its tools and the runs of its scripts, then its rows when open ----

  function toolRow(tool: ToolRow): HTMLDivElement {
    const element = make("div", "tool-row");
    element.dataset.key = tool.key;
    toolState(element, tool);
    return element;
  }

  // A row says its state by an icon, and how long the call took once it ended.
  function toolState(element: HTMLElement, tool: ToolRow) {
    element.dataset.phase = tool.phase;
    const state: WorkflowState = tool.phase === "completed" ? "completed" : tool.phase === "failed" ? "failed" : tool.phase === "canceled" ? "canceled" : "running";
    const mark = timelineIcon(document, stateIcons[state], "tool-mark");
    mark.title = tool.phase;
    const took = Date.parse(tool.end ?? "") - Date.parse(tool.at ?? "");
    const time = state !== "running" && took >= 1000 ? duration(took) : null;
    element.replaceChildren(mark, make("strong", null, tool.title), make("span", "tool-phase", time ?? (state === "running" ? "" : tool.phase === "completed" ? "" : tool.phase)));
    element.setAttribute("aria-label", `${tool.title}: ${tool.phase}`);
  }

  function activityBody(block: ActivityBlock, body: HTMLElement) {
    // Rows are kept by key: a thought never changes, a tool changes its phase; new rows are added after.
    const existing = new Map(Array.from(body.children as HTMLCollectionOf<HTMLElement>).map(child => [child.dataset.key!, child]));
    let previous: Element | null = null;
    for (const entry of block.rows) {
      let element = existing.get(entry.key);
      if (!element) {
        if (entry.type === "thought") {
          element = make("div", "msg msg-reasoning");
          element.dataset.key = entry.key;
          const content = make("div", "markdown-content");
          const fill = () => options.fill(content, entry.text, fill);
          fill();
          element.append(make("div", "msg-label", "Thinking"), content);
        } else element = toolRow(entry);
      } else if (entry.type === "tool" && element.dataset.phase !== entry.phase) toolState(element, entry);
      const expected: ChildNode | null = previous ? previous.nextSibling : body.firstChild;
      if (expected !== element) body.insertBefore(element, expected);
      previous = element;
    }
  }

  /** Draws an activity in its element: its line, what shows under it folded or not, and its rows when it is open. */
  function drawActivity(item: HTMLLIElement, block: ActivityBlock, live: boolean, isOpen: boolean) {
    const facts = activityFacts(block, live);
    item.dataset.state = facts.state;
    item.dataset.expanded = String(isOpen);
    let toggle = item.querySelector<HTMLButtonElement>(":scope > .act-toggle");
    if (!toggle) {
      toggle = button("act-toggle");
      toggle.append(icon(document, "chevron", "act-chevron"));
      item.append(toggle);
    }
    toggle.setAttribute("aria-expanded", String(isOpen));
    toggle.querySelector(".act-line")?.remove();
    toggle.append(activityLine(document, facts, live, Date.now()));
    // What shows folded: the runs of its scripts, then the images its tools gave the model.
    let extras = item.querySelector<HTMLElement>(":scope > .act-extras");
    const runs = block.workflows ?? [];
    if (runs.length || block.images?.length) {
      if (!extras) { extras = make("div", "act-extras"); toggle.after(extras); }
      const cards = runs.map(run => workflowCard(run));
      const images = block.images?.length && options.session()
        ? [thumbnails(block.key, block.images.map(image => ({ key: image.key, title: image.title, url: savedImageUrl(options.session()!, image.id) })), "tool-images")] : [];
      extras.replaceChildren(...cards, ...images);
    } else extras?.remove();
    let body = item.querySelector<HTMLElement>(":scope > .act-body");
    if (isOpen) {
      if (!body) { body = make("div", "act-body"); item.append(body); }
      activityBody(block, body);
    } else body?.remove();
  }

  /** A run of a script whose call the timeline does not hold. */
  function workflowBlock(block: WorkflowBlock): DrawnBlock {
    const item = make("li", "activity wf-only");
    item.dataset.key = block.key;
    item.append(workflowCard(block.run));
    return { element: item, version: block.key, diagram: false };
  }

  /** Draws a run of a script again in its block. */
  function drawWorkflow(item: HTMLLIElement, block: WorkflowBlock) { item.replaceChildren(workflowCard(block.run)); }

  // ---- A run of a script: one line (its state, its title, its progress and the step it is at), which opens on its steps and sessions ----

  function workflowCard(run: WorkflowRun): HTMLElement {
    const key = `run:${run.runId}`;
    const isOpen = open.has(key);
    const card = make("div", "wf-card");
    card.dataset.state = run.state;
    card.dataset.run = run.runId;
    const head = button("wf-head");
    head.dataset.toggle = key;
    head.setAttribute("aria-expanded", String(isOpen));
    const text = make("span", "wf-text");
    const title = make("span", "wf-title");
    title.append(make("span", "wf-kind", "Workflow"), make("span", "wf-name", run.title ?? "Script"));
    const sub = make("span", "wf-sub", workflowLine(run));
    text.append(title, sub);
    head.append(timelineIcon(document, stateIcons[run.state], "wf-mark"), text);
    if (run.total > 0) head.append(make("span", "wf-progress", `${run.done}/${run.total}`));
    head.append(icon(document, "chevron", "wf-chevron"));
    head.setAttribute("aria-label", `Workflow ${run.title ?? ""}: ${stateWords[run.state]}${run.total ? `, ${run.done} of ${run.total} steps` : ""}`);
    card.append(head);
    if (run.total > 0) {
      const bar = make("div", "wf-bar");
      const fill = make("span");
      fill.style.width = `${Math.round(run.done / run.total * 100)}%`;
      bar.append(fill);
      card.append(bar);
    }
    if (isOpen) card.append(workflowBody(run));
    return card;
  }

  // What the line under the title says: what the run waits for, the step it is at, or how it ended.
  function workflowLine(run: WorkflowRun): string {
    if (run.waiting?.kind === "question") return "Waits for your answer";
    if (run.waiting?.kind === "approval") return "Waits for an approval";
    if (run.waiting?.kind === "sleep") {
      const until = Date.parse(run.waiting.until ?? "");
      return Number.isFinite(until) ? `Sleeps until ${new Date(until).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : "Sleeps";
    }
    if (run.state === "running") return run.current ? `▸ ${run.current}` : "Running…";
    if (run.state === "failed" && run.message) return run.message;
    const took = duration(run.durationMs ?? (Date.parse(run.endedAt ?? "") - Date.parse(run.startedAt ?? "")));
    const sessions = run.sessions.length ? `${run.sessions.length} session${run.sessions.length === 1 ? "" : "s"}` : null;
    return [stateWords[run.state], took, sessions].filter(Boolean).join(" · ");
  }

  function workflowBody(run: WorkflowRun): HTMLElement {
    const body = make("div", "wf-body");
    if (run.steps.length) {
      const steps = make("ol", "wf-steps");
      for (const step of run.steps) {
        const row = make("li", "wf-step");
        row.dataset.state = step.state;
        const name = make("span", "wf-step-title", step.title);
        if (step.count > 1) name.append(make("small", null, ` ×${step.count}`));
        const mark = timelineIcon(document, stateIcons[step.state], "wf-step-mark");
        mark.title = stateWords[step.state];
        row.append(mark, name, make("span", "wf-step-time", duration(step.durationMs) ?? (step.state === "waiting" ? "waiting" : "")));
        steps.append(row);
      }
      body.append(steps);
    }
    if (run.sessions.length) {
      body.append(make("div", "wf-section", "Sessions"));
      const list = make("ul", "wf-sessions");
      for (const session of run.sessions) {
        const row = make("li");
        const open = button("wf-session", `Open the session ${session.title ?? ""}`);
        open.dataset.session = session.sessionId;
        open.dataset.state = session.state;
        const mark = timelineIcon(document, stateIcons[session.state], "wf-step-mark");
        mark.title = stateWords[session.state];
        open.append(mark, make("span", "wf-session-title", session.title ?? options.sessionTitle(session.sessionId) ?? "Session"),
          make("span", "wf-step-time", duration(session.durationMs) ?? ""), timelineIcon(document, "chevron", "wf-session-go"));
        row.append(open);
        list.append(row);
      }
      body.append(list);
    }
    if (run.state === "failed" && run.message) body.append(make("p", "wf-message", run.message));
    if (!run.steps.length && !run.sessions.length && !(run.state === "failed" && run.message)) body.append(make("p", "wf-empty", "The script declared no step yet."));
    return body;
  }

  /** Answers a tap the view draws for; says whether it did. */
  function click(event: Event): boolean {
    const target = event.target as Element | null;
    const source = target?.closest<HTMLButtonElement>("button.host-source, button.wf-session");
    if (source?.dataset.session) { options.openSession(source.dataset.session); return true; }
    const shown = target?.closest<HTMLButtonElement>("button.thumb, button.md-image");
    if (shown?.dataset.group) {
      const items = groups.get(shown.dataset.group) ?? [];
      options.viewer.open(items, Number(shown.dataset.index ?? 0));
      return true;
    }
    const run = target?.closest<HTMLButtonElement>("button.wf-head");
    if (run?.dataset.toggle) {
      const key = run.dataset.toggle, card = run.closest<HTMLElement>(".wf-card")!;
      if (open.has(key)) open.delete(key); else open.add(key);
      const isOpen = open.has(key);
      run.setAttribute("aria-expanded", String(isOpen));
      card.querySelector(":scope > .wf-body")?.remove();
      if (isOpen) card.append(workflowBody(runOf(card)));
      return true;
    }
    const host = target?.closest<HTMLButtonElement>("button.host-head");
    const item = host?.closest<HTMLElement>("li.msg-host");
    if (host && item?.dataset.key) {
      const key = item.dataset.key;
      const block = blockOf(key);
      if (open.has(key)) {
        open.delete(key);
        host.setAttribute("aria-expanded", "false");
        delete item.dataset.open;
        item.querySelectorAll(":scope > .host-body, :scope > .thumbs, :scope > .msg-files").forEach(part => part.remove());
      } else if (block?.host) {
        open.add(key);
        openHost(item, block, block.host);
      }
      return true;
    }
    return false;
  }

  // The runs and the blocks drawn last, which a tap finds again by their keys.
  let runs = new Map<string, WorkflowRun>();
  let blocks = new Map<string, MessageBlock>();
  const runOf = (card: HTMLElement): WorkflowRun => runs.get(card.dataset.run ?? "")!;
  const blockOf = (key: string): MessageBlock | undefined => blocks.get(key);

  /** Tells the view the blocks of the timeline, before they are drawn. */
  function know(all: readonly (MessageBlock | ActivityBlock | WorkflowBlock)[]) {
    runs = new Map();
    blocks = new Map();
    for (const block of all) {
      if (block.kind === "message") blocks.set(block.key, block);
      else if (block.kind === "workflow") runs.set(block.run.runId, block.run);
      else for (const run of block.workflows ?? []) runs.set(run.runId, run);
    }
  }

  return { reset, know, messageBlock, drawActivity, workflowBlock, drawWorkflow, fillMessage, click };
}

/** The view of the timeline. */
export type TimelineView = ReturnType<typeof createTimelineView>;
