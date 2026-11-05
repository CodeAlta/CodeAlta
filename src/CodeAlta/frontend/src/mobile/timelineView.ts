// The blocks of the timeline of a session on the phone, drawn as compact cards for a touch screen: a message, a message
// the host sent (a background job, a message of another agent, a reminder...), the folded activity of a turn with the
// images its tools gave the model and the runs of its scripts that have a card of their own, and the images of a message.
// A card is one line that opens on a tap; what is open stays open while the page shows the session.
import { activityDuration } from "../timelineActivity/activityText";
import { plainResult } from "../workflows/scriptResult";
import { copyText } from "./clipboard";
import type { ToolCallDetail, ToolOutputPage, ToolOutputPlace } from "./contract";
import { activityLine, el, icon } from "./dom";
import { previewLine, type HostPrompt } from "./hostPrompt";
import { linkedImageUrl, matchLinkedImages, savedImageUrl, splitFiles, type ImageItem } from "./imageModel";
import type { ImageViewer } from "./imageViewer";
import type { OutputViewer } from "./outputViewer";
import { activityFacts, canWithdraw, clockTime, outputPreview, readWholeOutput, subjectPath, type ActivityBlock, type MessageBlock, type PendingPrompt, type ToolRow, type WorkflowBlock } from "./timeline";
import { timelineIcon, type TimelineIconName } from "./timelineIcons";
import { hasRunBlock, runsUnderLine, type WorkflowCall, type WorkflowRun, type WorkflowState } from "./workflowRun";

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
  /** Reads a tool call, when its row opens. */
  readTool: (activityId: string, seq: number | undefined) => Promise<ToolCallDetail>;
  /** Reads a page of the output of a tool call, in its full-screen view. */
  readToolOutput: (activityId: string, seq: number | undefined, place: ToolOutputPlace) => Promise<ToolOutputPage>;
  /** Takes a pending prompt back, to forget it or to edit it in the composer. */
  withdraw: (key: string, then: "delete" | "edit") => void;
  viewer: ImageViewer;
  outputViewer: OutputViewer;
}>;

const pendingLabels: Readonly<Record<PendingPrompt["mode"], string>> = { send: "You", steer: "You · steering", queue: "You · queued" };

const stateIcons: Readonly<Record<WorkflowState, TimelineIconName>> = {
  pending: "circle", running: "spinner", waiting: "pause", completed: "check", failed: "close", canceled: "stop", interrupted: "stop",
};
const stateWords: Readonly<Record<WorkflowState, string>> = {
  pending: "Not started", running: "Running", waiting: "Waiting", completed: "Done", failed: "Failed", canceled: "Stopped", interrupted: "Interrupted",
};

const duration = (milliseconds: number | null | undefined): string | null => milliseconds == null ? null : activityDuration(milliseconds);
const endedPhase = (phase: string) => phase === "completed" || phase === "failed" || phase === "canceled";

/** Draws the blocks of the timeline, and answers the taps on what it drew. */
export function createTimelineView(options: TimelineViewOptions) {
  const { document } = options;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  // The cards open on this page, by key: a message of the host, a run of a script.
  let open = new Set<string>();
  // The results of scripts already drawn: such a card shows what the script presented, open as an answer, until a tap folds it.
  let shownResults = new Set<string>();
  // The images of each group (a message, an activity), which the viewer goes through.
  let groups = new Map<string, ImageItem[]>();
  // The last read of each tool call that is open: an answer to an earlier one is dropped.
  const toolReads = new Map<string, number>();

  /** Forgets what was open: another session is shown. */
  function reset() { open = new Set(); shownResults = new Set(); groups = new Map(); }

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
    const label = make("div", "msg-label", pending ? pendingLabels[pending.mode] : block.role === "user" ? "You" : block.role === "assistant" ? "Agent" : "Error");
    const clock = pending ? null : clockTime(block.at);
    if (clock) {
      const time = make("time", "msg-time", ` · ${clock}`);
      time.dateTime = block.at!;
      label.append(time);
    }
    if (pending && canWithdraw(pending)) {
      // A prompt with files is not edited here: its files are on the computer, and the composer could not send them again.
      const actions = make("span", "pending-actions");
      if (!pending.attachments?.length) actions.append(pendingAction(pending, "edit", "Edit this prompt"));
      actions.append(pendingAction(pending, "delete", "Delete this prompt"));
      label.append(actions);
    }
    item.append(label);
    const content = make("div", "markdown-content");
    const diagram = fillMessage(content, block, block.text);
    if (block.text.trim()) item.append(content);
    files(item, block);
    return { element: item, version: block.key, diagram };
  }

  function pendingAction(pending: PendingPrompt, then: "delete" | "edit", label: string): HTMLButtonElement {
    const action = button(`pending-${then}`, label);
    action.dataset.prompt = pending.key;
    action.dataset.then = then;
    action.append(icon(document, then === "delete" ? "trash" : "edit"));
    return action;
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
    if (host.kind === "workflow" && !shownResults.has(block.key)) { shownResults.add(block.key); open.add(block.key); }
    if (open.has(block.key)) openHost(item, block, host);
    return { element: item, version: block.key, diagram: false };
  }

  function openHost(item: HTMLElement, block: MessageBlock, host: HostPrompt) {
    const head = item.querySelector<HTMLButtonElement>(".host-head")!;
    head.setAttribute("aria-expanded", "true");
    item.dataset.open = "true";
    if (item.querySelector(":scope > .host-body")) return;
    const body = make("div", "host-body markdown-content");
    const value = host.kind === "workflow" ? host.result.result : null;
    // The result of a script: what it presented, read from its run when its records are there; a value for the user (a run
    // of the library the host started for them) as Markdown after it, any other value as data, folded, as it is.
    const forUser = host.kind === "workflow" && host.run?.forUser === true;
    const text = host.kind === "job" ? host.job.body || host.job.result
      : host.kind === "workflow" ? [host.run?.presented.length ? host.run.presented.join("\n\n") : host.body, forUser ? value ?? "" : ""].filter(part => part.trim()).join("\n\n")
      : host.body;
    if (host.kind === "job" && host.job.title) body.append(make("p", "host-result", host.job.result));
    const content = make("div");
    fillMessage(content, block, text);
    body.append(...Array.from(content.childNodes));
    if (host.kind === "workflow") {
      body.append(make("p", "host-result", host.result.status));
      if (value !== null && !forUser) {
        const folded = make("details", "host-value");
        folded.append(make("summary", null, "Result"), make("pre", null, plainResult(value)));
        body.append(folded);
      }
    }
    item.append(body);
    files(item, block);
  }

  // ---- The activity of a turn: its line, the images of its tools and the runs of its scripts with a card, then its rows when open ----

  // A call is a row, which a tap opens on what the call does: a call whose events have no id cannot be read.
  function toolRow(tool: ToolRow): HTMLDivElement {
    const element = make("div", "tool-call");
    element.dataset.key = tool.key;
    const row = tool.activityId ? button("tool-row") : make("div", "tool-row");
    if (tool.activityId) row.setAttribute("aria-expanded", "false");
    element.append(row);
    toolState(element, tool);
    return element;
  }

  const toolKey = (activityId: string) => `tool:${activityId}`;

  // What a call works on, on its line. A path keeps the name of its file when the line is too short for it.
  function subjectElement(subject: string): HTMLElement {
    const path = subjectPath(subject);
    if (!path) return make("span", "tool-subject", subject);
    const element = make("span", "tool-subject tool-path");
    element.append(make("span", "tool-path-folders", path.folders), make("span", "tool-path-file", path.file));
    return element;
  }

  // A row says its state by an icon, what the call works on, and how long the call took once it ended.
  function toolState(element: HTMLElement, tool: ToolRow) {
    element.dataset.phase = tool.phase;
    element.dataset.subject = tool.subject ?? "";
    const row = element.querySelector<HTMLElement>(":scope > .tool-row")!;
    row.dataset.phase = tool.phase;
    if (tool.activityId) row.dataset.activity = tool.activityId;
    if (tool.seq !== undefined) row.dataset.seq = String(tool.seq);
    const state: WorkflowState = tool.phase === "completed" ? "completed" : tool.phase === "failed" ? "failed" : tool.phase === "canceled" ? "canceled" : "running";
    const mark = timelineIcon(document, stateIcons[state], "tool-mark");
    mark.title = tool.phase;
    const took = Date.parse(tool.end ?? "") - Date.parse(tool.at ?? "");
    const time = state !== "running" && took >= 1000 ? duration(took) : null;
    row.replaceChildren(mark, make("strong", null, tool.title));
    if (tool.subject) row.append(subjectElement(tool.subject));
    row.append(make("span", "tool-phase", time ?? (state === "running" ? "" : tool.phase === "completed" ? "" : tool.phase)));
    row.setAttribute("aria-label", `${tool.title}${tool.subject ? ` ${tool.subject}` : ""}: ${tool.phase}`);
    // What is open follows the call: it says more once the call has ended, and the row comes open again when its activity does.
    if (tool.activityId && open.has(toolKey(tool.activityId))) void showTool(element, row, tool.activityId);
  }

  // Reads a call and shows it under its row. A read that a later one replaced, or that comes once the row closed, is dropped.
  async function showTool(element: HTMLElement, row: HTMLElement, activityId: string) {
    let panel = element.querySelector<HTMLElement>(":scope > .tool-detail");
    if (!panel) {
      panel = make("div", "tool-detail");
      panel.append(make("p", "tool-note", "Loading…"));
      element.append(panel);
    }
    row.setAttribute("aria-expanded", "true");
    const reading = (toolReads.get(activityId) ?? 0) + 1;
    toolReads.set(activityId, reading);
    const current = () => toolReads.get(activityId) === reading && open.has(toolKey(activityId));
    let content: Node[];
    try {
      const seq = Number(row.dataset.seq) || undefined;
      const call = await options.readTool(activityId, seq);
      if (!current()) return;
      const caption = () => {
        const subject = element.dataset.subject;
        return [make("strong", null, call.name ?? "Output"), ...(subject ? [subjectElement(subject)] : [])];
      };
      content = call.error ? [make("p", "tool-note", call.error)] : toolParts(call, element.dataset.subject, place => options.readToolOutput(activityId, seq, place), caption);
    } catch (error) {
      if (!current()) return;
      content = [make("p", "tool-note", error instanceof Error ? error.message : String(error))];
    }
    panel.replaceChildren(...content);
  }

  // Copies a value of a call, whole: selecting it is what is hard on a phone. The icon says how it went, and a failure says why under the line.
  function copyButton(label: string, value: () => string | Promise<string>): HTMLButtonElement {
    const item = button("tool-copy", `Copy ${label.toLowerCase()}`);
    const show = (state: "idle" | "copying" | "copied" | "failed") => {
      item.dataset.state = state;
      item.replaceChildren(timelineIcon(document, state === "copied" ? "check" : state === "failed" ? "close" : state === "copying" ? "spinner" : "copy"));
    };
    show("idle");
    let timer = 0, failure: HTMLElement | null = null;
    item.addEventListener("click", async () => {
      if (item.dataset.state === "copying") return;
      document.defaultView!.clearTimeout(timer);
      failure?.remove();
      show("copying");
      try {
        await copyText(document.defaultView!, value());
        show("copied");
        timer = document.defaultView!.setTimeout(() => show("idle"), 1500);
      } catch (error) {
        show("failed");
        failure = make("p", "tool-note tool-bad", `Could not copy: ${error instanceof Error ? error.message : String(error)}`);
        item.parentElement?.after(failure);
      }
    });
    return item;
  }

  // The name of a part of a call, with the button that copies it.
  function sectionTitle(label: string, value: () => string | Promise<string>): HTMLElement {
    const section = make("div", "tool-section", label);
    section.append(copyButton(label, value));
    return section;
  }

  // The few lines of an output, under its call; a tap on them reads it all, full screen. An output these lines hold is only shown.
  function outputParts(call: ToolCallDetail, read: (place: ToolOutputPlace) => Promise<ToolOutputPage>, caption: () => Node[]): Node[] {
    if (!call.output) return [];
    const output = call.output, fromEnd = !!call.command, length = call.outputLength ?? output.length;
    const preview = outputPreview(output, length, fromEnd);
    const section = sectionTitle("Output", () => output.length >= length ? output : readWholeOutput(read, length));
    if (preview.whole) return [section, make("pre", "tool-text", preview.text)];
    const more = `Read all ${length.toLocaleString()} characters`;
    const open = button("tool-output", more);
    open.append(make("span", "tool-output-text", preview.text), make("span", "tool-output-more", more));
    open.addEventListener("click", () => options.outputViewer.open(caption(), read, fromEnd));
    return [section, open];
  }

  // What a call does, each fact on its own line. Its long texts scroll, and say when only a part of them came.
  function toolParts(call: ToolCallDetail, subject: string | undefined, readOutput: (place: ToolOutputPlace) => Promise<ToolOutputPage>, caption: () => Node[]): Node[] {
    const parts: Node[] = [];
    const fact = (label: string, value: string, className?: string, copied = true) => {
      const line = make("div", "tool-fact");
      line.append(make("span", "tool-fact-label", label), make("code", className, value));
      if (copied) line.append(copyButton(label, () => value));
      parts.push(line);
    };
    const text = (label: string, value: string | null | undefined, length: number | undefined, kept: "start" | "end") => {
      if (!value) return;
      parts.push(sectionTitle(label, () => value), make("pre", "tool-text", value));
      if (length !== undefined && length > value.length) parts.push(make("p", "tool-note", `The ${kept === "start" ? "first" : "last"} ${value.length.toLocaleString()} of ${length.toLocaleString()} characters.`));
    };
    // The row is a button, whose text cannot be selected: the path it shows comes here, unless a list of files below has it.
    if (subject && subjectPath(subject) && !call.readFiles?.includes(subject) && !call.modifiedFiles?.includes(subject)) fact("Path", subject);
    const folder = call.workingDirectory || call.sessionFolder;
    if (folder) fact("Folder", folder);
    if (call.exitCode !== null && call.exitCode !== undefined) fact("Exit code", String(call.exitCode), call.exitCode !== 0 ? "tool-bad" : undefined, false);
    if (call.failure) fact("Error", call.failure, "tool-bad");
    if (call.command) text("Command", call.command, undefined, "start");
    else text("Arguments", call.arguments, call.argumentsLength, "start");
    text("Modified files", call.modifiedFiles?.join("\n"), undefined, "start");
    text("Read files", call.readFiles?.join("\n"), undefined, "start");
    text("Diff", call.diff, call.diffLength, "start");
    parts.push(...outputParts(call, readOutput, caption));
    if (!parts.length) parts.push(make("p", "tool-note", "The call says nothing more yet."));
    return parts;
  }

  function activityBody(block: ActivityBlock, body: HTMLElement) {
    // A run of a script that only made calls is in the place of its call, and folds with the activity.
    const cards = new Map((block.workflows ?? []).filter(run => run.callId && !hasRunBlock(run)).map(run => [run.callId!, run]));
    // Rows are kept by key: a thought never changes, a tool changes its phase; new rows are added after. The card of a run is
    // drawn again, as the version of the activity changes with its runs.
    const existing = new Map(Array.from(body.children as HTMLCollectionOf<HTMLElement>).map(child => [child.dataset.key!, child]));
    let previous: Element | null = null;
    for (const entry of block.rows) {
      let element = existing.get(entry.key);
      const run = entry.type === "tool" && entry.activityId ? cards.get(entry.activityId) : undefined;
      if (run) {
        const card = workflowCard(run);
        card.dataset.key = entry.key;
        element?.replaceWith(card);
        element = card;
      } else if (entry.type === "tool" && element?.classList.contains("wf-card")) {
        // The run got a card of its own under the line: its call is a row again.
        const row = toolRow(entry);
        element.replaceWith(row);
        element = row;
      } else if (!element) {
        if (entry.type === "thought") {
          element = make("div", "msg msg-reasoning");
          element.dataset.key = entry.key;
          const content = make("div", "markdown-content");
          const fill = () => options.fill(content, entry.text, fill);
          fill();
          element.append(make("div", "msg-label", "Thinking"), content);
        } else element = toolRow(entry);
      } else if (entry.type === "tool" && (element.dataset.phase !== entry.phase || element.dataset.subject !== (entry.subject ?? ""))) toolState(element, entry);
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
    // What shows folded or not: the runs of its scripts with a card of their own (folded, also those that go on), then the
    // images its tools gave the model. The other runs fold with the activity, in the place of their call (#323).
    let extras = item.querySelector<HTMLElement>(":scope > .act-extras");
    const runs = runsUnderLine(block.workflows ?? [], isOpen);
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

  // ---- A run of a script: one line (its state, its title, its progress and the step it is at), which opens on its steps, sessions and calls ----

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
    // What the script presented shows whole, open or not: it is what the run says to the user.
    if (run.presented.length) {
      const parts = make("div", "wf-presented");
      for (const part of run.presented) {
        const content = make("div", "markdown-content");
        const redraw = () => { options.fill(content, part, redraw); };
        options.fill(content, part, redraw);
        parts.append(content);
      }
      card.append(parts);
    }
    if (isOpen) card.append(workflowBody(run));
    return card;
  }

  // What the line under the title says: what the run waits for, the step (or, without steps, the call) it is at, or how it ended.
  function workflowLine(run: WorkflowRun): string {
    if (run.waiting?.kind === "question") return "Waits for your answer";
    if (run.waiting?.kind === "approval") return "Waits for an approval";
    if (run.waiting?.kind === "sleep") {
      const until = Date.parse(run.waiting.until ?? "");
      return Number.isFinite(until) ? `Sleeps until ${new Date(until).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : "Sleeps";
    }
    if (run.state === "running") {
      const current = run.current ?? (run.steps.length ? null : run.calls.filter(call => !endedPhase(call.phase)).at(-1)?.title);
      return current ? `▸ ${current}` : "Running…";
    }
    if (run.state === "failed" && run.message) return run.message;
    const took = duration(run.durationMs ?? (Date.parse(run.endedAt ?? "") - Date.parse(run.startedAt ?? "")));
    const sessions = run.sessions.length ? `${run.sessions.length} session${run.sessions.length === 1 ? "" : "s"}` : null;
    // Without steps, the calls say what the script did.
    const calls = !run.steps.length && run.calls.length ? `${run.calls.length} call${run.calls.length === 1 ? "" : "s"}` : null;
    return [stateWords[run.state], took, sessions, calls].filter(Boolean).join(" · ");
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
    if (run.calls.length) {
      body.append(make("div", "wf-section", "Calls"));
      const list = make("ol", "wf-calls");
      const live = run.state === "running" || run.state === "waiting";
      for (const call of run.calls) list.append(callRow(call, live));
      body.append(list);
    }
    if (run.state === "failed" && run.message) body.append(make("p", "wf-message", run.message));
    if (!run.steps.length && !run.sessions.length && !run.calls.length && !(run.state === "failed" && run.message))
      body.append(make("p", "wf-empty", run.state === "running" || run.state === "waiting" ? "The script made no call yet." : "The script made no call."));
    return body;
  }

  // A call of the script: its state, its title (the tool and what it was given), what it works on and how long it took.
  // A call still running in a run that ended was stopped with it.
  function callRow(call: WorkflowCall, live: boolean): HTMLLIElement {
    const state: WorkflowState = call.phase === "completed" ? "completed" : call.phase === "failed" ? "failed" : call.phase === "canceled" ? "canceled" : live ? "running" : "interrupted";
    const row = make("li", "wf-call");
    row.dataset.state = state;
    const mark = timelineIcon(document, stateIcons[state], "wf-step-mark");
    mark.title = stateWords[state];
    const took = Date.parse(call.end ?? "") - Date.parse(call.at ?? "");
    row.append(mark, make("span", "wf-step-title", call.title));
    if (call.subject) row.append(subjectElement(call.subject));
    row.append(make("span", "wf-step-time", state !== "running" && took >= 1000 ? duration(took) : ""));
    row.setAttribute("aria-label", `${call.title}${call.subject ? ` ${call.subject}` : ""}: ${stateWords[state]}`);
    return row;
  }

  /** Answers a tap the view draws for; says whether it did. */
  function click(event: Event): boolean {
    const target = event.target as Element | null;
    const withdraw = target?.closest<HTMLButtonElement>("button.pending-delete, button.pending-edit");
    if (withdraw?.dataset.prompt) { options.withdraw(withdraw.dataset.prompt, withdraw.dataset.then === "edit" ? "edit" : "delete"); return true; }
    const toolCall = target?.closest<HTMLButtonElement>("button.tool-row");
    if (toolCall?.dataset.activity) {
      const key = toolKey(toolCall.dataset.activity), element = toolCall.closest<HTMLElement>(".tool-call")!;
      if (open.has(key)) {
        open.delete(key);
        toolCall.setAttribute("aria-expanded", "false");
        element.querySelector(":scope > .tool-detail")?.remove();
      } else {
        open.add(key);
        void showTool(element, toolCall, toolCall.dataset.activity);
      }
      return true;
    }
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
