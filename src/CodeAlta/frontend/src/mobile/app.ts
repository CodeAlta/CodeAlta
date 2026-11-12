import { hasImages, newSessionReadsImages } from "./attachments";
import { ApiError, browserStore, createApi, failureText, signedOutMessage, SignedOutError, takeAddressToken } from "./api";
import { applyAppearance, fontStepKey, fontSteps, readFontStep, readThemeChoice, steppedFont, themeKey, themeOf, type Theme, type ThemeChoice } from "./appearance";
import { createBarMenu } from "./barMenu";
import { coalesce, composerView } from "./composer";
import { createApprovalPanel } from "./approvalPanel";
import { createWaitingRuns } from "./waitingRuns";
import { createAskPanel } from "./askPanel";
import { createBackgroundJobs } from "./backgroundJobs";
import { isBusy, type Live, type Options, type ProjectList, type PromptMode, type SessionChoices, type Space, type TimelineEvent } from "./contract";
import { activityLine, el, icon, iconSvg, type IconName } from "./dom";
import { createDrafts, newSessionDraft } from "./drafts";
import { installedApp, setUpInstall } from "./install";
import { setUpNotifications } from "./notifications";
import { createLiveStream, type LinkState } from "./liveStream";
import { createMarkdownView, linkedFileName, linksLine, linksMarkdown } from "./markdownView";
import type { ChartData } from "../vegaLite/vegaLite";
import {
  choiceLabel, choiceRequest, entryName, findModel, forgetMode, initialChoice, lastProjectKey, pickEffort, pickModel, preferencesKey,
  providerEntries, providerName, providers, rememberChoice, tierChoice, tierLabel, tierReady, tierText, variantLabels, type Choice, type ProjectPreferences,
} from "./models";
import { createPhoneFiles, newSessionFiles } from "./phoneFiles";
import { sectionModels } from "../modelOrder";
import { promptSuggestionText } from "../promptSuggestion";
import { phoneSuggestion } from "./suggestion";
import {
  appliedSelection, chipText, modeDescription, modeName, pickedChanges, pickSessionModel, providerPermissionMode, queueFirstKey, shownSelection, type Picked,
} from "./selection";
import { safeColor, sessionMeta, showSpaces } from "./sessionMeta";
import { createSessionList } from "./sessionList";
import { topLevelCount } from "./sessionTree";
import { createSheet } from "./sheet";
import { ago, pageSize, readSizes, createSessionStore, type SessionView } from "./sessionStore";
import {
  activityFacts, appendEvents, blockVersion, foldEvents, liveBlock, pendingBlock, planTimeline, prependEvents, stillPending, subjectPath, withDays, withQueue, type DayBlock, type PendingPrompt, type TimelineBlock,
} from "./timeline";
import { createImageViewer } from "./imageViewer";
import { createOutputViewer } from "./outputViewer";
import { createSessionFind } from "./sessionFind";
import { createTimelineView } from "./timelineView";

/** Starts the phone page in a window whose document is `mobile.html`. */
export function startMobile(window: Window & typeof globalThis) {
  const document = window.document;
  const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string | null, text?: string | number | null) => el(document, tag, className, text);
  const store = browserStore(() => window.localStorage);
  const drafts = createDrafts(store);
  const sessions = createSessionStore();
  let signedOut = false;
  const api = createApi({ fetch: (input, init) => window.fetch(input, init), store, onSignedOut: message => showSignedOut(message) });
  const host = window.location.host;

  // ---- The menu of "⋯" in the top bar: the commands that do not fit the bar, and the appearance ----
  const barMenu = createBarMenu({ window, button: $<HTMLButtonElement>("bar-more"), menu: $("bar-menu") });

  // ---- Appearance: the theme (Auto follows the system) and the text size, kept on the phone ----
  const systemLight = window.matchMedia("(prefers-color-scheme: light)");
  let themeChoice: ThemeChoice = readThemeChoice(store);
  let theme: Theme = themeOf(themeChoice, systemLight.matches);
  let fontStep = readFontStep(store);
  const themeChoices = Array.from(document.querySelectorAll<HTMLButtonElement>("#bar-menu [data-theme-choice]"));
  const fontSmaller = $<HTMLButtonElement>("font-smaller"), fontLarger = $<HTMLButtonElement>("font-larger");
  function showAppearance() {
    applyAppearance(document, theme, fontStep);
    for (const item of themeChoices) item.setAttribute("aria-checked", String(item.dataset.themeChoice === themeChoice));
    $("font-step").textContent = `${fontStep} of ${fontSteps}`;
    fontSmaller.disabled = fontStep <= 1;
    fontLarger.disabled = fontStep >= fontSteps;
  }
  function chooseTheme(choice: ThemeChoice) {
    themeChoice = choice;
    if (choice === "auto") store.remove(themeKey); else store.set(themeKey, choice);
    const next = themeOf(choice, systemLight.matches), changed = next !== theme;
    theme = next;
    showAppearance();
    if (changed) redrawDiagrams();
  }
  for (const item of themeChoices) item.addEventListener("click", () => chooseTheme(item.dataset.themeChoice as ThemeChoice));
  systemLight.addEventListener?.("change", () => { if (themeChoice === "auto") chooseTheme("auto"); });
  const stepFont = (by: -1 | 1) => { fontStep = steppedFont(fontStep, by); store.set(fontStepKey, String(fontStep)); showAppearance(); padComposer(); };
  fontSmaller.addEventListener("click", () => stepFont(-1));
  fontLarger.addEventListener("click", () => stepFont(1));
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

  function showSignedOut(message = signedOutMessage) {
    if (signedOut) return;
    signedOut = true;
    stream.stop();
    link.state = "signed-out";
    showLink();
    sessions.setFilter(sessions.filter);
    list.replaceChildren();
    moreButton.hidden = true;
    note.textContent = message;
    if (window.location.hash) window.location.hash = "#/";
  }

  // ---- The list of sessions ----
  const list = $<HTMLUListElement>("sessions"), moreButton = $<HTMLButtonElement>("more");
  // A link to a session is a chip with the state the page knows of it: a session it has not read yet is a plain chip, which opens it all the same.
  const markdown = createMarkdownView({ window, upload: body => api.diagram(body), installed: () => installedApp(window), note: text => noteHere(text),
    describeSession: id => {
      const view = sessions.view(id);
      return { title: view?.title ?? "", scope: view?.project ?? null, state: isBusy(view?.live?.live) ? "running" : "idle", updatedAt: view?.lastActivity ?? null };
    },
    openSession: id => { window.location.hash = `#/session/${encodeURIComponent(id)}`; } });

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

  const sessionList = createSessionList({ window, list, sessions, store, visible: () => !$("home").hidden });
  // The runs that wait for the person, read with the list: of this computer, and of the machines of their servers.
  const waitingRuns = createWaitingRuns({ document, root: $("waiting"), api, host });
  function renderList() {
    if (signedOut) return;
    sessionList.render();
    moreButton.hidden = !sessions.hasMore;
    if (sessions.list().length) note.textContent = "";
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
      if (!await readOn(filter, startedAt, pageSize)) return;
      renderList();
      void waitingRuns.read();
      showScope();
      if (!sessions.list().length) note.textContent = listError
        || (filter.length > 1 ? "No sessions in these projects." : filter.length ? "No sessions in this project." : "No sessions yet.");
    } catch (error) {
      if (error instanceof SignedOutError) return;
      note.textContent = `CodeAlta is not reachable: ${failureText(error, host)}`;
    }
  });
  // Sub-agents folded under their parents take no card of their own: the list reads on, a page at a time and four at most,
  // until it shows `cards` cards. False when the filter changed meanwhile.
  async function readOn(filter: readonly string[], startedAt: number, cards: number): Promise<boolean> {
    for (let read = 0; read < 4 && sessions.hasMore && topLevelCount(sessions.list()) < cards; read++) {
      const page = await api.sessions(sessions.next, pageSize, filter);
      if (filter !== sessions.filter) return false;
      sessions.applyPage(page, startedAt, true);
    }
    return true;
  }
  let reloadTimer = 0;
  function scheduleListRead(ms: number) { window.clearTimeout(reloadTimer); reloadTimer = window.setTimeout(() => void readList(), ms); }

  moreButton.addEventListener("click", async () => {
    moreButton.disabled = true;
    moreButton.textContent = "Loading…";
    const filter = sessions.filter, startedAt = sessions.beginRead();
    try {
      const cards = topLevelCount(sessions.list()) + pageSize;
      const page = await api.sessions(sessions.next, pageSize, filter);
      if (filter === sessions.filter) { sessions.applyPage(page, startedAt, true); if (await readOn(filter, startedAt, cards)) renderList(); }
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
    sessionList.settle();
    showFilter();
    if (window.location.hash === "#/new" && projects) updateNewProjects(newProject.value);
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
        if (project.path) {
          if (small.childNodes.length > 0) small.append(document.createTextNode(" "));
          small.append(document.createTextNode(project.path));
        }
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

  // ---- The page of a session: its timeline, folded as the desktop folds it, drawn block by block (timelineView.ts) ----
  type Drawn = { element: HTMLLIElement; version: string; diagram: boolean };
  const timeline = $<HTMLOListElement>("timeline");
  const detail = {
    id: null as string | null, generation: 0, since: 0, replace: true, events: [] as TimelineEvent[],
    expanded: new Set<string>(), drawn: new Map<string, Drawn>(), loaded: false, blocks: [] as TimelineBlock[],
    // The older events: the first sequence number held, whether events come before it, and whether a read of them runs.
    before: null as number | null, hasOlder: false, readingOlder: false,
  };
  const pendingNotes = new Map<string, string>();
  // The prompts each session accepted that its timeline does not hold yet (a steered one reaches the journal later): shown at once.
  const pendingPrompts = new Map<string, PendingPrompt[]>();
  // The queued prompts taken back from this page, which a read of the queue on its way may still name.
  const removedFromQueue = new Set<string>();
  let pendingSeq = 0;
  let working: HTMLLIElement | null = null;
  const outputViewer = createOutputViewer({ window });

  // A Markdown file, drawn again as more of it is read; a diagram or a formula that comes later draws the text as it is then.
  // Its blocks say which lines they come from, and `after` is called after each drawing.
  function drawnMarkdown() {
    let target: HTMLElement | null = null, shown = "", afterDrawing = () => { /* Nothing drawn yet. */ };
    const fill = () => { if (target) { markdown.fill(target, shown, fill, { sourceLines: true }); afterDrawing(); } };
    return {
      prepare: (element: HTMLElement) => markdown.handle(element, text => showToast(text)),
      draw: (element: HTMLElement, text: string, after: () => void) => { target = element; shown = text; afterDrawing = after; fill(); },
    };
  }

  // A file a message links, a page at a time. Its title is the name the link gives, then the path the server found.
  function openLinkedFile(href: string, seq: number) {
    const id = detail.id;
    if (!id) return;
    const name = document.createElement("strong");
    name.textContent = "File";
    const subject = document.createElement("span");
    subject.className = "tool-subject";
    subject.textContent = linkedFileName(href);
    outputViewer.open([name, subject], async place => {
      const page = await api.linkedFile(id, seq, href, place);
      const path = page.path ? subjectPath(page.path) : null;
      if (path) {
        const folders = document.createElement("span"), file = document.createElement("span");
        folders.className = "tool-path-folders";
        folders.textContent = path.folders;
        file.className = "tool-path-file";
        file.textContent = path.file;
        subject.classList.add("tool-path");
        subject.replaceChildren(folders, file);
      }
      return page;
    }, false, { label: `File ${linkedFileName(href)}`, file: true, drawn: linksMarkdown(href) ? drawnMarkdown() : undefined, asText: linksLine(href) });
  }

  markdown.handle(timeline, text => showToast(text), openLinkedFile);
  // The charts of a message read the data files it names, below the folder of the session: the server finds them in the message.
  const chartData = (id: string, seq: number): ChartData => ({ key: `${id}\n${seq}`, read: path => api.chartData(id, seq, path) });
  const blocksView = createTimelineView({
    document, fill: (element, text, redraw, seq) => markdown.fill(element, text, redraw, seq === undefined || !detail.id ? undefined : { chartData: chartData(detail.id, seq) }), session: () => detail.id,
    sessionTitle: id => sessions.view(id)?.title ?? null,
    openSession: id => { window.location.hash = `#/session/${encodeURIComponent(id)}`; },
    viewer: createImageViewer({ window }),
    outputViewer,
    readTool: (activityId, seq) => {
      if (!detail.id) return Promise.reject(new Error("No session is open."));
      return api.tool(detail.id, activityId, seq === undefined ? undefined : Math.max(0, seq - 1));
    },
    readToolOutput: (activityId, seq, place) => {
      if (!detail.id) return Promise.reject(new Error("No session is open."));
      return api.toolOutput(detail.id, activityId, seq === undefined ? undefined : Math.max(0, seq - 1), place);
    },
    withdraw: (key, then) => void withdrawPrompt(key, then),
  });
  // The search in the timeline (sessionFind.ts), from the magnifier of the head of the session.
  const sessionFind = createSessionFind({
    window, timeline, hasOlder: () => detail.hasOlder, readOlder, firstHeld: () => detail.before,
    redraw: () => drawTimeline(), layout: () => padComposer(),
  });
  $<HTMLButtonElement>("detail-find").addEventListener("click", () => { showWholeTitle(false); sessionFind.bar.open(); });

  const nearBottom = () => window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 40;
  const toBottom = () => window.scrollTo(0, document.documentElement.scrollHeight);
  function noteHere(text: string) { if (detail.id) detailNote.textContent = text; else showToast(text); }

  function drawTimeline() {
    if (!detail.id) return;
    const view = sessions.view(detail.id);
    const busy = isBusy(view?.live?.live);
    const blocks = detail.blocks = foldEvents(detail.events);
    blocksView.know(blocks);
    type Entry = { block: TimelineBlock | DayBlock; live: boolean; open: boolean; key: string; version: string; prompt?: PendingPrompt };
    const entries: Entry[] = blocks.map((block, index) => {
      const live = liveBlock(blocks, index, busy), open = detail.expanded.has(block.key) || sessionFind.opens(block, detail.expanded);
      return { block, live, open, key: block.key, version: blockVersion(block, live, open) };
    });
    const pending = stillPending(pendingPrompts.get(detail.id) ?? [], detail.events, busy, Date.now());
    pendingPrompts.set(detail.id, pending);
    const held: Entry[] = [...entries, ...pending.map(prompt => ({ block: pendingBlock(prompt), live: false, open: false, key: prompt.key,
      version: `${prompt.key}|${prompt.mode}|${prompt.queueId ?? ""}|${prompt.withdrawalId ?? ""}|${prompt.taken ?? false}`, prompt }))];
    const byBlock = new Map(held.map(entry => [entry.block, entry]));
    // A day line says "Today" until midnight: its label is its version, so that it is drawn again after.
    const all: Entry[] = withDays(held.map(entry => entry.block as TimelineBlock), new Date())
      .map(block => block.kind === "day" ? { block, live: false, open: false, key: block.key, version: block.label } : byBlock.get(block)!);
    // While a text is looked for, the place shown stays where it is as the agent writes.
    const follow = nearBottom() && !sessionFind.bar.isOpen();
    const plan = planTimeline(new Map([...detail.drawn].map(([key, drawn]) => [key, drawn.version])), all);
    for (const key of plan.removed) { detail.drawn.get(key)?.element.remove(); detail.drawn.delete(key); }
    let previous: Element | null = null;
    plan.steps.forEach((step, index) => {
      const entry = all[index];
      let drawn = detail.drawn.get(step.key);
      if (step.action === "create" || !drawn) {
        if (entry.block.kind === "message") drawn = { ...blocksView.messageBlock(entry.block, entry.prompt), version: entry.version };
        else if (entry.block.kind === "workflow") drawn = { ...blocksView.workflowBlock(entry.block), version: entry.version };
        else if (entry.block.kind === "day") {
          const item = make("li", "day-mark", entry.block.label);
          item.dataset.key = entry.key;
          drawn = { element: item, version: entry.version, diagram: false };
        }
        else {
          const item = make("li", "activity");
          item.dataset.key = entry.key;
          blocksView.drawActivity(item, entry.block, entry.live, entry.open);
          drawn = { element: item, version: entry.version, diagram: false };
        }
        detail.drawn.set(step.key, drawn);
      } else if (step.action === "update" && entry.block.kind === "message") {
        // A pending prompt learned what takes it back, or that nothing can any more.
        const next = { ...blocksView.messageBlock(entry.block, entry.prompt), version: entry.version };
        drawn.element.replaceWith(next.element);
        detail.drawn.set(step.key, next);
        drawn = next;
      } else if (step.action === "update" && entry.block.kind === "activity") {
        blocksView.drawActivity(drawn.element, entry.block, entry.live, entry.open);
        drawn.version = entry.version;
      } else if (step.action === "update" && entry.block.kind === "workflow") {
        blocksView.drawWorkflow(drawn.element, entry.block);
        drawn.version = entry.version;
      } else if (step.action === "update" && entry.block.kind === "day") {
        drawn.element.textContent = entry.block.label;
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
    if (blocksView.click(event)) return;
    const toggle = (event.target as Element | null)?.closest<HTMLButtonElement>("button.act-toggle");
    const key = toggle?.closest<HTMLElement>("li.activity")?.dataset.key;
    if (!key) return;
    const block = detail.blocks.find(item => item.key === key);
    if (block && sessionFind.toggle(block, detail.expanded)) { drawTimeline(); return; }
    if (detail.expanded.has(key)) detail.expanded.delete(key); else detail.expanded.add(key);
    drawTimeline();
  });

  // Keeps what the phone shows where it is while the timeline changes above it. The browser, which may do the same by
  // itself, is kept out of it: not all do. What was on the screen may be gone (a refresh drops the older pages): then the
  // page goes to the end of the timeline, as when it opens.
  function keepPlace(draw: () => void) {
    const anchor = Array.from(timeline.children).find(item => item.getBoundingClientRect().bottom > 0);
    // At the end of the timeline, the page stays at its end: drawing it sees to that.
    if (!anchor || nearBottom()) { draw(); return; }
    const top = anchor.getBoundingClientRect().top;
    timeline.style.overflowAnchor = "none";
    draw();
    if (anchor.isConnected) window.scrollBy(0, anchor.getBoundingClientRect().top - top); else toBottom();
    window.requestAnimationFrame(() => { timeline.style.overflowAnchor = ""; });
  }

  // A session opens on a page of its latest events, and so does a refresh, which replaces what is held once it comes: the
  // timeline is never drawn from half of them. Any other read gives the events after the last one held. A read of another
  // generation (another session, a refresh since) is dropped.
  const readDetail = coalesce(async () => {
    const id = detail.id;
    if (!id || signedOut) return;
    const generation = detail.generation, since = detail.since;
    try {
      if (detail.replace) {
        const page = await api.latestEvents(id);
        if (detail.id !== id || detail.generation !== generation) return;
        keepPlace(() => {
          detail.events = appendEvents([], page.events ?? []);
          detail.since = Math.max(page.since ?? 0, detail.events.at(-1)?.seq ?? 0);
          detail.before = page.before ?? null;
          detail.hasOlder = !!page.hasMore && detail.before !== null;
          // A read that failed is made again from the end: what came before it would all be read from the start.
          detail.replace = !!page.error && !page.events?.length;
          detail.loaded = true;
          if (page.error) detailNote.textContent = page.error;
          drawTimeline();
          showOlder();
        });
        watchOlder();
        void readQueue();
        return;
      }
      const page = await api.events(id, since);
      if (detail.id !== id || detail.generation !== generation) return;
      detail.events = appendEvents(detail.events, page.events ?? []);
      detail.since = Math.max(detail.since, page.since ?? since, detail.events.at(-1)?.seq ?? 0);
      if (page.error) detailNote.textContent = page.error;
      drawTimeline();
      if (queueToRead(id)) void readQueue();
    } catch (error) {
      if (detail.id !== id || error instanceof SignedOutError) return;
      detailNote.textContent = `Could not read the session: ${failureText(error, host)}`;
    }
  });
  // The queue of the session says which queued prompts wait, also after a reload or when they come from the computer. It is
  // read while the session works or holds some, and when a session opens: nothing else adds to it.
  const readQueue = coalesce(async () => {
    const id = detail.id;
    if (!id || signedOut) return;
    try {
      const page = await api.queue(id);
      if (detail.id !== id || page.error) return;
      const held = pendingPrompts.get(id) ?? [];
      const next = withQueue(held, page.items ?? [], removedFromQueue, detail.events.at(-1)?.seq ?? 0);
      const same = next.length === held.length && next.every((prompt, index) => prompt === held[index]);
      if (same) return;
      pendingPrompts.set(id, next);
      drawTimeline();
    } catch { /* The next read says it: the prompts the page holds stay as they are. */ }
  });
  function queueToRead(id: string) { return isBusy(sessions.view(id)?.live?.live) || (pendingPrompts.get(id) ?? []).some(prompt => !!prompt.queueId); }

  let detailTimer = 0;
  function scheduleDetailRead(ms: number) { window.clearTimeout(detailTimer); detailTimer = window.setTimeout(() => void readDetail(), ms); }
  function refreshDetail() { if (!detail.id) return Promise.resolve(); detail.generation++; detail.replace = true; detail.readingOlder = false; return readDetail(); }

  // ---- The older events: a page more as the top of the timeline comes near, or on a tap ----
  const older = $<HTMLButtonElement>("older");
  function showOlder() {
    older.hidden = !detail.id || !detail.hasOlder;
    older.disabled = detail.readingOlder;
    older.textContent = detail.readingOlder ? "Loading older messages…" : "Load older messages";
    sessionFind.bar.changed();
  }
  const olderWatch = new window.IntersectionObserver(items => {
    if (!items.some(item => item.isIntersecting)) return;
    olderWatch.unobserve(older);
    void readOlder();
  }, { rootMargin: "800px 0px 0px 0px" });
  // Watching it again says at once whether it is near: a short page reads the next one by itself. A read that failed does
  // not come here, so that it is not made again and again: a tap makes it again.
  function watchOlder() { olderWatch.unobserve(older); if (detail.id && detail.hasOlder) olderWatch.observe(older); }
  // A read of older messages asked for while one runs for the same session waits for that one; a read of the session shown
  // before (another one, or the same one read again) does not hold it back.
  let olderRead: { id: string | null; generation: number; read: Promise<void> } | null = null;
  function readOlder(): Promise<void> {
    if (olderRead && olderRead.id === detail.id && olderRead.generation === detail.generation) return olderRead.read;
    const entry: { id: string | null; generation: number; read: Promise<void> } = { id: detail.id, generation: detail.generation, read: Promise.resolve() };
    olderRead = entry;
    entry.read = readOlderPage().finally(() => { if (olderRead === entry) olderRead = null; });
    return entry.read;
  }
  async function readOlderPage() {
    const id = detail.id, generation = detail.generation, before = detail.before;
    if (!id || signedOut || detail.readingOlder || !detail.hasOlder || before === null) return;
    const current = () => detail.id === id && detail.generation === generation;
    detail.readingOlder = true;
    showOlder();
    try {
      const page = await api.latestEvents(id, before);
      if (!current()) return;
      detail.readingOlder = false;
      keepPlace(() => {
        detail.events = prependEvents(detail.events, page.events ?? []);
        detail.before = page.before ?? before;
        detail.hasOlder = !!page.hasMore;
        drawTimeline();
        showOlder();
      });
      if (page.error) detailNote.textContent = page.error; else watchOlder();
    } catch (error) {
      if (!current() || error instanceof SignedOutError) return;
      detail.readingOlder = false;
      showOlder();
      detailNote.textContent = `Could not read the older messages: ${failureText(error, host)}`;
    }
  }
  older.addEventListener("click", () => void readOlder());

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
    sessionFind.bar.dismiss();
    showWholeTitle(false);
    detail.id = id;
    detail.generation++;
    detail.since = 0;
    detail.replace = true;
    detail.events = [];
    detail.blocks = [];
    detail.expanded = new Set();
    detail.drawn = new Map();
    detail.loaded = false;
    detail.before = null;
    detail.hasOlder = false;
    detail.readingOlder = false;
    showOlder();
    watchOlder();
    blocksView.reset();
    timeline.replaceChildren();
    working = null;
    detailNote.textContent = pendingNotes.get(id) ?? "";
    pendingNotes.delete(id);
    bindComposer(id);
    showDetailHead();
    void readChoices(id);
    void askPanel.read(id);
    void approvalPanel.read(id);
    if (!sessions.listed(id) || !sessions.view(id)?.title) void readSessionAlone(id);
    void readDetail().then(() => { if (detail.id === id) toBottom(); });
  }
  function closeDetail() {
    sessionFind.bar.dismiss();
    bindComposer(null);
    detail.id = null;
    detail.generation++;
    detail.drawn = new Map();
    detail.blocks = [];
    detail.hasOlder = false;
    showOlder();
    watchOlder();
    timeline.replaceChildren();
    working = null;
  }

  function redrawDiagrams() {
    for (const drawn of detail.drawn.values()) {
      if (!drawn.diagram) continue;
      const content = drawn.element.querySelector<HTMLElement>(":scope > .markdown-content");
      const key = drawn.element.dataset.key!;
      const block = detail.blocks.find(b => b.key === key);
      if (content && block?.kind === "message") blocksView.fillMessage(content, block, block.text);
    }
  }

  // ---- The composer: its text is the draft of the session it was typed for ----
  const composer = $<HTMLFormElement>("composer"), promptBox = $<HTMLTextAreaElement>("prompt");
  const sendButton = $<HTMLButtonElement>("send"), queueButton = $<HTMLButtonElement>("queue"), abortButton = $<HTMLButtonElement>("abort");
  const hint = $("composer-hint");
  // What the model suggests as the next prompt, as a chip over the box; for each session, the suggestion a prompt was sent after.
  const suggestionChip = $<HTMLButtonElement>("suggestion"), suggestionText = $("suggestion-text");
  const answeredSuggestions = new Map<string, string>();
  let composerFor: string | null = null;
  // Files and photos held with a prompt, for each session and the new one (phoneFiles.ts).
  const files = createPhoneFiles({
    document, input: $<HTMLInputElement>("attach-input"),
    session: { strip: $("attach-strip"), note: $("attach-note") }, newSession: { strip: $("new-attach-strip"), note: $("new-attach-note") },
    current: () => composerFor,
    readsImages: key => key === newSessionFiles ? newSessionReadsImages(options, choice) : sessionReadsImages(key),
    changed: key => { if (key === composerFor && detail.id === key) showComposer(); },
  });
  $<HTMLButtonElement>("attach").addEventListener("click", () => { if (composerFor) files.choose(composerFor); });
  $<HTMLButtonElement>("new-attach").addEventListener("click", () => files.choose(newSessionFiles));
  const stopping = new Map<string, number>(), stoppedAt = new Map<string, number>();

  // ---- The ask a session waits on: in place of the composer once the run that asked ended, as on the desktop ----
  const askBar = $("ask");
  const askPanel = createAskPanel({
    document, window, root: askBar, cancelSheet: $("ask-cancel-sheet"), api, host,
    current: () => detail.id,
    live: id => sessions.view(id)?.live,
    markdown: (element, text) => { const fill = () => markdown.fill(element, text, fill); fill(); },
    onChange: () => showComposer(),
    onSent: () => scheduleDetailRead(0),
    onResize: () => padComposer(),
  });
  // ---- What the session asks permission for: above the prompt, whose Stop stays, as on the desktop ----
  const approvalRoot = $("approval");
  const approvalPanel = createApprovalPanel({
    document, window, root: approvalRoot, api, host,
    current: () => detail.id,
    onChange: () => showComposer(),
    onSent: () => scheduleDetailRead(0),
    onResize: () => padComposer(),
  });
  const bottomBar = () => askBar.hidden ? composer : askBar;
  const backgroundRoot = $("background-status");
  const background = createBackgroundJobs({ window, root: backgroundRoot, api, changed: padComposer });

  function bindComposer(id: string | null) {
    background.setSession(id);
    if (composerFor) drafts.set(composerFor, promptBox.value);
    composerFor = id;
    promptBox.value = id ? drafts.get(id) : "";
    grow();
    files.show(id);
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
    const state = composerView(busy, typed, !!view?.live?.canSteer, queueFirst, !!id && files.get(id).length > 0);
    sendButton.hidden = state.primary === null;
    if (state.primary) face(sendButton, state.primary);
    queueButton.hidden = state.secondary === null;
    if (state.secondary) { face(queueButton, state.secondary); queueButton.dataset.mode = state.secondary; }
    abortButton.hidden = !state.stop;
    const halting = !!id && stopping.has(id);
    abortButton.disabled = halting;
    abortButton.querySelector("span")!.textContent = halting ? "Stopping…" : "Stop";
    hint.textContent = state.hint;
    // The empty box says what the composer does now.
    promptBox.placeholder = busy && !typed ? state.hint : "Ask for more…";
    composer.dataset.submit = state.submit;
    showSelection();
    showAsk();
    showSuggestion(id, view?.live);
  }

  // The chip shows only over an empty box, while nothing runs or waits for the person (showAsk tells what waits).
  function showSuggestion(id: string | null, live: Live | null | undefined) {
    const waiting = !approvalRoot.hidden || !askBar.hidden || composer.classList.contains("sending");
    const suggestion = id ? phoneSuggestion(live, { text: promptBox.value, files: files.get(id).length, waiting, answered: answeredSuggestions.get(id) ?? null }) : null;
    const was = !suggestionChip.hidden;
    suggestionChip.hidden = !suggestion;
    if (suggestion) {
      suggestionText.textContent = suggestion;
      suggestionChip.title = `Use suggestion: ${suggestion}`;
      suggestionChip.setAttribute("aria-label", suggestionChip.title);
    }
    if (was !== !!suggestion) padComposer();
  }
  // A tap writes the suggestion in the box, to change or send: it never sends.
  suggestionChip.addEventListener("click", () => {
    const id = composerFor, suggestion = suggestionText.textContent ?? "";
    if (!id || suggestionChip.hidden || !suggestion) return;
    promptBox.value = suggestion;
    drafts.set(id, suggestion);
    grow();
    showComposer();
    promptBox.focus();
    promptBox.setSelectionRange(suggestion.length, suggestion.length);
  });

  function showAsk() {
    const was = !askBar.hidden;
    const shown = askPanel.show();
    composer.hidden = shown;
    bottomBar().prepend(backgroundRoot);
    // A request can wait while a question of a script shows: it goes with the bar that shows.
    const approvalWas = !approvalRoot.hidden;
    const approvalShown = approvalPanel.show();
    bottomBar().prepend(approvalRoot);
    if (detail.id && foot.parentElement !== bottomBar()) bottomBar().append(foot);
    if (shown !== was || approvalShown !== approvalWas) padComposer();
  }

  // ---- The selection of the composer: the chip (model and effort) and "⋯" (agent prompt, permissions, queue first) ----
  // What the session runs on comes from its choices (read when it opens, after each send), else from the list. What is picked on the
  // phone goes with the next send or queue, and then is the session's; a steer joins the running turn and leaves it for the next one.
  const choicesOf = new Map<string, SessionChoices>(), picks = new Map<string, Picked>();
  let queueFirst = store.get(queueFirstKey) === "1";
  const pickModelButton = $<HTMLButtonElement>("pick-model"), pickMoreButton = $<HTMLButtonElement>("pick-more");
  const sessionMoreSheet = createSheet(document, $("session-more-sheet"));
  const sessionAgent = $<HTMLSelectElement>("session-agent"), sessionMode = $<HTMLSelectElement>("session-mode"), queueFirstBox = $<HTMLInputElement>("session-queue-first");
  const sessionProvider = $<HTMLSelectElement>("session-provider");
  // A move of a session to another provider under way, or why the last one was refused.
  const providerSwitch = new Map<string, Readonly<{ busy: boolean; error: string | null }>>();
  const readChoices = async (id: string) => {
    const before = JSON.stringify(choicesOf.get(id) ?? null);
    try {
      const read = await api.choices(id);
      choicesOf.set(id, { current: read.current, models: read.models ?? [], modelsOrder: read.modelsOrder ?? null, prompts: read.prompts ?? [], permissionModes: read.permissionModes ?? [],
        providers: read.providers ?? [], error: read.error ?? null });
      files.reconcile(id);
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
    const { frontier, other } = sectionModels(models.filter(m => !query || `${m.name} ${m.id}`.toLowerCase().includes(query)), choices?.modelsOrder);
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

  // "⋯" of a session: its agent prompt, its provider (as the desktop, it moves an idle session to another provider of its space at
  // once), its permissions (when its provider has modes), and whether a prompt typed while it works is queued.
  function renderSessionMore() {
    const id = detail.id;
    if (!id) return;
    const choices = choicesOf.get(id), shown = sessionShown(id);
    const prompts = choices?.prompts.length ? choices.prompts : shown.agent ? [{ id: shown.agent, name: shown.agentName ?? shown.agent }] : [];
    sessionAgent.replaceChildren(...prompts.map(p => option(p.id, p.name, p.id === shown.agent)));
    sessionAgent.disabled = !choices?.prompts.length;
    // A provider the session can no longer use (removed, turned off, out of its space) is named, and any other can be chosen.
    const current = choices?.current.provider ?? null, offered = choices && !choices.error ? choices.providers ?? [] : [];
    const providerList = current && !offered.some(p => p.id === current) ? [{ id: current, name: `${shown.providerName ?? current}${offered.length ? " · Unavailable" : ""}` }, ...offered] : offered;
    const switching = providerSwitch.get(id), busy = isBusy(sessions.view(id)?.live?.live);
    $("session-provider-field").hidden = providerList.length < 2;
    sessionProvider.replaceChildren(...providerList.map(p => option(p.id, p.name, p.id === current)));
    sessionProvider.disabled = busy || !!switching?.busy;
    $("session-provider-text").textContent = switching?.busy ? "Switching provider…" : switching?.error
      ?? (busy ? "The provider can be changed once the session is idle." : "History is kept: the next prompt runs on the new provider.");
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
  sessionProvider.addEventListener("change", async () => {
    const id = detail.id, target = sessionProvider.value;
    if (!id || !target || target === choicesOf.get(id)?.current.provider || providerSwitch.get(id)?.busy) return;
    providerSwitch.set(id, { busy: true, error: null });
    renderSessionMore();
    try {
      await api.selectProvider(id, target);
      providerSwitch.delete(id);
      // The model, the effort and the mode picked were of the provider it left: the new one gives its own. The agent prompt stays.
      const agent = picks.get(id)?.agent;
      pick(id, agent ? { agent } : {});
    } catch (error) {
      if (error instanceof SignedOutError) return;
      providerSwitch.set(id, { busy: false, error: failureText(error, host) });
    }
    await readChoices(id);
    if (detail.id === id && sessionMoreSheet.shown) renderSessionMore();
  });
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
    // Files go with a Send or a Queue; a steer sends the text into the turn, and the files stay for the next one.
    const held = id && mode !== "steer" ? files.get(id) : [];
    if (!id || (!text && !held.length)) return;
    if (hasImages(held) && sessionReadsImages(id) === false) { files.note(id, "This model does not read images."); return; }
    const sent = held.length ? files.take(id) : held;
    const offered = promptSuggestionText(sessions.view(id)?.live?.suggestion);
    composer.classList.add("sending");
    // The prompt leaves the input (and its draft) as it is sent: the request can take a while to return while the
    // turn already runs. If it fails, the text goes back to the session it was written for.
    promptBox.value = "";
    drafts.clear(id);
    grow();
    showComposer();
    const sentAt = Date.now();
    // The prompt shows in the timeline at once, as the user's message: it goes when the journal holds it, or when the send fails.
    const prompt: PendingPrompt = { key: `p${++pendingSeq}`, text, mode, sentAt, after: detail.id === id ? detail.events.at(-1)?.seq ?? 0 : 0,
      ...(sent.length ? { attachments: sent.map(({ title, mediaType }) => ({ title, mediaType })) } : {}) };
    const forget = () => { forgetPrompt(id, prompt.key); };
    pendingPrompts.set(id, [...pendingPrompts.get(id) ?? [], prompt]);
    if (detail.id === id) { drawTimeline(); toBottom(); }
    // What was picked goes with a send or a queue; a steer leaves it for the next one.
    const picked = picks.get(id) ?? {}, selection = mode === "steer" ? {} : pickedChanges(choicesOf.get(id), picked);
    try {
      const answer = await api.prompt(id, text, mode, selection, sent);
      // The prompt answers the suggestion: it is not offered again, before the session says that its next turn started.
      if (offered) answeredSuggestions.set(id, offered);
      // What takes the prompt back, once it waits; a send to a session that works was queued.
      if (answer.queueItemId || answer.withdrawalId) {
        changePrompt(id, prompt.key, {
          ...(answer.queueItemId ? { queueId: answer.queueItemId, mode: "queue" as const } : {}),
          ...(answer.withdrawalId ? { withdrawalId: answer.withdrawalId } : {}),
        });
      }
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
      files.giveBack(id, sent);
      if (error instanceof SignedOutError) return;
      if (detail.id === id) detailNote.textContent = `Not sent: ${failureText(error, host)}`;
      else showToast(`Not sent to “${sessions.view(id)?.title || "the session"}”: ${failureText(error, host)} The text is kept as its draft.`, undefined, 8000);
    } finally {
      composer.classList.remove("sending");
      showComposer();
    }
  }
  function forgetPrompt(id: string, key: string) {
    pendingPrompts.set(id, (pendingPrompts.get(id) ?? []).filter(prompt => prompt.key !== key));
    if (detail.id === id) drawTimeline();
  }
  function changePrompt(id: string, key: string, change: Partial<PendingPrompt>) {
    pendingPrompts.set(id, (pendingPrompts.get(id) ?? []).map(prompt => prompt.key === key ? { ...prompt, ...change } : prompt));
    if (detail.id === id) drawTimeline();
  }

  // Takes a prompt back from the queue or from the run it was steered into. When it is too late, the prompt stays shown, as
  // what the agent has; its text goes back to the composer only once it is known that the agent will never have it.
  async function withdrawPrompt(key: string, then: "delete" | "edit") {
    const id = detail.id, prompt = id ? pendingPrompts.get(id)?.find(item => item.key === key) : null;
    if (!id || !prompt) return;
    let gone: boolean;
    let note = "";
    try {
      if (prompt.queueId) {
        const { status } = await api.unqueue(id, prompt.queueId);
        gone = status !== "sending";
        if (gone) removedFromQueue.add(prompt.queueId);
        else note = "Too late: the session is taking this prompt.";
      } else if (prompt.withdrawalId) {
        ({ withdrawn: gone } = await api.unsteer(id, prompt.withdrawalId));
        if (!gone) note = "Too late: the agent already has this prompt.";
      } else return;
    } catch (error) {
      if (error instanceof SignedOutError) return;
      if (detail.id === id) detailNote.textContent = `Not removed: ${failureText(error, host)}`;
      return;
    }
    if (!gone) {
      changePrompt(id, key, { taken: true });
      if (detail.id === id) detailNote.textContent = note;
      return;
    }
    forgetPrompt(id, key);
    if (detail.id === id) detailNote.textContent = "";
    if (then !== "edit" || composerFor !== id) return;
    const typed = promptBox.value.trim();
    promptBox.value = typed ? `${prompt.text}\n\n${typed}` : prompt.text;
    drafts.set(id, promptBox.value);
    grow();
    showComposer();
    promptBox.focus();
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
    const bar = creating ? $("create-bar") : bottomBar();
    const viewport = window.visualViewport;
    const covered = viewport ? Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop) : 0;
    bar.style.bottom = `${covered}px`;
    // The ask scrolls in the bar, which leaves the top of the screen to the timeline.
    if (bar === askBar) askBar.style.maxHeight = `${Math.round((viewport?.height ?? window.innerHeight) * 0.72)}px`;
    page.style.paddingBottom = `${bar.offsetHeight + covered + (creating ? 0 : 16)}px`;
  }
  window.visualViewport?.addEventListener("resize", padComposer);
  window.visualViewport?.addEventListener("scroll", padComposer);

  // The title of a session in the top bar: one line, or on a tap the whole of it.
  const heading = $<HTMLButtonElement>("detail-heading");
  function showWholeTitle(whole: boolean) {
    heading.setAttribute("aria-expanded", String(whole));
    heading.title = whole ? "Show the title on one line" : "Show the whole title";
  }
  heading.addEventListener("click", () => showWholeTitle(heading.getAttribute("aria-expanded") !== "true"));

  // The commands of a session that live in the menu of "⋯". What changes the page waits for the menu to close.
  $("menu-archive").addEventListener("click", () => barMenu.close(() => {
    const id = detail.id;
    if (!id) return;
    archive(id);
  }));
  $("menu-new").addEventListener("click", () => barMenu.close(startNew));
  $("menu-refresh").addEventListener("click", () => barMenu.close(() => void refreshAll()));
  // Notifications and Install act at once, in the tap: the browser asks for its permission or its prompt only there.
  for (const id of ["notifications", "install"]) $(id).addEventListener("click", () => barMenu.close());

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
  const newMoreSheet = createSheet(document, $("new-more-sheet")), promptSheet = createSheet(document, $("prompt-sheet"));
  let options: Options | null = null;
  let choice: Choice = { tier: null, provider: "", model: "", effort: "" };
  // Images go only to a model that says it reads them. A model that does not say is treated as one that does not.
  // Null until the choices are known: a photo picked then is kept and checked when they arrive.
  function sessionReadsImages(id: string): boolean | null {
    const choices = choicesOf.get(id);
    if (!choices || choices.error || choices.models.length === 0) return null;
    return choices.models.find(model => model.id === sessionShown(id).model)?.imageInput === true;
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
  // The first choice of the projects: a chat, in no project. No project id has a control character.
  const chatChoice = "\u0000chat";
  const isChat = () => newProject.value === chatChoice;
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

  // The agent prompt has its chip beside the label of the project; the chips of the bar: the tier or the model with its effort,
  // and "⋯" with a branch for a worktree and a shield for a permission mode.
  function renderChoice() {
    if (!options) return;
    const o = options;
    const label = [choiceLabel(o, choice), choice.effort].filter(Boolean).join(" · ");
    $("new-model-name").textContent = label;
    $("new-model").setAttribute("aria-label", `Model: ${label}`);
    const agent = o.prompts.find(p => p.id === newAgent.value)?.name ?? newAgent.value;
    $("new-prompt-name").textContent = agent;
    $("new-prompt").setAttribute("aria-label", `Agent prompt: ${agent}`);
    const modes = offeredModes();
    // A chat has no folder to make a worktree of.
    const worktree = newWorktree.checked && !isChat();
    tagsOf($("new-more-text"), [...(worktree ? [["branch", null] as const] : []), ...(newPermission ? [["shield", null] as const] : [])]);
    $("new-more").setAttribute("aria-label", ["More options", worktree && "new worktree",
      newPermission && `permissions ${modeName(newPermission, modes)}`].filter(Boolean).join(", "));
    if (modelSheet.shown && modelSheetFor === null) renderNewModels();
    if (newMoreSheet.shown) renderNewMore();
    if (promptSheet.shown) renderPrompts();
  }

  // The prompt sheet of the new session: each agent prompt with its description, as the agent chip of the desktop lists them.
  function renderPrompts() {
    if (!options) return;
    $("prompt-list").replaceChildren(...options.prompts.map(prompt => {
      const on = prompt.id === newAgent.value;
      const button = make("button", on ? "on" : null);
      button.type = "button";
      button.setAttribute("aria-pressed", String(on));
      button.append(make("span", null, prompt.name), ...(prompt.description ? [make("small", null, prompt.description)] : []));
      button.addEventListener("click", () => {
        if (!on) { newAgent.value = prompt.id; newAgent.dispatchEvent(new Event("change")); }
        promptSheet.close();
      });
      const item = make("li");
      item.append(button);
      return item;
    }));
  }
  $("new-prompt").addEventListener("click", () => {
    if (!options) return;
    renderPrompts();
    promptSheet.open($("prompt-list").querySelector<HTMLElement>("button.on"));
  });
  $("prompt-done").addEventListener("click", () => promptSheet.close());

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
    const { groups, frontier, other } = providerEntries(o, choice.provider, query), byEntry = new Map(groups.map(g => [g.entry.id, g]));
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
    newWorktree.closest("label")!.hidden = isChat();
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

  // The projects of the form are "None (Chat)", first, then those the list keeps to, all of them without a filter. The one
  // chosen is `preferred` (the project of the viewed session, or the one of the form), else the last one, while the list
  // keeps to them; else the first one the filter keeps, or without a filter the default project.
  function updateNewProjects(preferred: string | null) {
    if (!projects) return;
    const filter = sessions.filter;
    const eligible = projects.projects.filter(p => !filter.length || filter.includes(p.id));
    const available = (id: string | null) => id && (id === chatChoice || eligible.some(p => p.id === id)) ? id : null;
    const selected = available(preferred) ?? available(store.get(lastProjectKey))
      ?? (filter.length ? eligible[0]?.id : available(projects.default) ?? eligible[0]?.id);
    newProject.replaceChildren(option(chatChoice, "None (Chat)", selected === chatChoice), ...eligible.map(p => option(p.id, p.name, p.id === selected)));
    initForm();
  }

  async function openCreate(activeProject: string | null = null) {
    newNote.textContent = options ? "" : "Loading…";
    try {
      const [, read] = await Promise.all([readProjects(), options ? Promise.resolve(options) : api.options()]);
      options = {
        models: read.models ?? [], prompts: read.prompts ?? [], tiers: read.tiers ?? [], defaultTier: read.defaultTier ?? null, defaults: read.defaults ?? null,
        permissionModes: read.permissionModes ?? {}, modelsOrders: read.modelsOrders ?? {}, error: read.error ?? null,
      };
      newAgent.replaceChildren(...options.prompts.map(p => option(p.id, p.name, p.id === options!.defaults?.prompt)));
      if (!newText.value) newText.value = drafts.get(newSessionDraft);
      updateNewProjects(activeProject);
      files.reconcile(newSessionFiles);
      padComposer();
    } catch (error) {
      if (!(error instanceof SignedOutError)) newNote.textContent = `Could not read the choices: ${failureText(error, host)}`;
    }
  }
  newProject.addEventListener("change", () => { store.set(lastProjectKey, newProject.value); initForm(); });
  newText.addEventListener("input", () => drafts.set(newSessionDraft, newText.value));
  $("new-form").addEventListener("submit", async event => {
    event.preventDefault();
    const text = newText.value.trim();
    const held = files.get(newSessionFiles);
    if ((!text && !held.length) || !newProject.value) return;
    if (hasImages(held) && newSessionReadsImages(options, choice) === false) { files.note(newSessionFiles, "This model does not read images."); return; }
    const start = $<HTMLButtonElement>("new-start");
    start.disabled = true;
    newNote.textContent = "Starting…";
    try {
      const created = await api.create({
        ...(isChat() ? { chat: true } : { project: newProject.value, worktree: newWorktree.checked }), text, prompt: newAgent.value || undefined, ...choiceRequest(choice),
        ...(newPermission ? { permissionMode: newPermission } : {}),
        ...(held.length ? { attachments: held } : {}),
      });
      store.set(lastProjectKey, newProject.value);
      newText.value = "";
      files.take(newSessionFiles);
      drafts.clear(newSessionDraft);
      newNote.textContent = "";
      // A prompt that did not go is shown on the page of its session, back in its composer.
      if (created.sendError) {
        drafts.set(created.id, text);
        files.giveBack(created.id, held);
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
  function startNew() {
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
    void openCreate();
  }
  $("new").addEventListener("click", startNew);

  // ---- Swipe a card for its action, Gmail-style: the drag is the action ----
  // Right: Archive, on green. Past 40% of the width a release archives and the card leaves, or comes back while the phone
  // asks about the session's child sessions; short of it the card springs back. A drag that starts vertical is the page's
  // scroll (touch-action: pan-y). A toast offers Restore for 5 s.
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
      void archive(done.row.dataset.id!);
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
  let archiveQuestion: string | null = null;
  const archiveSheet = createSheet(document, $("archive-sheet"), () => { archiveQuestion = null; });
  let checkingArchive = false;
  $("archive-cancel").addEventListener("click", () => { archiveQuestion = null; archiveSheet.close(); });
  for (const [button, withChildren] of [["archive-no", false], ["archive-yes", true]] as const) {
    $(button).addEventListener("click", () => {
      const id = archiveQuestion;
      archiveQuestion = null;
      archiveSheet.close();
      if (id) doArchive(id, withChildren);
    });
  }
  // A swiped card left its row: it comes back when the session is not archived at once.
  function springBack(id: string) { const item = rowOf(id); if (item) place(item, 0, "spring"); }
  async function archive(id: string) {
    if (checkingArchive || archiveSheet.shown) { springBack(id); return; }
    checkingArchive = true;
    try {
      const result = await api.archiveChildren(id);
      if (result.error || typeof result.hasChildren !== "boolean") throw new Error(result.error || "The child sessions could not be checked.");
      if (result.hasChildren) { springBack(id); archiveQuestion = id; archiveSheet.open($("archive-yes")); }
      else doArchive(id, false);
    } catch (error) {
      springBack(id);
      if (!(error instanceof SignedOutError)) showToast(`Not archived: ${failureText(error, host)}`);
    } finally { checkingArchive = false; }
  }
  function doArchive(id: string, withChildren: boolean) {
    if (detail.id === id) window.location.hash = "#/";
    const title = sessions.view(id)?.title || "Session";
    sessions.hide(id);
    const item = rowOf(id);
    if (item) window.setTimeout(() => { if (sessions.hidden(id) && item.isConnected) collapse(item); }, 180); else renderList();
    const archiving = api.archive(id, withChildren).then(result => { sessions.settled(id); scheduleListRead(0); return result; });
    archiving.catch(error => {
      if (toast?.id === id) hideToast();
      bringBack(id);
      scheduleListRead(0);
      if (!(error instanceof SignedOutError)) showToast(`Not archived: ${failureText(error, host)}`);
    });
    const restore = async () => {
      if (toast?.id !== id) return;
      hideToast();
      try {
        const result = await archiving;
        if (withChildren && !Array.isArray(result.changed)) throw new Error("The archived sessions could not be confirmed; check them before restoring.");
        for (const changed of result.changed ?? [id]) await api.restore(changed);
      }
      catch (error) { scheduleListRead(0); if (!(error instanceof SignedOutError)) showToast(`Not restored: ${failureText(error, host)}`); return; }
      // A read of the list started before the restore would take the session out again: the row comes back with a read
      // made after it.
      await readList();
      bringBack(id);
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
    const returning = !id && !creating && $("home").hidden;
    // What shows changed under the menu: it closes, and the bar takes the commands of this page.
    barMenu.dismiss();
    showBar(id ? "session" : creating ? "new" : "home");
    $("home").hidden = !!id || creating;
    $("detail").hidden = !id;
    $("create").hidden = !creating;
    back.hidden = !id && !creating;
    const plus = $("new");
    plus.classList.toggle("on", creating);
    if (creating) plus.setAttribute("aria-current", "page"); else plus.removeAttribute("aria-current");
    document.body.classList.toggle("with-composer", !!id);
    // One footer: under the composer on the page of a session, under Start on the new-session screen, at the bottom of the list.
    if (id) bottomBar().append(foot);
    else if (creating) $("create-bar").append(foot);
    else if (foot.parentElement !== document.body) document.body.insertBefore(foot, toastElement);
    // The viewed session gives its project to the form, or the chats when it is one.
    if (creating) { const viewed = detail.id ? sessions.view(detail.id) : undefined; void openCreate(viewed ? viewed.projectId ?? chatChoice : null); }
    if (id && id !== detail.id) openDetail(id);
    if (!id && detail.id) closeDetail();
    // Back on the list, the cards are in their latest order at once.
    if (returning) sessionList.settle();
    if (!id) renderList();
    padComposer();
  }
  back.addEventListener("click", () => { window.location.hash = "#/"; });
  // The top bar of each page: on the list, the logo and its commands; on a session, Back, the state of the link, its title,
  // Find and "⋯", whose menu holds the other commands.
  function showBar(page: "home" | "session" | "new") {
    const session = page === "session";
    $("bar").dataset.page = page;
    heading.hidden = !session;
    $("detail-find").hidden = !session;
    filterButton.hidden = plus.hidden = $("refresh").hidden = session;
    for (const item of Array.from(document.querySelectorAll<HTMLElement>("#bar-menu [data-in-session]"))) item.hidden = !session;
    if (!session) showWholeTitle(false);
  }
  const plus = $("new");
  window.addEventListener("hashchange", route);

  // ---- Live updates ----
  function onSession(update: Live) {
    link.at = new Date();
    if (link.state === "live") showLink();
    // A session that suggests nothing any more starts again: the same words, suggested after another turn, are offered.
    if (!promptSuggestionText(update.suggestion)) answeredSuggestions.delete(update.id);
    const decision = sessions.applyLive(update);
    if (decision === "reload") scheduleListRead(500);
    else if (decision === "listed" && !$("home").hidden) renderList();
    askPanel.onLive(update);
    approvalPanel.onLive(update);
    if (detail.id === update.id) { showDetailHead(); drawTimeline(); scheduleDetailRead(400); }
  }
  function readAgain() { void readList(); if (detail.id) { void readDetail(); void askPanel.read(detail.id); void approvalPanel.read(detail.id); void background.refresh(); } }
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
    if (!$("home").hidden) sessionList.settle();
    if (stream.open) readAgain(); else stream.wake();
  });
  window.addEventListener("online", () => stream.wake());
  window.addEventListener("offline", () => { if (!signedOut) { link.state = "offline"; showLink(); } });
  // "min ago", the hour after which a dot turns gray; the running time of an activity.
  window.setInterval(() => { if (!$("home").hidden) renderList(); }, 60_000);
  window.setInterval(tickLive, 1000);

  // Reads everything again. The button turns while it does: Refresh in the bar, or "⋯" on a session, whose menu holds it.
  async function refreshAll() {
    const turning = [$("refresh"), ...(detail.id ? [$("bar-more")] : [])];
    for (const button of turning) button.classList.add("spin");
    try {
      if (signedOut) return;
      if (!$("home").hidden) sessionList.settle();
      await Promise.all([readList(), refreshDetail(), detail.id ? askPanel.read(detail.id) : null, detail.id ? approvalPanel.read(detail.id) : null, background.refresh()]);
    } finally { for (const button of turning) button.classList.remove("spin"); }
  }
  $<HTMLButtonElement>("refresh").addEventListener("click", () => void refreshAll());

  // ---- Sign-in: the token of the QR link (#k=, or ?k=) gets the cookie; without it, the cookie or the kept token is used ----
  async function signInFromAddress(): Promise<boolean> {
    const token = takeAddressToken(window.location, rest => window.history.replaceState(null, "", window.location.pathname + rest));
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
    const worker = setUpInstall(window, store);
    sessions.setFilter(keptProjects());
    showFilter();
    if (!await signInFromAddress()) return;
    route();
    await readList();
    if (signedOut) return;
    setUpNotifications(window, store, api, worker, text => showToast(text));
    stream.start();
    readProjects().then(showFilter, () => { /* the filter names the ids until the projects are read */ });
  }
  void start();
}
