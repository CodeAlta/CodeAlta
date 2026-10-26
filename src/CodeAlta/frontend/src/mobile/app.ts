import { isImageAttachment } from "../promptImages";
import { addAttachments, hasImages, type PhoneAttachment } from "./attachments";
import { ApiError, browserStore, createApi, failureText, signedOutMessage, SignedOutError, takeFragmentToken } from "./api";
import { applyAppearance, fontStepKey, nextFontStep, readFontStep, readTheme, themeKey, type Theme } from "./appearance";
import { coalesce, composerView } from "./composer";
import { isBusy, type Live, type Options, type ProjectList, type PromptMode, type SessionChoices, type Space, type TimelineEvent } from "./contract";
import { activityLine, el, icon, iconSvg, type IconName } from "./dom";
import { createDrafts, newSessionDraft } from "./drafts";
import { installedApp, setUpInstall } from "./install";
import { createLiveStream, type LinkState } from "./liveStream";
import { createMarkdownView } from "./markdownView";
import {
  choiceLabel, choiceRequest, entryName, findModel, forgetMode, groupVariants, initialChoice, modelsOf, orderModels, pickEffort, pickModel, preferencesKey,
  providerName, providers, rememberChoice, tierChoice, tierLabel, tierReady, tierText, variantLabels, type Choice, type ProjectPreferences,
} from "./models";
import {
  appliedSelection, chipText, modeDescription, modeName, pickedChanges, pickSessionModel, providerPermissionMode, queueFirstKey, shownSelection, type Picked,
} from "./selection";
import { safeColor, sessionMeta, showSpaces } from "./sessionMeta";
import { createSheet } from "./sheet";
import { ago, dotLabels, dotOf, pageSize, readSizes, createSessionStore, type SessionView } from "./sessionStore";
import {
  activityFacts, appendEvents, blockVersion, foldEvents, liveBlock, pendingBlock, planTimeline, stillPending, type ActivityBlock, type PendingPrompt, type TimelineBlock, type ToolRow,
} from "./timeline";

/** Starts the phone page in a window whose document is `mobile.html`. */
export function startMobile(window: Window & typeof globalThis) {
  const document = window.document;
  const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  const store = browserStore(() => window.localStorage);
  const drafts = createDrafts(store);
  const sessions = createSessionStore();
  let signedOut = false;
  const api = createApi({ fetch: (input, init) => window.fetch(input, init), store, onSignedOut: () => showSignedOut() });
  const host = window.location.host;

  // ---- Appearance: the theme and the text size, kept on the phone ----
  let theme: Theme = readTheme(store, window.matchMedia("(prefers-color-scheme: light)").matches);
  let fontStep = readFontStep(store);
  const themeButton = $<HTMLButtonElement>("theme"), fontButton = $<HTMLButtonElement>("font");
  function showAppearance() {
    applyAppearance(document, theme, fontStep);
    themeButton.innerHTML = iconSvg(theme === "dark" ? "sun" : "moon"); // the icon shows what a tap switches to
    themeButton.title = theme === "dark" ? "Light mode" : "Dark mode";
    themeButton.setAttribute("aria-label", themeButton.title);
    fontButton.querySelector(".font-step")!.textContent = String(fontStep);
    fontButton.title = `Text size ${fontStep} of 5`;
    fontButton.setAttribute("aria-label", fontButton.title);
  }
  themeButton.addEventListener("click", () => {
    theme = theme === "dark" ? "light" : "dark";
    store.set(themeKey, theme);
    showAppearance();
    redrawDiagrams();
  });
  fontButton.addEventListener("click", () => { fontStep = nextFontStep(fontStep); store.set(fontStepKey, String(fontStep)); showAppearance(); padComposer(); });
  showAppearance();

  // ---- The link to the computer: one state, in the footer (under the composer on a session's page) and on the logo ----
  const foot = $("foot"), linkDot = $("link-dot");
  const link = { machine: "", state: "connecting" as LinkState | "signed-out", at: null as Date | null };
  function showLink() {
    const word = { connecting: "Connecting…", live: "Live", reconnecting: "Reconnecting…", offline: "Offline", "signed-out": "Not connected" }[link.state];
    const text = [link.machine, word, link.state === "live" && link.at ? link.at.toLocaleTimeString() : null].filter(Boolean).join(" · ");
    foot.replaceChildren(make("span", `link-dot ${link.state}`), document.createTextNode(text));
    linkDot.className = `link-dot ${link.state}`;
    linkDot.setAttribute("aria-label", text);
    linkDot.title = text;
  }
  showLink();

  // ---- Notes and the toast ----
  const note = $("note"), detailNote = $("detail-note"), newNote = $("new-note");
  const toastElement = $("toast");
  let toast: { id: string; timer: number } | null = null;
  function showToast(text: string, action?: { label: string; run: () => void }, ms = 5000) {
    toastElement.replaceChildren(make("span", "toast-text", text));
    if (action) {
      const button = make("button", "toast-action", action.label);
      button.type = "button";
      button.addEventListener("click", action.run);
      toastElement.append(button);
    }
    toastElement.hidden = false;
    toastElement.classList.remove("toast-in");
    void toastElement.offsetWidth;
    toastElement.classList.add("toast-in");
    if (toast) window.clearTimeout(toast.timer);
    toast = { id: "", timer: window.setTimeout(hideToast, ms) };
  }
  function hideToast() { if (toast) window.clearTimeout(toast.timer); toast = null; toastElement.classList.remove("toast-in"); toastElement.hidden = true; }

  function showSignedOut() {
    if (signedOut) return;
    signedOut = true;
    stream.stop();
    link.state = "signed-out";
    showLink();
    sessions.setFilter(sessions.filter);
    list.replaceChildren();
    moreButton.hidden = true;
    note.textContent = signedOutMessage;
    if (window.location.hash) window.location.hash = "#/";
  }

  // ---- The list of sessions ----
  const list = $<HTMLUListElement>("sessions"), moreButton = $<HTMLButtonElement>("more");
  const markdown = createMarkdownView({ window, upload: body => api.diagram(body), installed: () => installedApp(window), note: text => noteHere(text) });

  // The facts of a session, compact: the dot (with a word while it works), then its space, project, worktree, model and effort, agent and
  // whether it is a sub-agent; at most two lines, the time on the right.
  function metaLine(view: SessionView, options: Readonly<{ space: boolean; live: boolean; dot: boolean; when: string | null; now: number; selection?: boolean }>): HTMLDivElement {
    const line = make("div", "meta");
    const facts = make("div", "meta-facts");
    if (options.dot) facts.append(make("span", "dot"));
    for (const item of sessionMeta(view, options)) {
      const span = make("span", `meta-item meta-${item.kind}`);
      if (item.kind === "space") {
        const mark = make("span", "space-dot");
        const color = safeColor(item.color);
        if (color) mark.style.setProperty("--space", color);
        span.append(mark);
      }
      if (item.kind === "worktree") span.append(icon(document, "branch"));
      if (item.kind === "child") span.append(icon(document, "child"));
      if (item.kind === "agent") span.append(icon(document, "bot"));
      span.append(document.createTextNode(item.text));
      // A space between facts: where the line may break.
      if (facts.lastChild) facts.append(document.createTextNode(" "));
      facts.append(span);
    }
    line.append(facts);
    if (options.when) line.append(make("span", "ago", ago(options.when, options.now)));
    return line;
  }

  function card(view: SessionView, now: number): { element: HTMLDivElement; version: string } {
    const state = dotOf(view, now);
    const live = view.live;
    const when = live?.at ?? view.lastActivity;
    const lineText = live?.line ?? view.summary;
    const space = showSpaces(sessions.spaces);
    const version = JSON.stringify([state, view.title, ago(when, now), live?.kind, lineText, live?.activity, sessionMeta(view, { space, live: true }), space]);
    const element = make("div", `card ${state}`);
    const anchor = make("a", "card-link");
    anchor.href = `#/session/${encodeURIComponent(view.id)}`;
    anchor.append(make("div", "title", view.title || "(untitled session)"));
    const meta = metaLine(view, { space, live: true, dot: true, when, now });
    meta.title = dotLabels[state];
    anchor.append(meta);
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
    return { element, version };
  }

  function row(view: SessionView, now: number): HTMLLIElement {
    const item = make("li", "row");
    item.dataset.id = view.id;
    const under = make("div", "swipe-under");
    under.setAttribute("aria-hidden", "true");
    const action = make("span", "swipe-act archive");
    action.append(icon(document, "archive"), make("span", null, "Archive"));
    under.append(action);
    const { element, version } = card(view, now);
    item.dataset.version = version;
    item.append(under, element);
    return item;
  }

  // Rows are kept by id, and a card is drawn again only when what it shows changed: a state change transitions the
  // outline color instead of redrawing it, and the focus stays where it is.
  function renderList() {
    if (signedOut) return;
    const now = Date.now();
    const views = sessions.list();
    const old = new Map(Array.from(list.children as HTMLCollectionOf<HTMLLIElement>).map(item => [item.dataset.id!, item]));
    const rows = views.map(view => {
      const item = old.get(view.id);
      if (!item) return row(view, now);
      const fresh = card(view, now);
      if (item.dataset.version !== fresh.version) {
        const current = item.querySelector<HTMLElement>(".card")!;
        current.className = fresh.element.className + (current.classList.contains("springing") ? " springing" : "");
        current.replaceChildren(...Array.from(fresh.element.childNodes));
        item.dataset.version = fresh.version;
      }
      return item;
    });
    rows.forEach((item, index) => { if (list.children[index] !== item) list.insertBefore(item, list.children[index] ?? null); });
    Array.from(list.children).slice(rows.length).forEach(item => item.remove());
    moreButton.hidden = !sessions.hasMore;
    if (views.length) note.textContent = "";
  }

  let listError: string | null = null;
  const readList = coalesce(async () => {
    if (signedOut) return;
    const filter = sessions.filter, startedAt = sessions.beginRead();
    if (!sessions.count) note.textContent = "Loading…";
    try {
      let offset = 0, append = false;
      for (const size of readSizes(sessions.count)) {
        const page = await api.sessions(offset, size, filter);
        if (filter !== sessions.filter) return;
        sessions.applyPage(page, startedAt, append);
        listError = page.error ?? null;
        if (page.machine && page.machine !== link.machine) { link.machine = page.machine; showLink(); }
        append = true;
        offset = page.next;
        if (!page.hasMore) break;
      }
      renderList();
      showScope();
      if (!sessions.list().length) note.textContent = listError
        || (filter.length > 1 ? "No sessions in these projects." : filter.length ? "No sessions in this project." : "No sessions yet.");
    } catch (error) {
      if (error instanceof SignedOutError) return;
      note.textContent = `CodeAlta is not reachable: ${failureText(error, host)}`;
    }
  });
  let reloadTimer = 0;
  function scheduleListRead(ms: number) { window.clearTimeout(reloadTimer); reloadTimer = window.setTimeout(() => void readList(), ms); }

  moreButton.addEventListener("click", async () => {
    moreButton.disabled = true;
    moreButton.textContent = "Loading…";
    const filter = sessions.filter, startedAt = sessions.beginRead();
    try {
      const page = await api.sessions(sessions.next, pageSize, filter);
      if (filter === sessions.filter) { sessions.applyPage(page, startedAt, true); renderList(); }
    } catch (error) {
      if (!(error instanceof SignedOutError)) showToast(`Not loaded: ${failureText(error, host)}`);
    } finally {
      moreButton.disabled = false;
      moreButton.textContent = "More";
      moreButton.hidden = !sessions.hasMore || signedOut;
    }
  });

  // ---- The project filter: a sheet of the projects, any of them checked, and "All projects"; the choice is kept ----
  const projectKey = "alta.project";
  // The projects kept: a list, or the one project an earlier version kept.
  function keptProjects(): string[] {
    const raw = store.get(projectKey);
    if (!raw) return [];
    try {
      const value: unknown = JSON.parse(raw);
      return Array.isArray(value) ? value.filter((id): id is string => typeof id === "string" && id.length > 0) : [raw];
    } catch { return [raw]; }
  }
  let projects: ProjectList | null = null;
  async function readProjects(): Promise<ProjectList> {
    if (!projects) {
      const read = await api.projects();
      projects = { projects: read.projects ?? [], default: read.default ?? null, error: read.error ?? null };
    }
    return projects;
  }
  const filterButton = $<HTMLButtonElement>("filter");
  // The projects checked in the open sheet: the list keeps to them once the sheet closes.
  let checking: string[] | null = null;
  const filterSheet = createSheet(document, $("sheet"), () => {
    const chosen = checking;
    checking = null;
    if (chosen && (chosen.length !== sessions.filter.length || chosen.some(id => !sessions.filter.includes(id)))) chooseProjects(chosen);
  });
  function showFilter() {
    const chosen = sessions.filter;
    const names = chosen.map(id => projects?.projects.find(p => p.id === id)?.name ?? id);
    filterButton.querySelector<HTMLElement>(".badge")!.hidden = chosen.length === 0;
    const label = chosen.length ? `Filter by project: ${names.join(", ")}` : "Filter by project";
    filterButton.setAttribute("aria-label", label);
    filterButton.title = label;
    showScope();
  }
  // Where the phone is, at the top of the list: the computer and the spaces it sees.
  const spaceMark = (space: Space) => {
    const item = make("span", "scope-space");
    const dot = make("span", "space-dot");
    const color = safeColor(space.color);
    if (color) dot.style.setProperty("--space", color);
    item.append(dot, document.createTextNode(space.name));
    return item;
  };
  function showScope() {
    const where = $("scope-where");
    where.replaceChildren();
    if (!sessions.machine) return;
    where.append(icon(document, "computer"), make("span", "scope-machine", sessions.machine));
    const spaces = sessions.spaces;
    if (spaces.length) where.append(...spaces.map(spaceMark));
    else where.append(make("span", "scope-space", "All spaces"));
  }
  function chooseProjects(chosen: readonly string[]) {
    store.set(projectKey, JSON.stringify(chosen));
    sessions.setFilter(chosen);
    list.replaceChildren();
    showFilter();
    void readList();
  }
  filterButton.addEventListener("click", async () => {
    let data: ProjectList;
    try { data = await readProjects(); }
    catch (error) { if (!(error instanceof SignedOutError)) showToast(`Projects not read: ${failureText(error, host)}`); return; }
    checking = [...sessions.filter];
    // "All projects", then the projects checked, then the others: those checked are at hand to uncheck.
    const all = make("button", checking.length === 0 ? "on" : null, "All projects");
    all.type = "button";
    all.setAttribute("aria-pressed", String(checking.length === 0));
    all.addEventListener("click", () => { checking = []; filterSheet.close(); });
    const ordered = [...data.projects.filter(p => checking!.includes(p.id)), ...data.projects.filter(p => !checking!.includes(p.id))];
    const rows = ordered.map(project => {
      const item = make("li"), button = make("button", checking!.includes(project.id) ? "on" : null, project.name);
      button.type = "button";
      button.dataset.project = project.id;
      button.setAttribute("aria-pressed", String(checking!.includes(project.id)));
      // Its space when the phone sees several, then its folder.
      const space = showSpaces(sessions.spaces) && "space" in project ? project.space : null;
      if (project.path || space) {
        const small = make("small");
        if (space) small.append(spaceMark(space));
        if (project.path) small.append(document.createTextNode(project.path));
        button.append(small);
      }
      // A tap checks the project or unchecks it; the rows stay where they are while the sheet is open.
      button.addEventListener("click", () => {
        if (!checking) return;
        checking = checking.includes(project.id) ? checking.filter(id => id !== project.id) : [...checking, project.id];
        const on = checking.includes(project.id);
        button.classList.toggle("on", on);
        button.setAttribute("aria-pressed", String(on));
        all.classList.toggle("on", checking.length === 0);
        all.setAttribute("aria-pressed", String(checking.length === 0));
      });
      item.append(button);
      return item;
    });
    const first = make("li");
    first.append(all);
    $("sheet-list").replaceChildren(first, ...rows);
    filterSheet.open();
  });

  // ---- The page of a session: its timeline, folded as the desktop folds it, drawn block by block ----
  type Drawn = { element: HTMLLIElement; version: string; diagram: boolean };
  const timeline = $<HTMLOListElement>("timeline");
  const detail = {
    id: null as string | null, generation: 0, since: 0, replace: false, events: [] as TimelineEvent[],
    expanded: new Set<string>(), drawn: new Map<string, Drawn>(), loaded: false, blocks: [] as TimelineBlock[],
  };
  const pendingNotes = new Map<string, string>();
  // The prompts each session accepted that its timeline does not hold yet (a steered one reaches the journal later): shown at once.
  const pendingPrompts = new Map<string, PendingPrompt[]>();
  let pendingSeq = 0;
  const pendingLabels: Readonly<Record<PendingPrompt["mode"], string>> = { send: "You", steer: "You · steering", queue: "You · queued" };
  let working: HTMLLIElement | null = null;
  markdown.handle(timeline);

  const nearBottom = () => window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 40;
  const toBottom = () => window.scrollTo(0, document.documentElement.scrollHeight);
  function noteHere(text: string) { if (detail.id) detailNote.textContent = text; else showToast(text); }

  function messageBlock(block: Extract<TimelineBlock, { kind: "message" }>, pending?: PendingPrompt): Drawn {
    const item = make("li", `msg msg-${block.role}${pending ? " pending" : ""}`);
    item.dataset.key = block.key;
    item.append(make("div", "msg-label", pending ? pendingLabels[pending.mode] : block.role === "user" ? "You" : block.role === "assistant" ? "Agent" : "Error"));
    const content = make("div", "markdown-content");
    const fill = () => markdown.fill(content, block.text, fill);
    const diagram = fill();
    item.append(content);
    if (block.attachments?.length) {
      const files = make("div", "msg-files");
      for (const title of block.attachments) files.append(make("span", "attach-chip", title));
      item.append(files);
    }
    return { element: item, version: block.key, diagram };
  }

  function toolRow(tool: ToolRow): HTMLDivElement {
    const element = make("div", "tool-row");
    element.dataset.key = tool.key;
    element.dataset.phase = tool.phase;
    element.append(make("span", "tool-dot"), make("strong", null, tool.title), make("span", "tool-phase", tool.phase));
    return element;
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
          const fill = () => markdown.fill(content, entry.text, fill);
          fill();
          element.append(make("div", "msg-label", "Thinking"), content);
        } else element = toolRow(entry);
      } else if (entry.type === "tool" && element.dataset.phase !== entry.phase) {
        element.dataset.phase = entry.phase;
        element.querySelector(".tool-phase")!.textContent = entry.phase;
      }
      const expected: ChildNode | null = previous ? previous.nextSibling : body.firstChild;
      if (expected !== element) body.insertBefore(element, expected);
      previous = element;
    }
  }

  function drawActivity(item: HTMLLIElement, block: ActivityBlock, live: boolean, open: boolean) {
    const facts = activityFacts(block, live);
    item.dataset.state = facts.state;
    item.dataset.expanded = String(open);
    let toggle = item.querySelector<HTMLButtonElement>(":scope > .act-toggle");
    if (!toggle) {
      toggle = make("button", "act-toggle");
      toggle.type = "button";
      toggle.append(icon(document, "chevron", "act-chevron"));
      item.append(toggle);
    }
    toggle.setAttribute("aria-expanded", String(open));
    toggle.querySelector(".act-line")?.remove();
    toggle.append(activityLine(document, facts, live, Date.now()));
    let body = item.querySelector<HTMLElement>(":scope > .act-body");
    if (open) {
      if (!body) { body = make("div", "act-body"); item.append(body); }
      activityBody(block, body);
    } else body?.remove();
  }

  function drawTimeline() {
    if (!detail.id) return;
    const view = sessions.view(detail.id);
    const busy = isBusy(view?.live?.live);
    const blocks = detail.blocks = foldEvents(detail.events);
    type Entry = { block: TimelineBlock; live: boolean; open: boolean; key: string; version: string; prompt?: PendingPrompt };
    const entries: Entry[] = blocks.map((block, index) => {
      const live = liveBlock(blocks, index, busy), open = detail.expanded.has(block.key);
      return { block, live, open, key: block.key, version: blockVersion(block, live, open) };
    });
    const pending = stillPending(pendingPrompts.get(detail.id) ?? [], detail.events, busy, Date.now());
    pendingPrompts.set(detail.id, pending);
    const all: Entry[] = [...entries, ...pending.map(prompt => ({ block: pendingBlock(prompt), live: false, open: false, key: prompt.key, version: `${prompt.key}|${prompt.mode}`, prompt }))];
    const follow = nearBottom();
    const plan = planTimeline(new Map([...detail.drawn].map(([key, drawn]) => [key, drawn.version])), all);
    for (const key of plan.removed) { detail.drawn.get(key)?.element.remove(); detail.drawn.delete(key); }
    let previous: Element | null = null;
    plan.steps.forEach((step, index) => {
      const entry = all[index];
      let drawn = detail.drawn.get(step.key);
      if (step.action === "create" || !drawn) {
        if (entry.block.kind === "message") drawn = messageBlock(entry.block, entry.prompt);
        else {
          const item = make("li", "activity");
          item.dataset.key = entry.key;
          drawActivity(item, entry.block, entry.live, entry.open);
          drawn = { element: item, version: entry.version, diagram: false };
        }
        detail.drawn.set(step.key, drawn);
      } else if (step.action === "update" && entry.block.kind === "activity") {
        drawActivity(drawn.element, entry.block, entry.live, entry.open);
        drawn.version = entry.version;
      }
      const expected: ChildNode | null = previous ? previous.nextSibling : timeline.firstChild;
      if (expected !== drawn.element) timeline.insertBefore(drawn.element, expected);
      previous = drawn.element;
    });
    // While a turn runs and its activity has no event yet: a line that says it works.
    const placeholder = busy && !entries.at(-1)?.live;
    if (placeholder) {
      working ??= (() => {
        const item = make("li", "activity working");
        item.dataset.state = "running";
        const line = make("div", "act-toggle");
        line.append(activityLine(document, { headline: "Working…", thoughts: 0, tools: 0, succeeded: 0, failed: 0 }, true, Date.now()));
        item.append(line);
        return item;
      })();
      // Before the prompts that wait for their turn.
      timeline.insertBefore(working, pending.length ? detail.drawn.get(pending[0].key)?.element ?? null : null);
    } else { working?.remove(); working = null; }
    if (!all.length && !detailNote.textContent) detailNote.textContent = detail.loaded ? "No messages yet." : "Loading…";
    else if (all.length && (detailNote.textContent === "Loading…" || detailNote.textContent === "No messages yet.")) detailNote.textContent = "";
    padComposer();
    if (follow) toBottom();
  }

  // The running activity's time goes on between events.
  function tickLive() {
    if (!detail.id) return;
    const blocks = detail.blocks;
    const last = blocks.at(-1);
    if (!last || last.kind !== "activity" || !liveBlock(blocks, blocks.length - 1, isBusy(sessions.view(detail.id)?.live?.live))) return;
    const toggle = detail.drawn.get(last.key)?.element.querySelector(":scope > .act-toggle");
    const line = toggle?.querySelector(".act-line");
    if (line) line.replaceWith(activityLine(document, activityFacts(last, true), true, Date.now()));
  }

  timeline.addEventListener("click", event => {
    const toggle = (event.target as Element | null)?.closest<HTMLButtonElement>("button.act-toggle");
    const key = toggle?.closest<HTMLElement>("li.activity")?.dataset.key;
    if (!key) return;
    if (detail.expanded.has(key)) detail.expanded.delete(key); else detail.expanded.add(key);
    drawTimeline();
  });

  // A read gives the events after the last one held; a refresh reads them all again and replaces them once they come,
  // so the timeline is never drawn from half of them. A read of another generation (another session, a refresh since)
  // is dropped.
  const readDetail = coalesce(async () => {
    const id = detail.id;
    if (!id || signedOut) return;
    const generation = detail.generation, replace = detail.replace, since = replace ? 0 : detail.since;
    try {
      const page = await api.events(id, since);
      if (detail.id !== id || detail.generation !== generation) return;
      detail.events = replace ? appendEvents([], page.events ?? []) : appendEvents(detail.events, page.events ?? []);
      detail.since = Math.max(replace ? 0 : detail.since, page.since ?? since, detail.events.at(-1)?.seq ?? 0);
      detail.replace = false;
      detail.loaded = true;
      if (page.error) detailNote.textContent = page.error;
      drawTimeline();
    } catch (error) {
      if (detail.id !== id || error instanceof SignedOutError) return;
      detailNote.textContent = `Could not read the session: ${failureText(error, host)}`;
    }
  });
  let detailTimer = 0;
  function scheduleDetailRead(ms: number) { window.clearTimeout(detailTimer); detailTimer = window.setTimeout(() => void readDetail(), ms); }
  function refreshDetail() { if (!detail.id) return Promise.resolve(); detail.generation++; detail.replace = true; return readDetail(); }

  function showDetailHead() {
    if (!detail.id) return;
    const view = sessions.view(detail.id);
    $("detail-title").textContent = view?.title || "Session";
    // Where the prompt goes: space · project · worktree · sub-agent. The chip of the composer says what it runs on.
    const meta = view ? metaLine(view, { space: true, live: false, dot: false, when: null, now: Date.now(), selection: false }) : null;
    $("detail-meta").replaceChildren(...(meta ? Array.from(meta.querySelector(".meta-facts")!.childNodes) : []));
    showComposer();
  }

  // A session that is not in the list (filtered out, older, just created) is read alone, for its title and state.
  async function readSessionAlone(id: string) {
    try {
      const session = await api.session(id);
      sessions.applySession(session);
      if (detail.id === id) { showDetailHead(); drawTimeline(); }
    } catch (error) {
      if (detail.id !== id || error instanceof SignedOutError) return;
      detailNote.textContent = error instanceof ApiError && error.status === 404
        ? "This session is not on this phone: it was archived, or it is in a space the Mobile plugin is not in."
        : `Could not read the session: ${failureText(error, host)}`;
    }
  }

  function openDetail(id: string) {
    detail.id = id;
    detail.generation++;
    detail.since = 0;
    detail.replace = false;
    detail.events = [];
    detail.blocks = [];
    detail.expanded = new Set();
    detail.drawn = new Map();
    detail.loaded = false;
    timeline.replaceChildren();
    working = null;
    detailNote.textContent = pendingNotes.get(id) ?? "";
    pendingNotes.delete(id);
    bindComposer(id);
    showDetailHead();
    void readChoices(id);
    if (!sessions.listed(id) || !sessions.view(id)?.title) void readSessionAlone(id);
    void readDetail().then(() => { if (detail.id === id) toBottom(); });
  }
  function closeDetail() {
    bindComposer(null);
    detail.id = null;
    detail.generation++;
    detail.drawn = new Map();
    detail.blocks = [];
    timeline.replaceChildren();
    working = null;
  }

  function redrawDiagrams() {
    for (const drawn of detail.drawn.values()) {
      if (!drawn.diagram) continue;
      const content = drawn.element.querySelector<HTMLElement>(":scope > .markdown-content");
      const key = drawn.element.dataset.key!;
      const block = detail.blocks.find(b => b.key === key);
      if (content && block?.kind === "message") { const fill = () => markdown.fill(content, block.text, fill); fill(); }
    }
  }

  // ---- The composer: its text is the draft of the session it was typed for ----
  const composer = $<HTMLFormElement>("composer"), promptBox = $<HTMLTextAreaElement>("prompt");
  const sendButton = $<HTMLButtonElement>("send"), queueButton = $<HTMLButtonElement>("queue"), abortButton = $<HTMLButtonElement>("abort");
  const hint = $("composer-hint");
  const attachNote = $("attach-note"), attachStrip = $("attach-strip"), newAttachNote = $("new-attach-note"), newAttachStrip = $("new-attach-strip");
  const attachInput = $<HTMLInputElement>("attach-input");
  const attachmentsOf = new Map<string, PhoneAttachment[]>();
  let newFiles: PhoneAttachment[] = [];
  let attachTarget: "session" | "new" = "session";
  let composerFor: string | null = null;

  // Files and photos held with a prompt, as the desktop composer holds them. Images need a model that reads them.
  function paintAttachments(strip: HTMLElement, files: readonly PhoneAttachment[], replace: (next: PhoneAttachment[]) => void) {
    strip.replaceChildren(...files.map(file => {
      const chip = make("span", "attach-chip");
      const remove = document.createElement("button");
      remove.type = "button";
      remove.setAttribute("aria-label", `Remove ${file.title}`);
      remove.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>`;
      remove.addEventListener("click", () => replace(files.filter(item => item !== file)));
      chip.append(make("span", null, file.title), remove);
      return chip;
    }));
    strip.hidden = files.length === 0;
  }
  function drawAttachments() {
    paintAttachments(attachStrip, composerFor ? attachmentsOf.get(composerFor) ?? [] : [], next => {
      if (composerFor) attachmentsOf.set(composerFor, next);
      drawAttachments();
    });
    paintAttachments(newAttachStrip, newFiles, next => { newFiles = next; drawAttachments(); });
  }
  function showAttachNote(note: HTMLElement, notice: string | null) {
    note.textContent = notice ?? "";
    note.hidden = !notice;
  }
  async function takeFiles(list: FileList | null) {
    const chosen = list ? Array.from(list) : [];
    attachInput.value = "";
    if (!chosen.length) return;
    const session = attachTarget === "session" ? composerFor : null;
    const current = session ? attachmentsOf.get(session) ?? [] : newFiles;
    const allowed = session ? sessionImagesAllowed(session) : newImagesAllowed();
    const added = await addAttachments(current, chosen, allowed !== false);
    if (session) attachmentsOf.set(session, [...added.files]); else newFiles = [...added.files];
    drawAttachments();
    showAttachNote(session ? attachNote : newAttachNote, added.notice);
  }
  $<HTMLButtonElement>("attach").addEventListener("click", () => { attachTarget = "session"; attachInput.click(); });
  $<HTMLButtonElement>("new-attach").addEventListener("click", () => { attachTarget = "new"; attachInput.click(); });
  attachInput.addEventListener("change", () => { void takeFiles(attachInput.files); });
  const stopping = new Map<string, number>(), stoppedAt = new Map<string, number>();

  function bindComposer(id: string | null) {
    if (composerFor) drafts.set(composerFor, promptBox.value);
    composerFor = id;
    promptBox.value = id ? drafts.get(id) : "";
    grow();
    drawAttachments();
    showAttachNote(attachNote, null);
  }
  const buttonFaces: Readonly<Record<PromptMode, Readonly<{ icon: "send" | "steer" | "queue"; label: string; title: string }>>> = {
    send: { icon: "send", label: "Send", title: "Send the prompt" },
    steer: { icon: "steer", label: "Steer", title: "Send now, into the running turn" },
    queue: { icon: "queue", label: "Queue", title: "Runs after this turn" },
  };
  function face(button: HTMLButtonElement, mode: PromptMode) {
    const { icon: name, label, title } = buttonFaces[mode];
    button.innerHTML = iconSvg(name);
    button.append(make("span", null, label));
    button.title = title;
  }
  function showComposer() {
    const id = detail.id;
    const view = id ? sessions.view(id) : null;
    const busy = isBusy(view?.live?.live);
    if (id && !busy && stopping.has(id)) endStopping(id);
    const typed = promptBox.value.trim().length > 0;
    const filesHeld = !!id && (attachmentsOf.get(id)?.length ?? 0) > 0;
    const filesWait = filesHeld && busy;
    const state = composerView(busy, typed, !!view?.live?.canSteer, queueFirst);
    sendButton.hidden = state.primary === null;
    if (state.primary) face(sendButton, state.primary);
    queueButton.hidden = state.secondary === null;
    if (state.secondary) { face(queueButton, state.secondary); queueButton.dataset.mode = state.secondary; }
    abortButton.hidden = !state.stop;
    const halting = !!id && stopping.has(id);
    abortButton.disabled = halting;
    sendButton.disabled = filesWait;
    queueButton.disabled = filesWait;
    abortButton.querySelector("span")!.textContent = halting ? "Stopping…" : "Stop";
    hint.textContent = filesWait ? "Files wait until the turn ends." : state.hint;
    // The empty box says what the composer does now.
    promptBox.placeholder = filesWait ? "Files wait until the turn ends." : busy && !typed ? state.hint : "Ask for more…";
    composer.dataset.submit = state.submit;
    showSelection();
  }

  // ---- The selection of the composer: the chip (model and effort) and "⋯" (agent prompt, permissions, queue first) ----
  // What the session runs on comes from its choices (read when it opens, after each send), else from the list. What is picked on the
  // phone goes with the next send or queue, and then is the session's; a steer joins the running turn and leaves it for the next one.
  const choicesOf = new Map<string, SessionChoices>(), picks = new Map<string, Picked>();
  let queueFirst = store.get(queueFirstKey) === "1";
  const pickModelButton = $<HTMLButtonElement>("pick-model"), pickMoreButton = $<HTMLButtonElement>("pick-more");
  const sessionMoreSheet = createSheet(document, $("session-more-sheet"));
  const sessionAgent = $<HTMLSelectElement>("session-agent"), sessionMode = $<HTMLSelectElement>("session-mode"), queueFirstBox = $<HTMLInputElement>("session-queue-first");
  const readChoices = async (id: string) => {
    const before = JSON.stringify(choicesOf.get(id) ?? null);
    try {
      const read = await api.choices(id);
      choicesOf.set(id, { current: read.current, models: read.models ?? [], prompts: read.prompts ?? [], permissionModes: read.permissionModes ?? [], error: read.error ?? null });
      reconcileImages(id);
    } catch (error) {
      if (error instanceof SignedOutError) return;
      // An older CodeAlta has no choices: the chip keeps what the list says, and its sheet says why.
      choicesOf.set(id, { current: { provider: null, providerName: null, model: null, modelName: null, reasoning: null, agent: null, agentName: null, permissionMode: null },
        models: [], prompts: [], permissionModes: [], error: `The choices were not read: ${failureText(error, host)}` });
    }
    // An open sheet is drawn again only when the choices changed: drawing it moves the focus.
    if (detail.id === id && JSON.stringify(choicesOf.get(id) ?? null) !== before) {
      showSelection();
      if (modelSheet.shown && modelSheetFor === id) renderSessionModels();
      if (sessionMoreSheet.shown) renderSessionMore();
    }
  };
  const sessionShown = (id: string) => shownSelection(sessions.view(id), choicesOf.get(id)?.error ? null : choicesOf.get(id), picks.get(id));
  function tagsOf(tags: HTMLElement, items: readonly (readonly [IconName, string | null])[]) {
    tags.replaceChildren(...items.map(([name, text]) => {
      const tag = make("span");
      tag.append(icon(document, name));
      if (text) tag.append(document.createTextNode(` ${text}`));
      return tag;
    }));
  }
  function showSelection() {
    const id = detail.id;
    if (!id) return;
    const choices = choicesOf.get(id), picked = picks.get(id) ?? {};
    const shown = sessionShown(id), text = chipText(shown), changes = pickedChanges(choices, picked);
    const modelPending = "model" in changes || "reasoning" in changes, morePending = "agent" in changes || "permissionMode" in changes;
    $("pick-model-text").textContent = text.model;
    pickModelButton.classList.toggle("pending", modelPending);
    pickModelButton.setAttribute("aria-label", `Model: ${text.model}${modelPending ? ", for the next prompt" : ""}`);
    tagsOf($("pick-more-text"), [...(text.agent ? [["bot", text.agent] as const] : []), ...(shown.permissionMode ? [["shield", null] as const] : [])]);
    pickMoreButton.classList.toggle("pending", morePending);
    pickMoreButton.setAttribute("aria-label", ["More options", text.agent && `agent prompt ${text.agent}`,
      shown.permissionMode && `permissions ${modeName(shown.permissionMode, choices?.permissionModes)}`, morePending && "for the next prompt"].filter(Boolean).join(", "));
  }
  function pick(id: string, next: Picked) {
    picks.set(id, next);
    showSelection();
  }

  // The model sheet of a session: the efforts of its model, then the models of its provider.
  function renderSessionModels() {
    const id = modelSheetFor;
    if (!id) return;
    const choices = choicesOf.get(id), shown = sessionShown(id);
    $("model-provider").textContent = shown.providerName ?? "";
    $("tiers").hidden = true;
    $("new-variants").hidden = true;
    $("model-note").textContent = choices?.error ?? (choices ? "" : "Loading…");
    const model = choices?.models.find(m => m.id === shown.model);
    effortField(model?.efforts ?? [], shown.reasoning ?? "", effort => { pick(id, { ...picks.get(id), reasoning: effort }); renderSessionModels(); });
    const models = choices && !choices.error ? choices.models : [];
    const query = modelSearch.value.trim().toLowerCase();
    modelSearch.hidden = models.length <= 8;
    const { frontier, other } = orderModels(models.filter(m => !query || `${m.name} ${m.id}`.toLowerCase().includes(query)));
    $("model-list").replaceChildren(...modelItems(frontier, other, m => m.id === shown.model, m => m.name, m => m.id, m => {
      if (choices) pick(id, pickSessionModel(choices, picks.get(id) ?? {}, shown, m.id));
      renderSessionModels();
    }));
  }
  pickModelButton.addEventListener("click", () => {
    const id = detail.id;
    if (!id) return;
    modelSheetFor = id;
    modelSearch.value = "";
    renderSessionModels();
    modelSheet.open($("model-list").querySelector<HTMLElement>("button.on"));
    void readChoices(id);
  });

  // "⋯" of a session: its agent prompt, its permissions (when its provider has modes), and whether a prompt typed while it works is queued.
  function renderSessionMore() {
    const id = detail.id;
    if (!id) return;
    const choices = choicesOf.get(id), shown = sessionShown(id);
    const prompts = choices?.prompts.length ? choices.prompts : shown.agent ? [{ id: shown.agent, name: shown.agentName ?? shown.agent }] : [];
    sessionAgent.replaceChildren(...prompts.map(p => option(p.id, p.name, p.id === shown.agent)));
    sessionAgent.disabled = !choices?.prompts.length;
    const modes = choices?.permissionModes ?? [];
    $("session-mode-field").hidden = modes.length === 0;
    sessionMode.replaceChildren(option(providerPermissionMode, modeName(null), !shown.permissionMode),
      ...modes.map(m => option(m.id, modeName(m.id, modes), m.id === shown.permissionMode)));
    $("session-mode-text").textContent = modeDescription(shown.permissionMode, modes);
    queueFirstBox.checked = queueFirst;
  }
  pickMoreButton.addEventListener("click", () => {
    const id = detail.id;
    if (!id) return;
    renderSessionMore();
    sessionMoreSheet.open(sessionAgent);
    void readChoices(id);
  });
  sessionAgent.addEventListener("change", () => { const id = detail.id; if (id) pick(id, { ...picks.get(id), agent: sessionAgent.value }); });
  sessionMode.addEventListener("change", () => {
    const id = detail.id;
    if (!id) return;
    pick(id, { ...picks.get(id), permissionMode: sessionMode.value });
    $("session-mode-text").textContent = modeDescription(sessionMode.value === providerPermissionMode ? null : sessionMode.value, choicesOf.get(id)?.permissionModes);
  });
  queueFirstBox.addEventListener("change", () => { queueFirst = queueFirstBox.checked; store.set(queueFirstKey, queueFirst ? "1" : "0"); showComposer(); });
  $("session-more-done").addEventListener("click", () => sessionMoreSheet.close());
  function endStopping(id: string) {
    window.clearTimeout(stopping.get(id));
    stopping.delete(id);
    if (detail.id === id) showComposer();
  }

  async function submitPrompt(mode: "send" | "steer" | "queue") {
    const id = composerFor, text = promptBox.value.trim();
    const files = id ? [...(attachmentsOf.get(id) ?? [])] : [];
    if (!id || (!text && !files.length)) return;
    // Files go with a new prompt. While a turn runs they stay, and leave with Send once the session is idle.
    if (files.length && mode !== "send") { attachNote.textContent = "Attachments go with the next prompt, once the session is idle."; attachNote.hidden = false; return; }
    if (hasImages(files) && sessionImagesAllowed(id) === false) { attachNote.textContent = "This model does not read images."; attachNote.hidden = false; return; }
    composer.classList.add("sending");
    // The prompt leaves the input (and its draft) as it is sent: the request can take a while to return while the
    // turn already runs. If it fails, the text goes back to the session it was written for.
    promptBox.value = "";
    drafts.clear(id);
    attachmentsOf.set(id, []);
    drawAttachments();
    grow();
    showComposer();
    const sentAt = Date.now();
    // The prompt shows in the timeline at once, as the user's message: it goes when the journal holds it, or when the send fails.
    const prompt: PendingPrompt = { key: `p${++pendingSeq}`, text, mode, sentAt, after: detail.id === id ? detail.events.at(-1)?.seq ?? 0 : 0, ...(files.length ? { attachments: files.map(file => file.title) } : {}) };
    const forget = () => { pendingPrompts.set(id, (pendingPrompts.get(id) ?? []).filter(p => p !== prompt)); if (detail.id === id) drawTimeline(); };
    pendingPrompts.set(id, [...pendingPrompts.get(id) ?? [], prompt]);
    if (detail.id === id) { drawTimeline(); toBottom(); }
    // What was picked goes with a send or a queue; a steer leaves it for the next one.
    const picked = picks.get(id) ?? {}, selection = mode === "steer" ? {} : pickedChanges(choicesOf.get(id), picked);
    try {
      await api.prompt(id, text, mode, selection, files);
      if (mode !== "steer" && picks.get(id) === picked) picks.delete(id);
      if (Object.keys(selection).length) {
        // The session runs on it now: its choices say so.
        const choices = choicesOf.get(id);
        if (choices && !choices.error) choicesOf.set(id, { ...choices, current: appliedSelection(choices, selection) });
        void readChoices(id);
      }
      if (detail.id === id) {
        detailNote.textContent = "";
        showSelection();
        toBottom();
        scheduleDetailRead(300);
      }
    } catch (error) {
      // A turn the user stopped ends its request with an error: that prompt was sent, and is not given back.
      if ((stoppedAt.get(id) ?? 0) >= sentAt) { if (detail.id === id) detailNote.textContent = "Stopped."; return; }
      forget();
      if (composerFor === id && !promptBox.value.trim()) { promptBox.value = text; drafts.set(id, text); grow(); }
      else drafts.giveBack(id, text);
      if ((attachmentsOf.get(id) ?? []).length === 0 && files.length) { attachmentsOf.set(id, files); if (composerFor === id) drawAttachments(); }
      if (error instanceof SignedOutError) return;
      if (detail.id === id) detailNote.textContent = `Not sent: ${failureText(error, host)}`;
      else showToast(`Not sent to “${sessions.view(id)?.title || "the session"}”: ${failureText(error, host)} The text is kept as its draft.`, undefined, 8000);
    } finally {
      composer.classList.remove("sending");
      showComposer();
    }
  }
  composer.addEventListener("submit", event => {
    event.preventDefault();
    void submitPrompt((composer.dataset.submit as "send" | "steer" | "queue" | undefined) ?? "send");
  });
  // The second button: Queue, or Steer when the phone queues first.
  queueButton.addEventListener("click", () => void submitPrompt(queueButton.dataset.mode === "steer" ? "steer" : "queue"));
  promptBox.addEventListener("keydown", event => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); composer.requestSubmit(); }
  });
  // Stop: one tap, no confirmation; the button says "Stopping…" until the turn leaves the running state (or 15 s).
  abortButton.addEventListener("click", async () => {
    const id = detail.id;
    if (!id || stopping.has(id)) return;
    stoppedAt.set(id, Date.now());
    stopping.set(id, window.setTimeout(() => endStopping(id), 15_000));
    showComposer();
    try { await api.abort(id); }
    catch (error) {
      if (error instanceof SignedOutError) return;
      if (detail.id === id) detailNote.textContent = `Not stopped: ${failureText(error, host)}`;
      endStopping(id);
    }
  });
  // The box grows with its text up to 160 px; its borders count in its height (border-box), not in its scroll height.
  function grow() {
    promptBox.style.height = "auto";
    const borders = promptBox.offsetHeight - promptBox.clientHeight;
    promptBox.style.height = `${Math.min(promptBox.scrollHeight + borders, 160)}px`;
    padComposer();
  }
  promptBox.addEventListener("input", () => { if (composerFor) drafts.set(composerFor, promptBox.value); grow(); showComposer(); });
  // The composer sits on the keyboard: the visual viewport says how much of the layout viewport the keyboard covers.
  // The composer of a session, and the Start bar of a new one, sit on the keyboard: the visual viewport says how much of the layout
  // viewport the keyboard covers.
  function padComposer() {
    // The page of a session holds the footer in its composer; the new-session page has the footer under it, so the body makes the room.
    const creating = !$("create").hidden;
    const page = !$("detail").hidden ? $("detail") : creating ? document.body : null;
    if (!creating) document.body.style.paddingBottom = "";
    if (!page) return;
    const bar = creating ? $("create-bar") : composer;
    const viewport = window.visualViewport;
    const covered = viewport ? Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop) : 0;
    bar.style.bottom = `${covered}px`;
    page.style.paddingBottom = `${bar.offsetHeight + covered + (creating ? 0 : 16)}px`;
  }
  window.visualViewport?.addEventListener("resize", padComposer);
  window.visualViewport?.addEventListener("scroll", padComposer);

  $("detail-archive").addEventListener("click", () => {
    const id = detail.id;
    if (!id) return;
    window.location.hash = "#/";
    archive(id);
  });

  // ---- New session: the project and the prompt; on the bar, the chips of the selection beside Start ----
  // As the desktop: the model chip opens the model sheet, with the tiers of the settings at its top (a tier fills provider,
  // model and effort and stays marked until one of them is changed by hand), the effort as a stepped slider over the
  // efforts of the model, the variants of the model and the models of the provider. "⋯" opens the options: agent prompt,
  // provider (it filters the models), permissions (when the provider has modes) and a worktree. Choices are remembered per project.
  const newProject = $<HTMLSelectElement>("new-project"), newAgent = $<HTMLSelectElement>("new-agent"), newProvider = $<HTMLSelectElement>("new-provider");
  const newText = $<HTMLTextAreaElement>("new-text"), newWorktree = $<HTMLInputElement>("new-worktree"), newMode = $<HTMLSelectElement>("new-mode");
  // The model sheet serves the new session and the composer of a session: `modelSheetFor` is the session it shows, null for the new one.
  let modelSheetFor: string | null = null;
  const modelSheet = createSheet(document, $("model-sheet"), () => { modelSheetFor = null; }), modelSearch = $<HTMLInputElement>("model-search");
  const newMoreSheet = createSheet(document, $("new-more-sheet"));
  let options: Options | null = null;
  let choice: Choice = { tier: null, provider: "", model: "", effort: "" };
  // Images go only to a model that says it reads them. A model that does not say is treated as one that does not.
  // Null until the choices are known: a photo picked then is kept and checked when they arrive.
  function sessionImagesAllowed(id: string): boolean | null {
    const choices = choicesOf.get(id);
    if (!choices || choices.error || choices.models.length === 0) return null;
    return choices.models.find(model => model.id === sessionShown(id).model)?.imageInput === true;
  }
  function newImagesAllowed(): boolean | null {
    if (!options) return null;
    const id = choice.tier ? options.tiers.find(tier => tier.tier === choice.tier)?.model : choice.model;
    return options.models.find(model => model.id === id && model.provider === (choice.provider || model.provider))?.imageInput === true;
  }
  function reconcileImages(id: string) {
    if (sessionImagesAllowed(id) !== false) return;
    const files = attachmentsOf.get(id) ?? [];
    if (!hasImages(files)) return;
    const kept = files.filter(file => !isImageAttachment(file));
    attachmentsOf.set(id, kept);
    if (composerFor === id) {
      drawAttachments();
      showAttachNote(attachNote, kept.length ? "This model does not read images. The other files were attached." : "This model does not read images.");
    }
  }
  function reconcileNewImages() {
    if (newImagesAllowed() !== false || !hasImages(newFiles)) return;
    const kept = newFiles.filter(file => !isImageAttachment(file));
    newFiles = kept;
    drawAttachments();
    showAttachNote(newAttachNote, kept.length ? "This model does not read images. The other files were attached." : "This model does not read images.");
  }
  // The permission mode of the new session; "" is the provider's.
  let newPermission = "";
  const allPreferences = (): Record<string, ProjectPreferences> => { try { return JSON.parse(store.get(preferencesKey) ?? "{}") ?? {}; } catch { return {}; } };
  const projectPreferences = () => allPreferences()[newProject.value] ?? {};
  function remember(preferences: ProjectPreferences) {
    const all = allPreferences();
    all[newProject.value] = preferences;
    store.set(preferencesKey, JSON.stringify(all));
  }
  const option = (value: string, label: string, selected: boolean) => { const item = make("option", null, label); item.value = value; item.selected = selected; return item; };
  const offeredModes = () => options?.permissionModes?.[choice.provider] ?? [];
  const rememberNew = () => remember(rememberChoice(newAgent.value || undefined, choice, newPermission || undefined));

  function setChoice(next: Choice) {
    // A mode belongs to its provider.
    if (next.provider !== choice.provider) newPermission = "";
    choice = next;
    rememberNew();
    renderChoice();
  }

  // The stepped slider of the efforts of a model, in the model sheet; plain text when there is nothing to choose, nothing when it has none.
  function effortField(efforts: readonly string[], effort: string, onPick: (effort: string) => void) {
    const box = $("sheet-effort");
    $("sheet-effort-field").hidden = efforts.length === 0 && !effort;
    if (efforts.length < 2) { box.replaceChildren(make("span", "reasoning-static", effort || efforts[0] || "")); return; }
    const range = make("input");
    range.type = "range"; range.min = "0"; range.max = String(efforts.length - 1); range.step = "1";
    range.value = String(Math.max(0, efforts.indexOf(effort)));
    range.setAttribute("aria-labelledby", "sheet-effort-label");
    range.setAttribute("aria-valuetext", effort);
    const labels = make("div", "reasoning-labels");
    efforts.forEach((value, index) => {
      const button = make("button", value === effort ? "on" : null, value);
      button.type = "button";
      button.style.left = `${index / (efforts.length - 1) * 100}%`;
      button.addEventListener("click", () => onPick(value));
      labels.append(button);
    });
    range.addEventListener("input", () => onPick(efforts[Number(range.value)]));
    box.replaceChildren(range, labels);
  }

  // The rows of a model list: the latest of each line, then "Other models".
  function modelItems<T>(frontier: readonly T[], other: readonly T[], on: (model: T) => boolean, name: (model: T) => string, small: (model: T) => string, onPick: (model: T) => void): HTMLLIElement[] {
    return [...frontier, ...(other.length && frontier.length ? [null] : []), ...other].map(model => {
      if (model === null) { const item = make("li", "model-sep", "Other models"); item.setAttribute("role", "separator"); return item; }
      const mine = on(model);
      const button = make("button", mine ? "on" : null);
      button.type = "button";
      button.setAttribute("aria-pressed", String(mine));
      button.append(make("span", null, name(model)), make("small", null, small(model)));
      button.addEventListener("click", () => onPick(model));
      const item = make("li");
      item.append(button);
      return item;
    });
  }

  // The chips of the bar: the tier or the model with its effort; "⋯" with the agent prompt when it is not the default one, a branch
  // for a worktree, a shield for a permission mode.
  function renderChoice() {
    if (!options) return;
    const o = options;
    const label = [choiceLabel(o, choice), choice.effort].filter(Boolean).join(" · ");
    $("new-model-name").textContent = label;
    $("new-model").setAttribute("aria-label", `Model: ${label}`);
    const agent = newAgent.value && newAgent.value.toLowerCase() !== "default" ? o.prompts.find(p => p.id === newAgent.value)?.name ?? newAgent.value : null;
    const modes = offeredModes();
    tagsOf($("new-more-text"), [...(agent ? [["bot", agent] as const] : []), ...(newWorktree.checked ? [["branch", null] as const] : []), ...(newPermission ? [["shield", null] as const] : [])]);
    $("new-more").setAttribute("aria-label", ["More options", agent && `agent prompt ${agent}`, newWorktree.checked && "new worktree",
      newPermission && `permissions ${modeName(newPermission, modes)}`].filter(Boolean).join(", "));
    if (modelSheet.shown && modelSheetFor === null) renderNewModels();
    if (newMoreSheet.shown) renderNewMore();
  }

  // The model sheet of the new session: the tiers, the effort of the choice, the variants of its model, the models of its provider.
  function renderNewModels() {
    if (!options) return;
    const o = options, query = modelSearch.value.trim().toLowerCase();
    $("model-provider").textContent = providerName(o, choice.provider);
    $("model-note").textContent = "";
    const tiers = $("tiers");
    tiers.replaceChildren(...o.tiers.map(tier => {
      const on = tier.tier === choice.tier;
      const button = make("button", `tier${on ? " on" : ""}${tier.error ? " failed" : ""}`);
      button.type = "button";
      button.disabled = !tierReady(tier);
      button.setAttribute("aria-pressed", String(on));
      button.append(make("span", "tier-name", tierLabel(tier.tier)), make("span", "tier-model", tierText(o, tier)));
      if (tier.error) button.title = tier.error;
      button.addEventListener("click", () => { const next = tierChoice(o, tier.tier); if (next) setChoice(next); });
      return button;
    }));
    tiers.hidden = o.tiers.length === 0;
    const model = findModel(o, choice.provider, choice.model);
    effortField(model?.efforts ?? [], choice.effort, effort => { if (effort !== choice.effort || choice.tier) setChoice({ ...choice, effort, tier: null }); });
    // The options of the model: a checkbox per variant its entry has; a combination that does not exist is disabled.
    const groups = groupVariants(modelsOf(o, choice.provider)), byEntry = new Map(groups.map(g => [g.entry.id, g]));
    const group = groups.find(g => g.members.some(x => x.model.id === choice.model));
    const flags = group ? group.members.find(x => x.model.id === choice.model)!.flags.filter(f => !group.fixed.includes(f)) : [];
    const variants = $("new-variants");
    variants.hidden = !group || group.options.length === 0;
    variants.replaceChildren(...(group?.options ?? []).map(flag => {
      const on = flags.includes(flag), target = group!.find(on ? flags.filter(x => x !== flag) : [...flags, flag]);
      const box = make("input"), check = make("label", "check");
      box.type = "checkbox"; box.checked = on; box.disabled = !target;
      box.addEventListener("change", () => { if (target) setChoice({ tier: null, provider: choice.provider, model: target.id, effort: pickEffort(o, target, choice.effort) }); });
      check.append(box, document.createTextNode(` ${variantLabels[flag]}`));
      return check;
    }));
    modelSearch.hidden = groups.length <= 8;
    const { frontier, other } = orderModels(groups.filter(g => !query || g.members.some(x => `${x.model.name} ${x.model.id}`.toLowerCase().includes(query))).map(g => g.entry));
    $("model-list").replaceChildren(...modelItems(frontier, other, m => !choice.tier && byEntry.get(m.id)!.members.some(x => x.model.id === choice.model), entryName,
      m => m.id + (byEntry.get(m.id)!.options.length ? ` (${[...byEntry.get(m.id)!.options].sort().map(f => `+${f}`).join(", ")})` : ""),
      m => { if (choice.tier || !byEntry.get(m.id)!.members.some(x => x.model.id === choice.model)) setChoice({ tier: null, provider: choice.provider, model: m.id, effort: pickEffort(o, m, choice.effort) }); }));
  }
  $("new-model").addEventListener("click", () => {
    if (!options) return;
    modelSheetFor = null;
    modelSearch.value = "";
    renderNewModels();
    modelSheet.open($("model-list").querySelector<HTMLElement>("button.on") ?? $("tiers").querySelector<HTMLElement>("button.on"));
  });
  modelSearch.addEventListener("input", () => { if (modelSheetFor) renderSessionModels(); else renderNewModels(); });
  $("model-done").addEventListener("click", () => modelSheet.close());

  // "⋯" of the new session.
  function renderNewMore() {
    if (!options) return;
    const o = options, modes = offeredModes();
    newProvider.replaceChildren(...providers(o).map(p => option(p, providerName(o, p), p === choice.provider)));
    $("new-mode-field").hidden = modes.length === 0;
    newMode.replaceChildren(option("", modeName(null), !newPermission), ...modes.map(m => option(m.id, modeName(m.id, modes), m.id === newPermission)));
    $("new-mode-text").textContent = modeDescription(newPermission || null, modes);
  }
  $("new-more").addEventListener("click", () => {
    if (!options) return;
    renderNewMore();
    newMoreSheet.open(newAgent);
  });
  $("new-more-done").addEventListener("click", () => newMoreSheet.close());
  newProvider.addEventListener("change", () => {
    if (!options) return;
    const model = pickModel(options, newProvider.value);
    choice = { tier: null, provider: newProvider.value, model: model?.id ?? "", effort: pickEffort(options, model) };
    newPermission = "";
    // A provider choice forgets the model.
    remember({ ...(newAgent.value ? { agentPromptId: newAgent.value } : {}), providerKey: newProvider.value });
    renderChoice();
  });
  newAgent.addEventListener("change", () => { remember({ ...projectPreferences(), agentPromptId: newAgent.value }); renderChoice(); });
  newMode.addEventListener("change", () => { newPermission = newMode.value; rememberNew(); renderChoice(); });
  newWorktree.addEventListener("change", renderChoice);

  function initForm() {
    if (!options) return;
    const stored = projectPreferences();
    const { choice: first, remembered } = initialChoice(options, stored);
    choice = first;
    // A remembered mode stays while its provider offers it.
    newPermission = remembered.permissionMode && remembered.providerKey === choice.provider && offeredModes().some(m => m.id === remembered.permissionMode) ? remembered.permissionMode : "";
    // The space the session goes to, beside its project.
    const space = projects?.projects.find(p => p.id === newProject.value)?.space;
    $("new-space").replaceChildren(...(space ? [spaceMark(space)] : []));
    const agent = remembered.agentPromptId && options.prompts.some(p => p.id === remembered.agentPromptId) ? remembered.agentPromptId : options.defaults?.prompt || options.prompts[0]?.id || "";
    newAgent.value = agent;
    const kept = newPermission ? remembered : forgetMode(remembered);
    if (JSON.stringify(kept) !== JSON.stringify(stored)) remember(kept);
    renderChoice();
    // A default tier that cannot be used says why, beside the form.
    const tier = options.defaultTier ? options.tiers.find(t => t.tier === options!.defaultTier) : undefined;
    newNote.textContent = options.error || (tier && !tierReady(tier) && !choice.tier && !stored.providerKey
      ? `The ${tierLabel(tier.tier)} tier is not ready: ${tier.error || "it has no model"}.` : "");
  }

  async function openCreate(fresh: boolean) {
    newNote.textContent = options ? "" : "Loading…";
    try {
      const [projectList, read] = await Promise.all([readProjects(), options ? Promise.resolve(options) : api.options()]);
      options = {
        models: read.models ?? [], prompts: read.prompts ?? [], tiers: read.tiers ?? [], defaultTier: read.defaultTier ?? null, defaults: read.defaults ?? null,
        permissionModes: read.permissionModes ?? {}, error: read.error ?? null,
      };
      const keep = !fresh && projectList.projects.some(p => p.id === newProject.value) ? newProject.value : null;
      // A list kept to projects starts the session in the first one checked.
      const selected = keep ?? (fresh ? projectList.default : sessions.filter[0] ?? projectList.default);
      newProject.replaceChildren(...projectList.projects.map(p => option(p.id, p.name, p.id === selected)));
      newAgent.replaceChildren(...options.prompts.map(p => option(p.id, p.name, p.id === options!.defaults?.prompt)));
      if (!newText.value) newText.value = drafts.get(newSessionDraft);
      initForm();
      reconcileNewImages();
      padComposer();
    } catch (error) {
      if (!(error instanceof SignedOutError)) newNote.textContent = `Could not read the choices: ${failureText(error, host)}`;
    }
  }
  newProject.addEventListener("change", initForm);
  newText.addEventListener("input", () => drafts.set(newSessionDraft, newText.value));
  $("new-form").addEventListener("submit", async event => {
    event.preventDefault();
    const text = newText.value.trim();
    const files = [...newFiles];
    if ((!text && !files.length) || !newProject.value) return;
    if (hasImages(files) && newImagesAllowed() === false) { newAttachNote.textContent = "This model does not read images."; newAttachNote.hidden = false; return; }
    const start = $<HTMLButtonElement>("new-start");
    start.disabled = true;
    newNote.textContent = "Starting…";
    try {
      const created = await api.create({
        project: newProject.value, text, prompt: newAgent.value || undefined, ...choiceRequest(choice),
        ...(newPermission ? { permissionMode: newPermission } : {}), worktree: newWorktree.checked,
        ...(files.length ? { attachments: files } : {}),
      });
      newText.value = "";
      newFiles = [];
      drawAttachments();
      drafts.clear(newSessionDraft);
      newNote.textContent = "";
      // A prompt that did not go is shown on the page of its session, back in its composer.
      if (created.sendError) {
        drafts.set(created.id, text);
        if (files.length) attachmentsOf.set(created.id, files);
        pendingNotes.set(created.id, `Created, but the prompt was not sent: ${created.sendError}`);
      }
      scheduleListRead(0);
      window.location.hash = `#/session/${encodeURIComponent(created.id)}`;
    } catch (error) {
      if (!(error instanceof SignedOutError)) newNote.textContent = `Not started: ${failureText(error, host)}`;
    } finally { start.disabled = false; padComposer(); }
  });
  // "+" on the new-session screen starts the form again from the settings: the choices are read again (models, prompts,
  // projects), what was remembered for the project of the form is forgotten (not the other projects'), the prompt is cleared.
  $("new").addEventListener("click", () => {
    if (window.location.hash !== "#/new") { window.location.hash = "#/new"; return; }
    const all = allPreferences();
    delete all[newProject.value];
    store.set(preferencesKey, JSON.stringify(all));
    options = null;
    projects = null;
    newText.value = "";
    drafts.clear(newSessionDraft);
    newWorktree.checked = false;
    modelSheet.close();
    newMoreSheet.close();
    void openCreate(true);
  });

  // ---- Swipe a card for its action, Gmail-style: the drag is the action ----
  // Right: Archive, on green. Past 40% of the width a release archives and the card leaves; short of it the card springs
  // back. A drag that starts vertical is the page's scroll (touch-action: pan-y). A toast offers Restore for 5 s.
  let swipe: { row: HTMLElement; card: HTMLElement; x: number; y: number; dx: number; dragging: boolean; pointer: number } | null = null;
  function place(item: HTMLElement, x: number, how?: "spring" | "out") {
    const element = item.querySelector<HTMLElement>(".card")!;
    element.classList.toggle("springing", how === "spring");
    element.classList.toggle("leaving-x", how === "out");
    element.style.transform = x ? `translateX(${x}px)` : "";
    item.dataset.dir = x > 0 ? "archive" : "";
    item.dataset.armed = String(Math.abs(x) >= item.offsetWidth * 0.4);
  }
  list.addEventListener("pointerdown", event => {
    const element = (event.target as Element).closest<HTMLElement>(".card");
    if (!element || event.button || swipe || (event.target as Element).closest(".card-archive")) return;
    swipe = { row: element.parentElement!, card: element, x: event.clientX, y: event.clientY, dx: 0, dragging: false, pointer: event.pointerId };
  });
  list.addEventListener("pointermove", event => {
    if (!swipe || event.pointerId !== swipe.pointer) return;
    const dx = event.clientX - swipe.x, dy = event.clientY - swipe.y;
    if (!swipe.dragging) {
      if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) { swipe = null; return; } // a scroll
      if (Math.abs(dx) < 10) return;
      swipe.dragging = true;
      try { swipe.card.setPointerCapture(event.pointerId); } catch { /* the pointer is gone */ }
    }
    swipe.dx = dx;
    place(swipe.row, dx > 0 ? dx : dx * 0.15); // left resists: it has no action
  });
  function endSwipe(event: PointerEvent) {
    if (!swipe || event.pointerId !== swipe.pointer) return;
    const done = swipe;
    swipe = null;
    if (!done.dragging) return;
    done.row.dataset.dragged = "1";
    window.setTimeout(() => delete done.row.dataset.dragged, 0);
    if (event.type === "pointerup" && done.dx >= done.row.offsetWidth * 0.4) {
      place(done.row, done.row.offsetWidth + 24, "out");
      archive(done.row.dataset.id!);
    } else place(done.row, 0, "spring");
  }
  list.addEventListener("pointerup", endSwipe);
  list.addEventListener("pointercancel", endSwipe);
  // The tap that ends a drag does not open the session.
  list.addEventListener("click", event => { if ((event.target as Element).closest<HTMLElement>(".row")?.dataset.dragged) { event.preventDefault(); event.stopPropagation(); } }, true);
  list.addEventListener("click", event => {
    const button = (event.target as Element).closest<HTMLButtonElement>(".card-archive");
    const id = button?.closest<HTMLElement>(".row")?.dataset.id;
    if (id) archive(id);
  });

  const rowOf = (id: string) => list.querySelector<HTMLElement>(`.row[data-id="${CSS.escape(id)}"]`);
  function collapse(item: HTMLElement) { item.style.height = `${item.offsetHeight}px`; void item.offsetHeight; item.classList.add("collapsing"); item.style.height = "0px"; }
  function bringBack(id: string) {
    sessions.bringBack(id);
    const item = rowOf(id);
    if (item) { item.classList.remove("collapsing"); item.style.height = ""; place(item, 0, "spring"); }
    renderList();
  }
  function archive(id: string) {
    const title = sessions.view(id)?.title || "Session";
    sessions.hide(id);
    const item = rowOf(id);
    if (item) window.setTimeout(() => collapse(item), 180); else renderList();
    const archiving = api.archive(id).then(() => sessions.settled(id));
    archiving.catch(error => {
      if (toast?.id === id) hideToast();
      bringBack(id);
      if (!(error instanceof SignedOutError)) showToast(`Not archived: ${failureText(error, host)}`);
    });
    const restore = async () => {
      if (toast?.id !== id) return;
      hideToast();
      try { await archiving; await api.restore(id); }
      catch (error) { if (!(error instanceof SignedOutError)) showToast(`Not restored: ${failureText(error, host)}`); return; }
      bringBack(id);
      scheduleListRead(0);
    };
    showToast(`Archived “${title.length > 40 ? `${title.slice(0, 39)}…` : title}”`, { label: "Restore", run: () => void restore() });
    toast!.id = id;
  }

  // ---- Routes: #/ is the list, #/session/<id> a session, #/new the new-session form ----
  const back = $<HTMLButtonElement>("back");
  function route() {
    const hash = window.location.hash;
    // The link of the QR code opened in a page that already runs: sign in with it, then show the list.
    if (hash.startsWith("#k=")) { void signInFromAddress().then(ok => { if (ok) route(); }); return; }
    const found = /^#\/session\/(.+)$/u.exec(hash);
    let id: string | null = null;
    if (found) { try { id = decodeURIComponent(found[1]); } catch { id = null; } }
    const creating = hash === "#/new" && !signedOut;
    if (signedOut) id = null;
    $("home").hidden = !!id || creating;
    $("detail").hidden = !id;
    $("create").hidden = !creating;
    back.hidden = !id && !creating;
    filterButton.hidden = !!id || creating;
    const plus = $("new");
    plus.classList.toggle("on", creating);
    if (creating) plus.setAttribute("aria-current", "page"); else plus.removeAttribute("aria-current");
    document.body.classList.toggle("with-composer", !!id);
    // One footer: under the composer on the page of a session, under Start on the new-session screen, at the bottom of the list.
    if (id) composer.append(foot);
    else if (creating) $("create-bar").append(foot);
    else if (foot.parentElement !== document.body) document.body.insertBefore(foot, toastElement);
    if (creating) void openCreate(false);
    if (id && id !== detail.id) openDetail(id);
    if (!id && detail.id) closeDetail();
    if (!id) renderList();
    padComposer();
  }
  back.addEventListener("click", () => { window.location.hash = "#/"; });
  window.addEventListener("hashchange", route);

  // ---- Live updates ----
  function onSession(update: Live) {
    link.at = new Date();
    if (link.state === "live") showLink();
    const decision = sessions.applyLive(update);
    if (decision === "reload") scheduleListRead(500);
    else if (decision === "listed" && !$("home").hidden) renderList();
    if (detail.id === update.id) { showDetailHead(); drawTimeline(); scheduleDetailRead(400); }
  }
  function readAgain() { void readList(); if (detail.id) void readDetail(); }
  const stream = createLiveStream({
    create: () => new window.EventSource("/api/events"),
    onSession,
    onState: state => { if (signedOut) return; link.state = state; if (state === "live") link.at = new Date(); showLink(); },
    onReconnected: readAgain,
    check: async () => {
      try { await api.sessions(0, 1, []); return "ok"; }
      catch (error) { return error instanceof SignedOutError ? "signed-out" : error instanceof ApiError ? "ok" : "unreachable"; }
    },
    setTimer: (callback, ms) => window.setTimeout(callback, ms),
    clearTimer: timer => window.clearTimeout(timer as number),
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden || signedOut) return;
    if (stream.open) readAgain(); else stream.wake();
  });
  window.addEventListener("online", () => stream.wake());
  window.addEventListener("offline", () => { if (!signedOut) { link.state = "offline"; showLink(); } });
  // "min ago", the hour after which a dot turns gray; the running time of an activity.
  window.setInterval(() => { if (!$("home").hidden) renderList(); }, 60_000);
  window.setInterval(tickLive, 1000);

  $<HTMLButtonElement>("refresh").addEventListener("click", async event => {
    const button = event.currentTarget as HTMLButtonElement;
    button.classList.add("spin");
    if (signedOut) { button.classList.remove("spin"); return; }
    try { await Promise.all([readList(), refreshDetail()]); } finally { button.classList.remove("spin"); }
  });

  // ---- Sign-in: the token of the QR link (#k=) gets the cookie; without it, the cookie or the kept token is used ----
  async function signInFromAddress(): Promise<boolean> {
    const token = takeFragmentToken(window.location.hash, () => window.history.replaceState(null, "", window.location.pathname + window.location.search));
    if (!token) return true;
    try {
      if (!await api.signIn(token)) { showSignedOut(); return false; }
    } catch (error) {
      note.textContent = `CodeAlta is not reachable: ${failureText(error, host)}`;
      return true;
    }
    if (signedOut) {
      signedOut = false;
      link.state = "connecting";
      showLink();
      note.textContent = "";
      route();
      void readList();
      stream.start();
    }
    return true;
  }

  async function start() {
    setUpInstall(window, store);
    sessions.setFilter(keptProjects());
    showFilter();
    if (!await signInFromAddress()) return;
    route();
    await readList();
    if (signedOut) return;
    stream.start();
    readProjects().then(showFilter, () => { /* the filter names the ids until the projects are read */ });
  }
  void start();
}
