"use strict";
const list = document.getElementById("sessions");
const note = document.getElementById("note");
const foot = document.getElementById("foot");

function ago(iso) {
  if (!iso) return "";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return Math.floor(s / 60) + " min ago";
  if (s < 86400) return Math.floor(s / 3600) + " h ago";
  return Math.floor(s / 86400) + " d ago";
}

const HOUR = 3600 * 1000;
const sessions = new Map();   // id -> session as last known, merged with its live state

// The dot: green while working, yellow while tools run, the thinking color while reasoning streams,
// blue (the assistant's color) once a turn is done, gray after an hour without activity.
function dot(s) {
  const live = s.live || "idle";
  if (live === "running" || live === "tools" || live === "thinking") return live;
  const at = Date.parse(s.at || s.lastActivity || "");
  return at && Date.now() - at < HOUR ? "answered" : "inactive";
}
const LABEL = { running: "working", tools: "tools", thinking: "thinking", answered: "done", inactive: "inactive" };

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

// ---- The folded activity line, ported from the desktop (frontend/src/timelineActivity/activityFold.ts) ----
const plainLine = line => line.replace(/^#+\s*/u, "").replace(/^[-*>]\s+/u, "").replace(/\*\*|__|`/gu, "").replace(/\s+/gu, " ").trim();
function thoughtHeadline(markdown) {
  if (!markdown) return null;
  const text = markdown.replaceAll("\r\n", "\n").trim();
  const title = /^\*\*([^*\n]+)\*\*/u.exec(text);
  if (title) return plainLine(title[1]) || null;
  const paragraphs = text.replace(/```[\s\S]*?(```|$)/gu, "").split(/\n\s*\n/u).map(p => plainLine(p.split("\n").map(plainLine).join(" "))).filter(Boolean);
  const last = paragraphs.at(-1);
  if (!last) return null;
  const sentences = last.split(/(?<=[.!?…])\s+(?=\S)/u).filter(Boolean);
  let line = sentences.at(-1) ?? last;
  if (line.length < 16 && sentences.length > 1) line = `${sentences.at(-2)} ${line}`;
  return line;
}
function activityDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return null;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return m < 60 ? `${m}m ${s % 60}s` : `${Math.floor(m / 60)}h ${m % 60}m`;
}
const ICON = {
  back: '<svg viewBox="0 0 24 24"><path d="M15 18l-6-6 6-6"/></svg>',
  chevron: '<svg viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M10 11v6M14 11v6M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>',
  archive: '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/></svg>',
  warning: '<svg viewBox="0 0 24 24"><path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18v.5"/></svg>',
  spinner: '<svg viewBox="0 0 24 24" class="spinner"><path d="M12 3a9 9 0 1 1-9 9"/></svg>',
  brain: '<svg viewBox="0 0 24 24"><path d="M9 4a3 3 0 0 0-3 3 3 3 0 0 0-2 5 3 3 0 0 0 2 5 3 3 0 0 0 6 1V5a3 3 0 0 0-3-1zM15 4a3 3 0 0 1 3 3 3 3 0 0 1 2 5 3 3 0 0 1-2 5 3 3 0 0 1-6 1"/></svg>',
  tool: '<svg viewBox="0 0 24 24"><path d="M14 6a4 4 0 0 0 5 5l-8 8a2 2 0 0 1-3-3l8-8a4 4 0 0 1-2-2z"/></svg>',
  external: '<svg viewBox="0 0 24 24"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/></svg>',
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>',
  timer: '<svg viewBox="0 0 24 24"><circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M10 2h4"/></svg>',
};
function svg(name, cls) { const s = el("span", "ico" + (cls ? " " + cls : "")); s.innerHTML = ICON[name]; s.setAttribute("aria-hidden", "true"); return s; }

// The line of an activity, as TimelineActivity renders it: mark, headline (or "Calling tools…" and the names), facts.
function activityLine(a, live, now = Date.now()) {
  const state = live ? "running" : a.failed > 0 ? "failed" : "done";
  const line = el("div", "act-line"); line.dataset.state = state;
  line.append(svg(live ? "spinner" : a.failed > 0 ? "warning" : "check", "act-mark"));
  const head = el("span", "act-headline", a.headline); if (a.thought) head.dataset.thought = "true";
  if (a.names) head.append(el("small", null, a.names));
  line.append(head);
  const facts = el("span", "act-facts");
  const fact = (icon, n, cls) => { const f = el("span", cls); f.append(svg(icon), document.createTextNode(n)); facts.append(f); };
  if (a.thoughts > 0) fact("brain", a.thoughts);
  if (a.tools > 0) fact("tool", a.tools);
  if (live && a.succeeded > 0) fact("check", a.succeeded, "ok");
  if (a.failed > 0) fact("close", a.failed, "bad");
  const start = Date.parse(a.startedAt || ""), end = Date.parse(a.endedAt || "");
  const d = activityDuration((live ? Math.max(now, end || 0) : end) - start);
  if (d) fact("timer", d, "dur");
  line.append(facts);
  return line;
}

// ---- The list of sessions ----
function card(s) {
  const state = dot(s);
  const li = el("li", "card " + state); li.dataset.id = s.id;
  const a = el("a", "card-link"); a.href = "#/session/" + encodeURIComponent(s.id);
  a.append(el("div", "title", s.title || "(untitled session)"));
  const meta = el("div", "meta");
  const tag = el("span", "tag"); tag.append(el("span", "dot"), document.createTextNode(LABEL[state]));
  meta.append(tag);
  if (s.project) meta.append(el("span", null, s.project));
  const when = s.at || s.lastActivity;
  if (when) meta.append(el("span", "ago", ago(when)));
  a.append(meta);
  if (s.kind === "activity" && s.activity) a.append(activityLine(s.activity, s.activity.state === "running"));
  else {
    const line = s.line || s.summary;
    if (line) { const l = el("div", "line", line); l.dataset.kind = s.line ? (s.kind || "assistant") : "summary"; a.append(l); }
  }
  li.append(a);
  return li;
}
// A row: the card over the action its drag does, Gmail-style: a drag to the right is the desktop's Archive
// (explorer/ExplorerSessions.tsx session menu: "archive" with the Archive icon; AppIcon.tsx maps it to lucide Archive).
// There is no Delete: CodeAlta offers plugins no public way to delete a session (no `alta session delete`).
function row(s) {
  const r = el("li", "row"); r.dataset.id = s.id;
  const under = el("div", "swipe-under"); under.setAttribute("aria-hidden", "true");
  const arch = el("span", "swipe-act archive"); arch.append(svg("archive"), el("span", null, "Archive"));
  under.append(arch);
  r.append(under, card(s));
  return r;
}

function render() {
  const items = [...sessions.values()].filter(s => !gone.has(s.id)).sort((a, b) => Date.parse(b.at || b.lastActivity || 0) - Date.parse(a.at || a.lastActivity || 0));
  // Cards are kept by id so that a state change transitions the outline color instead of redrawing it.
  const old = new Map([...list.children].map(li => [li.dataset.id, li]));
  const next = items.map(s => {
    const r = old.get(s.id);
    if (!r) return row(s);
    const fresh = card(s), c = r.querySelector(".card");
    c.className = fresh.className + (c.classList.contains("dragging") ? " dragging" : ""); c.replaceChildren(...fresh.childNodes);
    return r;
  });
  next.forEach((li, i) => { if (list.children[i] !== li) list.insertBefore(li, list.children[i] || null); });
  [...list.children].slice(next.length).forEach(li => li.remove());
  note.textContent = items.length ? "" : "No sessions yet.";
}

// Paging and the project filter: the server pages six at a time, newest first, within the chosen project.
const page = { next: 0, hasMore: false, project: (() => { try { return localStorage.getItem("alta.project") || ""; } catch (e) { return ""; } })() };
const moreButton = document.getElementById("more");
function mergeSession(s) {
  const live = s.live || {};
  sessions.set(s.id, { ...s, live: live.live || "idle", kind: live.kind, line: live.line, at: live.at, activity: live.activity, canSteer: !!live.canSteer });
}
async function fetchPage(offset) {
  const q = new URLSearchParams({ offset: String(offset) }); if (page.project) q.set("project", page.project);
  const response = await fetch("/api/sessions?" + q, { credentials: "same-origin" });
  // Without the cookie (an installed app that has its own) the kept token gets it again, once per tab.
  if (response.status === 401) {
    let kept = null; try { kept = localStorage.getItem("alta.token"); } catch { }
    if (kept && !sessionStorage.getItem("alta.reauth")) { sessionStorage.setItem("alta.reauth", "1"); location.replace("/?t=" + encodeURIComponent(kept)); return; }
  } else sessionStorage.removeItem("alta.reauth");
  if (response.status === 401) throw Object.assign(new Error("unauthorized"), { status: 401 });
  return response.json();
}
async function load() {
  note.textContent = sessions.size ? "" : "Loading…";
  try {
    const data = await fetchPage(0);
    sessions.clear();
    (data.sessions || []).forEach(mergeSession);
    page.next = data.next || 0; page.hasMore = !!data.hasMore;
    if (data.machine && data.machine !== machine) { machine = data.machine; connection(linkState); }
    render(); showFilter();
    moreButton.hidden = !page.hasMore;
    if (!sessions.size && data.error) note.textContent = data.error;
    foot.textContent = "Updated " + new Date().toLocaleTimeString();
  } catch (e) {
    if (e.status === 401) { note.textContent = "Not connected: scan the QR code in CodeAlta again."; list.replaceChildren(); return; }
    note.textContent = "CodeAlta is not reachable: " + e.message;
  }
}
moreButton.addEventListener("click", async () => {
  moreButton.disabled = true; moreButton.textContent = "Loading…";
  try {
    const data = await fetchPage(page.next);
    (data.sessions || []).forEach(mergeSession);
    page.next = data.next || page.next; page.hasMore = !!data.hasMore;
    render();
  } finally { moreButton.disabled = false; moreButton.textContent = "More"; moreButton.hidden = !page.hasMore; }
});

// The project filter: a sheet of the projects and "All projects"; the choice is kept and resets the paging.
let projects = null;
async function readProjects() {
  if (!projects) { const r = await fetch("/api/projects", { credentials: "same-origin" }); projects = await r.json(); }
  return projects;
}
const filterButton = document.getElementById("filter"), sheet = document.getElementById("sheet"), chip = document.getElementById("chip");
function showFilter() {
  const name = page.project ? (projects?.projects?.find(p => p.id === page.project)?.name || page.project) : "";
  filterButton.querySelector(".badge").hidden = !page.project;
  chip.hidden = !page.project; chip.querySelector("span").textContent = name;
}
function chooseProject(id) {
  page.project = id; save("alta.project", id); sheet.hidden = true; sessions.clear(); list.replaceChildren(); load();
}
filterButton.addEventListener("click", async () => {
  const data = await readProjects();
  const items = [{ id: "", name: "All projects" }, ...(data.projects || [])];
  document.getElementById("sheet-list").replaceChildren(...items.map(p => {
    const li = el("li"), b = el("button", p.id === page.project ? "on" : null, p.name); b.type = "button";
    if (p.path) b.append(el("small", null, p.path));
    b.addEventListener("click", () => chooseProject(p.id)); li.append(b); return li;
  }));
  sheet.hidden = false;
});
sheet.addEventListener("click", e => { if (e.target === sheet) sheet.hidden = true; });
chip.querySelector("button").addEventListener("click", () => chooseProject(""));
readProjects().then(showFilter).catch(() => { });

// ---- The session page: the timeline as the desktop folds it ----
const detail = { id: null, since: 0, events: [], expanded: new Set(), loading: null };
const timelineEl = document.getElementById("timeline");

function inline(text) {
  const span = el("span");
  // `code` and **bold**, everything else as text.
  for (const part of text.split(/(`[^`\n]+`|\*\*[^*\n]+\*\*)/u)) {
    if (!part) continue;
    if (part.startsWith("`") && part.endsWith("`") && part.length > 2) span.append(el("code", null, part.slice(1, -1)));
    else if (part.startsWith("**") && part.endsWith("**") && part.length > 4) span.append(el("strong", null, part.slice(2, -2)));
    else span.append(document.createTextNode(part));
  }
  return span;
}
function markdown(text) {
  const box = el("div", "md");
  const source = text.replaceAll("\r\n", "\n");
  // A fence opens or closes at the start of a line only: ``` within a sentence is text.
  const chunks = source.split(/^[ \t]*```[^\n]*\n?/mu), fences = [...source.matchAll(/^[ \t]*```([^\n]*)\n?/gmu)].map(m => m[1]);
  chunks.forEach((chunk, i) => {
    if (i % 2) {
      const code = chunk.replace(/\n$/u, ""), lang = (fences[i - 1] || "")   /* chunk i follows fence i - 1: every fence, opening or closing, splits */.trim().split(/\s/u)[0].toLowerCase();
      box.append(lang === "mermaid" ? diagram(code) : el("pre", null, code)); return;
    }
    for (const para of chunk.split(/\n\s*\n/u)) {
      if (!para.trim()) continue;
      const lines = para.split("\n");
      if (lines.every(l => /^\s*([-*]|\d+\.)\s+/u.test(l))) {
        const ul = el("ul"); lines.forEach(l => { const li = el("li"); li.append(inline(l.replace(/^\s*([-*]|\d+\.)\s+/u, ""))); ul.append(li); }); box.append(ul);
      } else if (/^#+\s/u.test(para)) { const h = el("h3"); h.append(inline(para.replace(/^#+\s*/u, ""))); box.append(h); }
      else { const p = el("p"); lines.forEach((l, j) => { if (j) p.append(el("br")); p.append(inline(l)); }); box.append(p); }
    }
  });
  return box;
}

// Folds the events like foldTimeline: prompts and answers stand alone; the work between them is one activity.
function foldEvents(events) {
  const blocks = []; let run = null;
  const close = () => { if (run) blocks.push(run); run = null; };
  for (const e of events) {
    if (e.kind === "user.message" || e.kind === "assistant.message" || /error/u.test(e.kind)) { close(); blocks.push({ kind: "row", event: e }); continue; }
    if (!(e.kind === "assistant.reasoning" || e.kind.startsWith("activity."))) continue;
    if (!run) run = { kind: "activity", key: "a" + e.seq, rows: [], tools: new Map() };
    if (e.kind === "assistant.reasoning") { if (e.text.trim()) run.rows.push({ type: "thought", event: e }); continue; }
    const id = e.activityId || "t" + e.seq;
    let tool = run.tools.get(id);
    if (!tool) { tool = { type: "tool", title: e.text || "tool", phase: "requested", at: e.at, end: e.at }; run.tools.set(id, tool); run.rows.push(tool); }
    tool.phase = e.kind.split(".").pop(); tool.end = e.at;
  }
  close();
  return blocks;
}
function summarize(block) {
  let headline = null, thoughts = 0, tools = 0, succeeded = 0, failed = 0, running = 0, start = NaN, end = NaN; const names = [];
  const time = iso => { const t = Date.parse(iso); if (Number.isFinite(t)) { if (!(t >= start)) start = t; if (!(t <= end)) end = t; } };
  for (const row of block.rows) {
    if (row.type === "thought") { thoughts++; time(row.event.at); const l = thoughtHeadline(row.event.text); if (l) headline = l; continue; }
    tools++; time(row.at); time(row.end);
    if (row.phase === "completed") succeeded++; else if (row.phase === "failed") failed++; else if (row.phase !== "canceled") running++;
    if (names.length < 3 && !names.includes(row.title)) names.push(row.title);
  }
  return { headline, thoughts, tools, succeeded, failed, running, names, startedAt: start, endedAt: end };
}
function activityFacts(s, live) {
  const headline = s.headline ?? (s.tools > 0 ? (live ? "Calling tools…" : "Called tools") : (live ? "Working…" : "Worked"));
  return { ...s, thought: !!s.headline, headline, names: !s.headline && s.names.length ? s.names.join(", ") : null,
    startedAt: new Date(s.startedAt || 0).toISOString(), endedAt: new Date(s.endedAt || 0).toISOString() };
}

function renderDetail() {
  const s = sessions.get(detail.id);
  document.getElementById("detail-title").textContent = s?.title || "Session";
  const busy = s && ["running", "tools", "thinking"].includes(s.live);
  showComposer(!!busy);
  const blocks = foldEvents(detail.events);
  const atBottom = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 40;
  timelineEl.replaceChildren(...blocks.map((b, i) => {
    const li = el("li");
    if (b.kind === "row") {
      const e = b.event, type = e.kind === "user.message" ? "user" : e.kind === "assistant.message" ? "assistant" : "error";
      li.className = "msg msg-" + type;
      li.append(el("div", "msg-label", type === "user" ? "You" : type === "assistant" ? "Agent" : "Error"), markdown(e.text || ""));
      return li;
    }
    const live = busy && i === blocks.length - 1;
    const sum = summarize(b), open = detail.expanded.has(b.key);
    // A tool still "running" in a turn that is over never ended: the turn was stopped or failed under it.
    if (!live && sum.running > 0) { sum.failed += sum.running; sum.running = 0; }
    li.className = "activity"; li.dataset.state = live ? "running" : sum.failed > 0 ? "failed" : "done"; li.dataset.expanded = open;
    const button = el("button", "act-toggle"); button.type = "button"; button.setAttribute("aria-expanded", open);
    button.append(svg("chevron", "act-chevron"), activityLine(activityFacts(sum, live), live));
    button.addEventListener("click", () => { open ? detail.expanded.delete(b.key) : detail.expanded.add(b.key); renderDetail(); });
    li.append(button);
    if (open) {
      const body = el("div", "act-body");
      for (const row of b.rows) {
        if (row.type === "thought") { const t = el("div", "msg msg-reasoning"); t.append(el("div", "msg-label", "Thinking"), markdown(row.event.text)); body.append(t); }
        else { const t = el("div", "tool-row"); t.dataset.phase = row.phase; t.append(el("span", "tool-dot"), el("strong", null, row.title), el("span", "tool-phase", row.phase)); body.append(t); }
      }
      li.append(body);
    }
    return li;
  }));
  document.getElementById("detail-note").textContent = blocks.length ? "" : (detail.loading ? "Loading…" : "No messages yet.");
  padComposer();
  if (atBottom && detail.follow !== false) window.scrollTo(0, document.documentElement.scrollHeight);
}

async function loadDetail() {
  if (!detail.id) return;
  if (detail.loading) { detail.again = true; return; }
  const id = detail.id;
  detail.loading = (async () => {
    try {
      const r = await fetch(`/api/sessions/${encodeURIComponent(id)}/events?since=${detail.since}`, { credentials: "same-origin" });
      if (!r.ok) throw new Error("HTTP " + r.status);
      const data = await r.json();
      if (detail.id !== id) return;
      detail.events.push(...(data.events || []));
      detail.since = data.since || detail.since;
      if (data.error) document.getElementById("detail-note").textContent = data.error;
    } catch (e) { document.getElementById("detail-note").textContent = "Could not read the session: " + e.message; }
  })();
  await detail.loading; detail.loading = null;
  if (detail.id === id) renderDetail();
  if (detail.again) { detail.again = false; loadDetail(); }
}

// ---- Routes: #/ is the list, #/session/{id} the session ----
const back = document.getElementById("back"), brand = document.getElementById("brand");
function route() {
  const m = /^#\/session\/(.+)$/u.exec(location.hash);
  const id = m ? decodeURIComponent(m[1]) : null;
  const creating = location.hash === "#/new";
  document.getElementById("home").hidden = !!id || creating;
  document.getElementById("detail").hidden = !id;
  document.getElementById("create").hidden = !creating;
  back.hidden = !id && !creating;
  filterButton.hidden = !!id || creating;
  const plus = document.getElementById("new"); plus.classList.toggle("on", creating); plus.toggleAttribute("aria-current", creating);
  document.body.classList.toggle("with-composer", !!id);
  if (creating) openCreate();
  if (id && id !== detail.id) {
    Object.assign(detail, { id, since: 0, events: [], expanded: new Set() });
    timelineEl.replaceChildren();
    detail.first = true;
    loadDetail().then(() => { window.scrollTo(0, document.documentElement.scrollHeight); });
  }
  if (!id) { detail.id = null; render(); }
}
back.addEventListener("click", () => { location.hash = "#/"; });
window.addEventListener("hashchange", route);

// Live updates: EventSource reconnects by itself; after a reconnect the list is fetched again.
let reload = 0, detailTimer = 0;
function listen() {
  const events = new EventSource("/api/events");
  events.addEventListener("open", () => connection("live"));
  // EventSource retries by itself while readyState is CONNECTING; CLOSED means it gave up (a refused request).
  events.addEventListener("error", () => connection(events.readyState === EventSource.CLOSED ? "offline" : "reconnecting"));
  events.addEventListener("session", e => {
    const u = JSON.parse(e.data);
    const s = sessions.get(u.id);
    if (!s) { clearTimeout(reload); reload = setTimeout(load, 500); return; }   // a session not in the list yet
    Object.assign(s, { live: u.live, kind: u.kind, line: u.line || s.line, at: u.at || s.at, activity: u.activity, canSteer: !!u.canSteer });
    if (detail.id === u.id) showComposer(["running", "tools", "thinking"].includes(u.live));
    if (detail.id === u.id) { clearTimeout(detailTimer); detailTimer = setTimeout(loadDetail, 400); }
    else if (!detail.id) render();
    connection("live");
  });
}
// The footer: "● SER9 · Live · 1:54:03 PM", the dot and word from the state of the event stream, the time of its
// last news (open or event).
let machine = "", linkState = "reconnecting", linkAt = null;
function connection(state) {
  linkState = state; if (state === "live") linkAt = new Date();
  const word = { live: "Live", reconnecting: "Reconnecting…", offline: "Offline" }[state];
  foot.replaceChildren(el("span", "link-dot " + state), document.createTextNode([machine, word, state === "live" && linkAt ? linkAt.toLocaleTimeString() : null].filter(Boolean).join(" · ")));
}
window.addEventListener("offline", () => connection("offline"));
// "min ago", the hour after which a dot turns gray, and the running time of an activity.
setInterval(() => { if (detail.id) renderDetail(); else render(); }, 60 * 1000);

// The commands of the top bar. A new one is an .icon-btn in index.html and a handler here.
const root = document.documentElement;
const SUN = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>';
const MOON = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>';
function save(key, value) { try { localStorage.setItem(key, value); } catch (e) { } }

const themeButton = document.getElementById("theme");
function showTheme() {
  const dark = root.dataset.theme === "dark";
  themeButton.innerHTML = dark ? SUN : MOON;   // the icon shows what a tap switches to
  themeButton.title = dark ? "Light mode" : "Dark mode";
  document.querySelector('meta[name="theme-color"]').content = dark ? "#252a31" : "#ffffff";
}
themeButton.addEventListener("click", () => {
  root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
  save("alta.theme", root.dataset.theme);
  showTheme();
});

const fontButton = document.getElementById("font");
function showFont() {
  fontButton.querySelector(".font-step").textContent = root.dataset.fontStep;
  fontButton.title = "Text size " + root.dataset.fontStep + " of 5";
}
fontButton.addEventListener("click", () => {
  root.dataset.fontStep = String(+root.dataset.fontStep % 5 + 1);
  save("alta.fontStep", root.dataset.fontStep);
  showFont();
});

const refreshButton = document.getElementById("refresh");
refreshButton.addEventListener("click", async () => {
  refreshButton.classList.add("spin");
  try { await load(); if (detail.id) { Object.assign(detail, { since: 0, events: [] }); await loadDetail(); } } finally { refreshButton.classList.remove("spin"); }
});
showTheme();
showFont();
document.addEventListener("visibilitychange", () => { if (!document.hidden) load(); });
load().then(() => { route(); listen(); });

// ---- The composer of a session, after the desktop's: Send when idle; while a turn runs, "Send now (steer)" (the
// primary action), "Enqueue until idle" and Abort. Same icons: send, steer (corner-up-right), queue (list-plus), stop.
const ICON_SEND = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M22 2L11 13M22 2l-7 20-4-9-9-4z"/></svg>';
const ICON_STEER = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 14l5-5-5-5"/><path d="M4 20v-7a4 4 0 0 1 4-4h12"/></svg>';
const composer = document.getElementById("composer"), promptBox = document.getElementById("prompt");
const sendButton = document.getElementById("send"), queueButton = document.getElementById("queue"), abortButton = document.getElementById("abort");
let composerBusy = false, stopping = 0, stoppedAt = 0;
const hint = document.getElementById("composer-hint");
// Idle: Send. While a turn runs: only Stop until new text is typed, then Steer (into this turn) and Queue (after it).
// Idle: Send. While a turn runs: only Stop until new text is typed, then Queue (after this turn), and Steer (into
// it) when the server says the session can be steered (canSteer).
function showComposer(busy) {
  if (!busy && stopping) endStopping();
  composerBusy = busy;
  const typed = promptBox.value.trim().length > 0;
  const steerable = busy && !!sessions.get(detail.id)?.canSteer;
  sendButton.innerHTML = (busy ? ICON_STEER : ICON_SEND) + `<span>${busy ? "Steer" : "Send"}</span>`;
  sendButton.title = busy ? "Send now, into the running turn" : "Send the prompt";
  sendButton.hidden = busy && (!typed || !steerable);
  queueButton.hidden = !busy || !typed;
  abortButton.hidden = !busy;
  hint.textContent = !busy ? "" : typed ? (steerable ? "Steer: into this turn · Queue: runs after this turn" : "Queue: runs after this turn")
    : (steerable ? "Working… type to steer or queue" : "Working… type to queue");
}
function clearDraft() { try { localStorage.removeItem("alta.draft." + detail.id); } catch (e) { } }
async function post(url, body) {
  const r = await fetch(url, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });
  const data = await r.json().catch(() => ({}));
  if (!r.ok || data.error) throw new Error(data.error || "HTTP " + r.status);
  return data;
}
async function submitPrompt(mode) {
  const text = promptBox.value.trim();
  if (!text || !detail.id) return;
  composer.classList.add("sending");
  const noteEl = document.getElementById("detail-note");
  // The prompt leaves the input (and its draft) as it is sent: the request can take a while to return while the
  // turn already runs. If it fails, the text comes back to send again.
  promptBox.value = ""; clearDraft(); grow(); showComposer(composerBusy);
  const sentAt = Date.now();
  try {
    const r = await post(`/api/sessions/${encodeURIComponent(detail.id)}/prompt`, { text, mode });
    noteEl.textContent = r.note || (r.mode === "queue" ? "Queued: runs after this turn." : "");
    window.scrollTo(0, document.documentElement.scrollHeight);
  } catch (e) {
    // A turn the user stopped ends its request with an error: that prompt was sent, and is not given back.
    if (stoppedAt >= sentAt) { noteEl.textContent = "Stopped."; return; }
    if (!promptBox.value.trim()) { promptBox.value = text; grow(); }
    noteEl.textContent = "Not sent: " + e.message;
  }
  finally { composer.classList.remove("sending"); showComposer(composerBusy); }
}
composer.addEventListener("submit", e => {
  e.preventDefault();
  if (!composerBusy) submitPrompt("send");
  else submitPrompt(sessions.get(detail.id)?.canSteer ? "steer" : "queue");
});
queueButton.addEventListener("click", () => submitPrompt("queue"));
// Stop: one tap, no confirmation; the button says "Stopping…" until the turn leaves the running state (or 15 s).
abortButton.addEventListener("click", async () => {
  if (stopping) return;
  stoppedAt = Date.now();
  abortButton.disabled = true; abortButton.lastChild.textContent = "Stopping…";
  stopping = setTimeout(endStopping, 15000);
  try { await post(`/api/sessions/${encodeURIComponent(detail.id)}/abort`); }
  catch (e) { document.getElementById("detail-note").textContent = "Not stopped: " + e.message; endStopping(); }
});
function endStopping() { clearTimeout(stopping); stopping = 0; abortButton.disabled = false; abortButton.lastChild.textContent = "Stop"; }
function grow() { promptBox.style.height = "auto"; promptBox.style.height = Math.min(promptBox.scrollHeight, 160) + "px"; padComposer(); }
promptBox.addEventListener("input", () => { grow(); showComposer(composerBusy); });
// The pane sits on the keyboard: the visual viewport says how much of the layout viewport the keyboard covers.
function padComposer() {
  const vv = window.visualViewport;
  const covered = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) : 0;
  composer.style.bottom = covered + "px";
  document.getElementById("detail").style.paddingBottom = (composer.offsetHeight + covered + 16) + "px";
}
window.visualViewport?.addEventListener("resize", padComposer);
window.visualViewport?.addEventListener("scroll", padComposer);
showComposer(false);

// ---- New session: the desktop's composer selection form (agent prompt, provider, model with its tiers, reasoning) ----
// As the desktop: the provider filters the models; the model list opens with the tiers of the settings at its top, a
// tier fills provider, model and effort and stays marked until one of them is changed by hand; reasoning is a stepped
// slider over the efforts of the model, or plain text when there is nothing to choose. Choices are remembered per
// project, as codealta.desktop.newSessionPreferences.v1 does on the desktop (a provider choice forgets the model).
let options = null;
const $id = i => document.getElementById(i);
const newProject = $id("new-project"), newAgent = $id("new-agent"), newProvider = $id("new-provider"), newNote = $id("new-note");
const draft = { provider: "", model: "", effort: "", tier: null };
const PREFS = "codealta.mobile.newSessionPreferences.v1";
const prefs = () => { try { return JSON.parse(localStorage.getItem(PREFS)) || {}; } catch { return {}; } };
function remember(p) { const all = prefs(); all[newProject.value] = p; try { localStorage.setItem(PREFS, JSON.stringify(all)); } catch { } }
function option(value, label, selected) { const o = el("option", null, label); o.value = value; o.selected = !!selected; return o; }
const modelsOf = p => (options?.models || []).filter(m => m.provider === p);
const groupsOf = p => groupVariants(modelsOf(p));
const groupOf = (p, id) => groupsOf(p).find(g => g.members.some(x => x.m.id === id));
const entryName = m => m.name.replace(/\s+No Thinking\b/i, "");
const VARIANT_LABEL = { thinking: "Thinking", fast: "Fast" };
const providerName = p => options?.models.find(m => m.provider === p)?.providerName || p;
const TIER_LABEL = { complex: "Complex", standard: "Standard", quick: "Quick" };
// A model of the provider, always one it lists: the wanted one, else alta's default model when it is this provider's,
// else the first entry of the frontier (as the model list orders them).
function pickModel(p, wanted) {
  const ms = modelsOf(p), d = options?.defaults;
  const m = ms.find(x => x.id === wanted) || (d?.provider === p ? ms.find(x => x.id === d.model) : null)
    || orderModels(groupVariants(ms).map(g => g.entry)).frontier[0] || ms[0];
  draft.model = m?.id || ""; return m;
}
function pickEffort(m, wanted) {
  const ef = m?.efforts || [], d = options?.defaults;
  const configured = d && m && d.provider === m.provider && d.model === m.id ? d.reasoning : null;   // the settings' effort, as the desktop
  draft.effort = [wanted, configured, m?.defaultEffort].find(e => e && ef.includes(e)) || ef[0] || "";
}
function applyTier(name) {
  const t = options.tiers.find(x => x.tier === name); if (!t?.model) return;
  draft.tier = name; draft.provider = t.provider; pickEffort(pickModel(t.provider, t.model), t.reasoning);
}
function renderDraft() {
  newProvider.replaceChildren(...[...new Set((options?.models || []).map(m => m.provider))].map(p => option(p, providerName(p), p === draft.provider)));
  const m = modelsOf(draft.provider).find(x => x.id === draft.model);
  const g0 = groupOf(draft.provider, draft.model);
  $id("new-model-name").textContent = (draft.tier ? TIER_LABEL[draft.tier] + " · " : "") + ((g0 && entryName(g0.entry)) || m?.name || draft.model || "No model");
  // The options of the model: a checkbox per variant its entry has; a combination that does not exist is disabled.
  const g = groupOf(draft.provider, draft.model), vbox = $id("new-variants");
  const flags = g ? g.members.find(x => x.m.id === draft.model).flags.filter(f => !g.fixed.includes(f)) : [];
  vbox.hidden = !g || g.options.length === 0;
  vbox.replaceChildren(...(g?.options || []).map(f => {
    const on = flags.includes(f), next = on ? flags.filter(x => x !== f) : [...flags, f], target = g.find(next);
    const label = el("label", "check"), box = el("input"); box.type = "checkbox"; box.checked = on; box.disabled = !target;
    box.onchange = () => { draft.model = target.id; draft.tier = null; pickEffort(target, draft.effort);
      remember({ agentPromptId: newAgent.value, providerKey: draft.provider, modelId: target.id }); renderDraft(); };
    label.append(box, document.createTextNode(" " + VARIANT_LABEL[f])); return label;
  }));
  const box = $id("new-effort"), ef = m?.efforts || [];
  $id("new-effort-field").hidden = ef.length === 0;       // no effort levels: no Reasoning section at all
  if (ef.length < 2) { box.replaceChildren(el("span", "reasoning-static", ef[0] || "")); return; }
  const range = el("input"); range.type = "range"; range.min = 0; range.max = ef.length - 1; range.step = 1; range.value = Math.max(0, ef.indexOf(draft.effort));
  range.setAttribute("aria-label", "Reasoning"); range.style.setProperty("--stops", ef.length);
  const labels = el("div", "reasoning-labels"); labels.style.setProperty("--stops", ef.length);
  ef.forEach((e, n) => { const b = el("button", n == range.value ? "on" : null, e); b.type = "button"; b.style.left = (n / (ef.length - 1) * 100) + "%"; b.onclick = () => setEffort(e); labels.append(b); });
  range.oninput = () => setEffort(ef[range.value]);
  box.replaceChildren(range, labels);
}
function setEffort(e) { if (e === draft.effort) return; draft.effort = e; draft.tier = null; remember({ ...prefs()[newProject.value], providerKey: draft.provider, modelId: draft.model, reasoningEffort: e, tier: undefined }); renderDraft(); }
newProvider.addEventListener("change", () => {
  draft.provider = newProvider.value; draft.tier = null; pickEffort(pickModel(draft.provider));
  remember({ agentPromptId: newAgent.value, providerKey: draft.provider }); renderDraft();
});
newAgent.addEventListener("change", () => remember({ ...prefs()[newProject.value], agentPromptId: newAgent.value }));
// The model list: a bottom sheet where the desktop has a popover.
const modelSheet = $id("model-sheet"), modelSearch = $id("model-search");
function renderModelList() {
  const q = modelSearch.value.trim().toLowerCase();
  const groups = groupsOf(draft.provider), byEntry = new Map(groups.map(g => [g.entry.id, g]));
  const { frontier, other } = orderModels(groups.filter(g => !q || g.members.some(x => (x.m.name + " " + x.m.id).toLowerCase().includes(q))).map(g => g.entry));
  const header = () => { const li = el("li", "model-sep", "Other models"); li.setAttribute("role", "separator"); return li; };
  $id("model-list").replaceChildren(...[...frontier, ...(other.length && frontier.length ? [null] : []), ...other].map(m => {
    if (m === null) return header();
    const g = byEntry.get(m.id), mine = g.members.some(x => x.m.id === draft.model);
    const b = el("button", mine ? "on" : null); b.type = "button"; b.setAttribute("role", "option");
    b.append(el("span", null, entryName(m))); b.append(el("small", null, m.id + (g.options.length ? " (" + [...g.options].sort().map(f => "+" + f).join(", ") + ")" : "")));
    b.onclick = () => { if (!mine || draft.tier) { draft.model = m.id; draft.tier = null; pickEffort(m, draft.effort);
      remember({ agentPromptId: newAgent.value, providerKey: draft.provider, modelId: m.id }); } modelSheet.hidden = true; renderDraft(); };
    const li = el("li"); li.append(b); return li;
  }));
}
$id("new-model").addEventListener("click", () => {
  $id("tiers").replaceChildren(...(options?.tiers || []).map(t => {
    const b = el("button", "tier" + (t.tier === draft.tier ? " on" : "")); b.type = "button"; b.disabled = !t.model; b.setAttribute("aria-pressed", t.tier === draft.tier);
    const m = (options.models || []).find(x => x.provider === t.provider && x.id === t.model);
    b.append(el("span", "tier-name", TIER_LABEL[t.tier] || t.tier), el("span", "tier-model", t.model ? (m?.name || t.model) : "No model ready"));
    b.onclick = () => { applyTier(t.tier); remember({ agentPromptId: newAgent.value, providerKey: draft.provider, modelId: draft.model, reasoningEffort: draft.effort, tier: t.tier }); modelSheet.hidden = true; renderDraft(); };
    return b;
  }));
  $id("tiers").hidden = !(options?.tiers || []).some(t => t.model);
  modelSearch.value = ""; renderModelList(); modelSheet.hidden = false;
});
modelSearch.addEventListener("input", renderModelList);
modelSheet.addEventListener("click", e => { if (e.target === modelSheet) modelSheet.hidden = true; });
function initDraft() {
  const stored = prefs()[newProject.value] || {}, o = options;
  // Empty values (null, "") are no pick: they are dropped, and so forgotten below.
  const saved = Object.fromEntries(Object.entries(stored).filter(([, v]) => typeof v === "string" && v));
  newAgent.value = o.prompts.some(p => p.id === saved.agentPromptId) ? saved.agentPromptId : o.defaults?.prompt || "default";
  const providers = new Set(o.models.map(m => m.provider));
  const standard = o.tiers.find(t => t.tier === "standard" && t.model);
  if (saved.tier && o.tiers.some(t => t.tier === saved.tier && t.model === saved.modelId && t.provider === saved.providerKey)) { applyTier(saved.tier); return; }
  if (!saved.providerKey && standard) { applyTier("standard"); return; }       // the desktop's default: the standard tier
  // Only a provider that lists models now: alta's default provider may have none (copilot, when it is not ready).
  draft.tier = null; draft.provider = providers.has(saved.providerKey) ? saved.providerKey : providers.has(o.defaults?.provider) ? o.defaults.provider : [...providers][0] || "";
  const m = pickModel(draft.provider, saved.providerKey === draft.provider ? saved.modelId : undefined);
  pickEffort(m, saved.reasoningEffort);
  // A saved pick that is no longer offered is forgotten, not kept to fail again.
  const valid = { ...saved };
  if (saved.providerKey && saved.providerKey !== draft.provider) { delete valid.providerKey; delete valid.modelId; delete valid.reasoningEffort; delete valid.tier; }
  if (saved.modelId && saved.modelId !== draft.model) { delete valid.modelId; delete valid.reasoningEffort; delete valid.tier; }
  if (saved.reasoningEffort && saved.reasoningEffort !== draft.effort) delete valid.reasoningEffort;
  if (JSON.stringify(valid) !== JSON.stringify(stored)) remember(valid);
}
async function openCreate(fresh = false) {
  newNote.textContent = options ? "" : "Loading…";
  try {
    const [p, o] = await Promise.all([readProjects(), options || fetch("/api/options", { credentials: "same-origin" }).then(r => r.json())]);
    options = o; o.tiers ||= []; o.models ||= []; o.prompts ||= [];
    newProject.replaceChildren(...(p.projects || []).map(x => option(x.id, x.name, x.id === (fresh ? p.default : page.project || p.default))));
    newAgent.replaceChildren(...o.prompts.map(x => option(x.id, x.name, x.id === o.defaults?.prompt)));
    initDraft(); renderDraft();
    newNote.textContent = o.error || "";
  } catch (e) { newNote.textContent = "Could not read the choices: " + e.message; }
}
newProject.addEventListener("change", () => { if (options) { initDraft(); renderDraft(); } });
$id("new-form").addEventListener("submit", async e => {
  e.preventDefault();
  const start = $id("new-start"); start.disabled = true; newNote.textContent = "Starting…";
  try {
    const data = await post("/api/sessions", { project: newProject.value, prompt: newAgent.value || undefined,
      ...(draft.tier ? { tier: draft.tier } : { provider: draft.provider, model: draft.model, reasoning: draft.effort || undefined }),
      worktree: $id("new-worktree").checked, text: $id("new-text").value });
    $id("new-text").value = ""; newNote.textContent = data.sendError ? "Created, but the prompt was not sent: " + data.sendError : "";
    await load();
    location.hash = "#/session/" + encodeURIComponent(data.id);
  } catch (err) {
    newNote.textContent = "Not started: " + (err instanceof TypeError ? "the connection to " + location.host + " failed. Check that this phone is on Tailscale, then try again." : err.message);
  }
  finally { start.disabled = false; }
});
// "+" on the new-session screen starts the form again from the settings: the choices are read again from alta
// (model resolve, prompt list, project list), the picks remembered here are forgotten, the prompt is cleared.
document.getElementById("new").addEventListener("click", () => {
  if (location.hash !== "#/new") { location.hash = "#/new"; return; }
  try { localStorage.removeItem(PREFS); } catch { }
  options = null; $id("new-text").value = ""; $id("new-worktree").checked = false; modelSheet.hidden = true;
  openCreate(true);
});

// ---- Swipe a card for its action, Gmail-style: the drag is the action ----
// Right: Archive (alta session archive, as the desktop's Archive), on green. Past 40% of the width a release archives
// and the card leaves; short of it the card springs back. A drag that starts vertical is the page's scroll
// (touch-action: pan-y). A toast offers Restore for 5 s, which un-archives through alta (alta session restore).
const gone = new Set();          // cards swiped away, kept out of the list while their action settles
let swipe = null;
function place(r, x, how) {
  const c = r.querySelector(".card");
  c.classList.toggle("springing", how === "spring"); c.classList.toggle("leaving-x", how === "out");
  c.style.transform = x ? `translateX(${x}px)` : "";
  r.dataset.dir = x > 0 ? "archive" : "";
  r.dataset.armed = String(Math.abs(x) >= r.offsetWidth * .4);
}
list.addEventListener("pointerdown", e => {
  const c = e.target.closest(".card"); if (!c || e.button || swipe) return;
  swipe = { r: c.parentElement, c, x: e.clientX, y: e.clientY, dx: 0, mode: null, id: e.pointerId };
});
list.addEventListener("pointermove", e => {
  if (!swipe || e.pointerId !== swipe.id) return;
  const dx = e.clientX - swipe.x, dy = e.clientY - swipe.y;
  if (!swipe.mode) {
    if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) { swipe = null; return; }   // a scroll
    if (Math.abs(dx) < 10) return;
    swipe.mode = "drag"; try { swipe.c.setPointerCapture(e.pointerId); } catch (err) { }
  }
  swipe.dx = dx; place(swipe.r, dx > 0 ? dx : dx * .15);   // left resists: it has no action
});
function endSwipe(e) {
  if (!swipe || e.pointerId !== swipe.id) return;
  const s = swipe; swipe = null;
  if (s.mode !== "drag") return;
  s.r.dataset.dragged = "1"; setTimeout(() => delete s.r.dataset.dragged, 0);
  const w = s.r.offsetWidth;
  if (e.type === "pointerup" && s.dx >= w * .4) {
    place(s.r, w + 24, "out");
    act(s.r, "archive");
  } else place(s.r, 0, "spring");
}
list.addEventListener("pointerup", endSwipe);
list.addEventListener("pointercancel", endSwipe);
// The tap that ends a drag does not open the session.
list.addEventListener("click", e => { if (e.target.closest(".row")?.dataset.dragged) { e.preventDefault(); e.stopPropagation(); } }, true);

const toastEl = document.getElementById("toast");
let toast = null;   // { kind, id, timer, settle() }
function showToast(text, restore) {
  toastEl.replaceChildren(el("span", "toast-text", text));
  if (restore) { const b = el("button", "toast-action", "Restore"); b.type = "button"; b.onclick = restore; toastEl.append(b); }
  toastEl.hidden = false; toastEl.classList.remove("toast-out"); void toastEl.offsetWidth; toastEl.classList.add("toast-in");
}
function hideToast() { toastEl.classList.remove("toast-in"); toastEl.hidden = true; }
// Settles the current toast now.
function settleToast() { if (toast) { clearTimeout(toast.timer); const t = toast; toast = null; hideToast(); t.settle(); } }
function collapse(r) { r.style.height = r.offsetHeight + "px"; void r.offsetHeight; r.classList.add("collapsing"); r.style.height = "0px"; }
function bringBack(id) {
  gone.delete(id); const r = list.querySelector(`.row[data-id="${CSS.escape(id)}"]`);
  if (r) { r.classList.remove("collapsing"); r.style.height = ""; place(r, 0, "spring"); }
  render();
}
function act(r) {
  settleToast();
  const id = r.dataset.id, title = sessions.get(id)?.title || "Session";
  gone.add(id); setTimeout(() => collapse(r), 180);
  const archiving = post(`/api/sessions/${encodeURIComponent(id)}/archive`);
  archiving.catch(err => { if (toast?.id === id) { clearTimeout(toast.timer); toast = null; } bringBack(id); showToast("Not archived: " + err.message); setTimeout(hideToast, 5000); });
  const restore = async () => {
    if (!toast || toast.id !== id) return;
    clearTimeout(toast.timer); toast = null; hideToast();
    try { await archiving; await post(`/api/sessions/${encodeURIComponent(id)}/restore`); }
    catch (err) { showToast("Not restored: " + err.message); setTimeout(hideToast, 5000); return; }
    bringBack(id);
  };
  toast = { id, settle: () => {} };
  toast.timer = setTimeout(settleToast, 5000);
  showToast(`Archived \u201c${title.length > 40 ? title.slice(0, 39) + "\u2026" : title}\u201d`, restore);
}

// ---- Diagrams: fenced `mermaid` blocks drawn as the desktop draws them (frontend/src/diagrams.ts) ----
// The same Mermaid (11.17.2), the same strict configuration and the same theme variables, taken from the colors of
// the page; drawn once per text and theme, one at a time, on the side. A text Mermaid refuses stays code, with the
// first line of what it said under it. A diagram wider than the phone scrolls sideways; a tap opens it full screen,
// where it pinches and pans.
const MAX_DIAGRAM = 20000, drawnDiagrams = new Map();
let mermaidLib = null, diagramQueue = Promise.resolve(), diagramSeq = 0;
function mix(base, over, amount) {
  const ch = (c, i) => parseInt(c.slice(1 + 2 * i, 3 + 2 * i), 16);
  return "#" + [0, 1, 2].map(i => Math.round(ch(base, i) * (1 - amount) + ch(over, i) * amount).toString(16).padStart(2, "0")).join("");
}
function diagramLook() {
  const root = document.documentElement, dark = root.dataset.theme !== "light";
  const probe = root.appendChild(document.createElement("span"));
  const color = (p, fb) => { probe.style.color = `var(${p})`; const m = /^rgb\((\d+), (\d+), (\d+)\)$/u.exec(getComputedStyle(probe).color); return m ? "#" + m.slice(1).map(c => Number(c).toString(16).padStart(2, "0")).join("") : fb; };
  const surface = color("--panel-2", dark ? "#2f343c" : "#f6f7f9"), text = color("--text", dark ? "#f6f7f9" : "#1c2127"), muted = color("--muted", dark ? "#abb3bf" : "#5f6b7c");
  const accent = color("--accent", dark ? "#4c90f0" : "#2d72d2"), user = color("--user-accent", dark ? "#9d3f9d" : "#7c327c"), green = color("--green", dark ? "#32a467" : "#238551");
  const yellow = color("--yellow", dark ? "#f0b726" : "#c87619"), red = color("--red", dark ? "#e76a6e" : "#cd4246");
  probe.remove();
  const hues = [accent, user, green, yellow, red, muted];
  const slices = Object.fromEntries([...hues.map(h => mix(surface, h, .8)), ...hues.map(h => mix(surface, h, .45))].map((v, i) => [`pie${i + 1}`, v]));
  const node = mix(surface, accent, dark ? .22 : .12);
  return { key: root.dataset.theme || "", variables: {
    darkMode: dark, background: surface, fontFamily: getComputedStyle(root).fontFamily, fontSize: "13px",
    primaryColor: node, primaryTextColor: text, primaryBorderColor: mix(surface, accent, .7),
    secondaryColor: mix(surface, user, dark ? .24 : .12), secondaryTextColor: text, secondaryBorderColor: mix(surface, user, .6),
    tertiaryColor: mix(surface, green, dark ? .2 : .1), tertiaryTextColor: text, tertiaryBorderColor: mix(surface, green, .6),
    lineColor: muted, textColor: text, mainBkg: node, nodeBorder: mix(surface, accent, .7),
    clusterBkg: mix(surface, text, .04), clusterBorder: mix(surface, text, .3), titleColor: text,
    edgeLabelBackground: surface, noteBkgColor: mix(surface, yellow, dark ? .25 : .3), noteTextColor: text, noteBorderColor: mix(surface, yellow, .6),
    ...slices, pieStrokeColor: surface, pieStrokeWidth: "2px", pieOuterStrokeColor: mix(surface, text, .3), pieOuterStrokeWidth: "1px",
    pieTitleTextColor: text, pieSectionTextColor: dark ? "#ffffff" : "#111418", pieLegendTextColor: text } };
}
function loadMermaid() {
  return mermaidLib ??= new Promise((ok, fail) => { const s = document.createElement("script"); s.src = "/mermaid.min.js"; s.onload = () => ok(window.mermaid); s.onerror = () => { mermaidLib = null; fail(new Error("Mermaid did not load")); }; document.head.append(s); });
}
function drawDiagram(text, look) {
  const key = look.key + "\n" + text;
  if (drawnDiagrams.has(key)) return drawnDiagrams.get(key);   // the result once drawn, the promise until then
  const work = diagramQueue.then(async () => {
    const mermaid = await loadMermaid();
    mermaid.initialize({ startOnLoad: false, securityLevel: "strict", suppressErrorRendering: true, theme: "base", themeVariables: look.variables, fontFamily: String(look.variables.fontFamily) });
    try { await mermaid.parse(text); } catch (e) { const l = String(e?.message || e || "").split(/\r?\n/u).map(v => v.trim()).find(Boolean)?.replace(/:$/u, "") || "Mermaid could not read it"; return { refused: l.length > 160 ? l.slice(0, 159) + "…" : l }; }
    return { svg: (await mermaid.render(`mobile-diagram-${++diagramSeq}`, text)).svg };
  }).catch(e => ({ refused: e?.message || "Mermaid could not draw it" }));
  diagramQueue = work; drawnDiagrams.set(key, work); work.then(r => { if (drawnDiagrams.has(key)) drawnDiagrams.set(key, r); });
  if (drawnDiagrams.size > 48) drawnDiagrams.delete(drawnDiagrams.keys().next().value);
  return work;
}
function diagram(text) {
  const pre = el("pre", null, text);
  if (text.length > MAX_DIAGRAM || !text.trim()) return pre;
  const box = el("div", "md-diagram-wrap"); box.append(pre);
  const show = r => {
    if (r.svg) {
      const fig = el("div", "markdown-diagram"); fig.innerHTML = r.svg; fig.tabIndex = 0; fig.setAttribute("role", "button"); fig.setAttribute("aria-label", "Diagram; tap to enlarge");
      fig.addEventListener("click", () => zoomDiagram(r.svg));
      // Open in a new tab (regular browser tabs only; installed, the full-screen view is the way): a real link (no
      // window.open after an await, which iOS blocks), ready once the page is made.
      const open = el("a", "diagram-open"); open.target = "_blank"; open.rel = "noopener"; open.setAttribute("aria-label", "Open diagram in a new tab");
      open.append(svg_("external"), el("span", null, "Open")); open.hidden = true;
      if (!installedApp()) diagramPage(r).then(url => { if (url) { open.href = url; open.hidden = false; } });
      box.replaceChildren(fig, open);
    } else if (r.refused) box.append(el("div", "markdown-diagram-refused", "Not drawn: " + r.refused));
  };
  const drawn = drawDiagram(text, diagramLook());
  if (drawn.then) drawn.then(show); else show(drawn);
  return box;
}
// Full screen: the diagram at its own size, which the browser pinches and pans (touch-action: pinch-zoom pan-x pan-y).
function zoomDiagram(svg) {
  const v = el("div", "diagram-zoom"); v.setAttribute("role", "dialog"); v.setAttribute("aria-label", "Diagram");
  const close = el("button", "icon-btn diagram-zoom-close"); close.type = "button"; close.setAttribute("aria-label", "Close"); close.append(svg_("close"));
  const stage = el("div", "diagram-zoom-stage"); stage.innerHTML = svg;
  const s = stage.querySelector("svg"); if (s) { const w = parseFloat(s.style.maxWidth) || s.viewBox?.baseVal?.width || 0; s.removeAttribute("style"); if (w) s.style.width = Math.max(w, innerWidth - 24) + "px"; }
  // Top left, as the header of a session: a back arrow and the app's icon, closing it as the close button does.
  const back = el("button", "diagram-zoom-back"); back.type = "button"; back.setAttribute("aria-label", "Back");
  const icon = document.createElement("img"); icon.src = "/icon-192.png"; icon.alt = ""; icon.width = icon.height = 28;
  back.append(svg_("back"), icon);
  v.append(back, close, stage); document.body.append(v);
  // Back closes it: an entry of its own in the history, taken off again when it closes by its button.
  history.pushState({ diagramZoom: true }, "");
  const gone = () => { v.remove(); removeEventListener("popstate", gone); };
  addEventListener("popstate", gone);
  back.onclick = close.onclick = () => { if (history.state?.diagramZoom) history.back(); else gone(); };
}
// The page of a drawn diagram (MobileServer /api/diagrams), made once per drawing and theme; null if it failed.
const diagramPages = new Map();
function diagramPage(r) {
  if (diagramPages.has(r.svg)) return diagramPages.get(r.svg);
  const look = diagramLook().variables;
  const w = parseFloat(/max-width:\s*([\d.]+)px/u.exec(r.svg)?.[1]) || 0;
  const svg = w ? r.svg.replace(/<svg\b/u, `<svg data-w="${w}" style="--w:${w}px"`).replace(/(<svg[^>]*?)\sstyle="max-width:[^"]*"/u, "$1") : r.svg;
  const made = fetch("/api/diagrams", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ svg, background: look.background, text: look.textColor, dark: look.darkMode }) })
    .then(x => x.ok ? x.json() : null).then(x => x?.url ? new URL(x.url, location.href).href : null).catch(() => null);
  diagramPages.set(r.svg, made); if (diagramPages.size > 48) diagramPages.delete(diagramPages.keys().next().value);
  return made;
}
function installedApp() { return ["standalone", "minimal-ui", "fullscreen", "window-controls-overlay"].some(m => matchMedia(`(display-mode: ${m})`).matches) || !!navigator.standalone; }
function svg_(name) { return svg(name); }
// A theme switch draws the diagrams of the open session again in the new colors.
new MutationObserver(() => { if (detail.id && typeof renderDetail === "function") renderDetail(); }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
