import { readFile } from "node:fs/promises";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import type { WaitingRun } from "./contract";
import type { ApprovalAutomation, ApprovalRequest, ApprovalResult, Ask, AskResult, BackgroundJob, Live, Options, PromptSelection, QueuedPrompt, Session, SessionChoices, Space, TimelineEvent, ToolCallDetail, UnqueueResult } from "./contract";

/** The policy the plugin's server sets on the page (the contract of the phone page). */
export const mobilePolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

/** A request the fixture received. */
export type FixtureRequest = Readonly<{ method: string; path: string; query: string; body: unknown }>;

/**
 * A fake of the Mobile plugin's server for the browser test of the phone page: the files of the page and the HTTP API
 * of the contract over sessions held in memory, with a cookie set by `/api/login` and the event stream of `/api/events`.
 */
export async function startFixtureServer(files: Readonly<Record<string, Readonly<{ type: string; body: string | Buffer }>>>) {
  const state = {
    /** The pairing token of the QR code. */
    token: "secret",
    /** The keys of the phones signed in, which sign them in again; the pairing token gives a new one. */
    keys: new Set<string>(),
    cookies: new Set<string>(),
    sessions: [] as Session[],
    events: new Map<string, TimelineEvent[]>(),
    jobs: new Map<string, BackgroundJob[]>(),
    jobOutput: new Map<string, string>(),
    /** The choices of the sessions; a session without is on the Codex ones (`fixtureChoices`). */
    choices: new Map<string, SessionChoices>(),
    options: null as Options | null,
    projects: [{ id: "p1", path: "C:/code/parser", name: "parser", space: fixtureSpaces[0] }, { id: "p2", path: "C:/code/site", name: "site", space: fixtureSpaces[1] }],
    /** The spaces the phone sees (empty: every space). */
    spaces: [...fixtureSpaces] as Space[],
    created: 0,
    /** What the next `POST /api/sessions` answers besides its id. */
    createAnswer: {} as Record<string, unknown>,
    /** The next prompt fails with this text, once. */
    promptError: null as string | null,
    /** The asks the sessions wait on: an answer or a cancel that goes ends it. */
    asks: new Map<string, Ask>(),
    /** What the next answer or cancel of an ask becomes; null: it goes. */
    askResult: null as AskResult | null,
    /** What becomes of the ask when the next answer or cancel is refused or still goes; null leaves it as it is. */
    askAfter: null as Ask | null,
    /** The permission requests the sessions wait on: an answer that goes ends the one it names. */
    approvals: new Map<string, ApprovalRequest[]>(),
    /** What the next answer to a permission request becomes; null: it goes. */
    approvalResult: null as ApprovalResult | null,
    /** The run of the automation that started each session, as the page of its requests says it. */
    automations: new Map<string, ApprovalAutomation>(),
    /** The runs that wait for the person, which the home page lists. */
    waiting: [] as WaitingRun[],
    /** What the next take-over answers; null: it is taken over. */
    takeOverResult: null as Readonly<{ ok: boolean; error: string | null }> | null,
    /** The messages a page of the latest events holds, as the plugin pages them; null: a page holds them all. */
    messagesPerPage: null as number | null,
    /** The reads of older events fail. */
    olderFails: false,
    /** The reads of older events of these sessions wait until their promise ends. */
    olderHeld: new Map<string, Promise<void>>(),
    /** The prompts that wait in the queues of the sessions. */
    queues: new Map<string, QueuedPrompt[]>(),
    /** What the next removal from a queue answers; null: the prompt is removed. */
    unqueueStatus: null as UnqueueResult["status"] | null,
    /** The steered prompts the runs still hold, by their ids: taking one back works once. */
    steers: new Set<string>(),
    /** The tool calls of the sessions, by `<session>/<activity id>`. */
    tools: new Map<string, ToolCallDetail>(),
    /** The whole outputs of the tool calls, read a page at a time, by `<session>/<activity id>`. */
    toolOutputs: new Map<string, string>(),
    /** The files the answers link, by `<session>/<seq>/<target of the link>`; the path the server finds is `C:\work\` and the target. */
    linkedFiles: new Map<string, string>(),
    /**
     * The images of the sessions, by `<session>/<image id>` for a saved copy and `<session>/linked/<seq>/<position>` for a
     * picture of the disk an answer shows; any other image is not found.
     */
    images: new Map<string, Readonly<{ type: string; body: Buffer }>>(),
  };
  const requests: FixtureRequest[] = [];
  const streams = new Set<ServerResponse>();
  let cookieSeq = 0, keySeq = 0, takeBackSeq = 0;

  const send = (response: ServerResponse, status: number, body?: unknown, headers: Record<string, string> = {}) => {
    response.writeHead(status, { "Content-Security-Policy": mobilePolicy, "Cache-Control": "no-store", ...(body === undefined ? {} : { "Content-Type": "application/json" }), ...headers });
    response.end(body === undefined ? undefined : JSON.stringify(body));
  };
  const signedIn = (request: IncomingMessage) => {
    const cookie = /(?:^|;\s*)alta_mobile=([^;]+)/.exec(request.headers.cookie ?? "")?.[1];
    return !!cookie && state.cookies.has(cookie);
  };
  const readBody = async (request: IncomingMessage) => {
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(chunk as Buffer);
    const text = Buffer.concat(chunks).toString("utf8");
    return text ? JSON.parse(text) as unknown : undefined;
  };
  const sessionOf = (id: string) => state.sessions.find(s => s.id === id);
  // The choices of a session: those the test set, else the Codex ones, on what the session says it runs.
  const choicesOf = (session: Session): SessionChoices => state.choices.get(session.id) ?? {
    current: {
      provider: session.model?.provider ?? "codex", providerName: session.model?.providerName ?? "Codex", model: session.model?.id ?? null, modelName: session.model?.name ?? null,
      reasoning: session.model?.reasoning ?? null, agent: session.agent?.id ?? "default", agentName: session.agent?.name ?? "Default", permissionMode: null,
    },
    ...fixtureChoices,
  };

  async function handle(request: IncomingMessage, response: ServerResponse) {
    const url = new URL(request.url ?? "/", "http://fixture");
    const path = url.pathname;
    const body = request.method === "POST" ? await readBody(request) : undefined;
    requests.push({ method: request.method ?? "GET", path, query: url.search, body });
    if (!path.startsWith("/api/")) {
      const file = files[path === "/" ? "/index.html" : path];
      if (!file) return send(response, 404);
      response.writeHead(200, { "Content-Type": file.type, "Content-Security-Policy": mobilePolicy });
      return response.end(file.body);
    }
    if (path === "/api/login" && request.method === "POST") {
      const given = (body as { token?: string } | undefined)?.token ?? "";
      const key = state.keys.has(given) ? given : given === state.token ? `key${++keySeq}` : null;
      if (!key) return send(response, 401);
      state.keys.add(key);
      const cookie = `c${++cookieSeq}`;
      state.cookies.add(cookie);
      return send(response, 200, { key }, { "Set-Cookie": `alta_mobile=${cookie}; Path=/; HttpOnly; SameSite=Strict` });
    }
    if (!signedIn(request)) return send(response, 401);
    if (path === "/api/events") {
      response.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-store", "Content-Security-Policy": mobilePolicy });
      response.write(": hello\n\n");
      for (const session of state.sessions) if (session.live) response.write(`event: session\ndata: ${JSON.stringify(session.live)}\n\n`);
      streams.add(response);
      request.on("close", () => streams.delete(response));
      return;
    }
    if (path === "/api/sessions" && request.method === "GET") {
      const offset = Number(url.searchParams.get("offset") ?? 0), limit = Math.min(50, Number(url.searchParams.get("limit") ?? 6));
      const projects = url.searchParams.getAll("project");
      const all = state.sessions.filter(s => projects.length === 0 || projects.includes(s.projectId ?? "")).sort((a, b) => Date.parse(b.lastActivity!) - Date.parse(a.lastActivity!));
      // As the plugin: the parents of the sub-agents of the page that no page gave yet come with it, and a sub-agent names its parent.
      const named = (s: Session): Session => s.parentId ? { ...s, parentTitle: sessionOf(s.parentId)?.title ?? s.parentTitle ?? null } : s;
      const page = all.slice(offset, offset + limit).map(named);
      const held = new Set(all.slice(0, offset + limit).map(s => s.id)), parents: Session[] = [];
      for (let children = page; children.length;) {
        const next: Session[] = [];
        for (const parent of children.map(s => all.find(p => p.id === s.parentId))) {
          if (!parent || held.has(parent.id)) continue;
          held.add(parent.id);
          next.push(parent);
          parents.push(named(parent));
        }
        children = next;
      }
      return send(response, 200, { sessions: page, parents, next: offset + page.length, hasMore: offset + page.length < all.length, projects, machine: "FIXTURE-PC", spaces: state.spaces, error: null });
    }
    if (path === "/api/sessions" && request.method === "POST") {
      const id = `new${++state.created}`;
      const { project, text } = body as { project: string; text: string };
      state.sessions.push(session(id, `New: ${text}`, project, 0));
      return send(response, 200, { id, ...state.createAnswer });
    }
    const image = /^\/api\/sessions\/([^/]+)\/(images\/[^/]+|linked-images\/\d+\/[^/]+)$/.exec(path);
    if (image) {
      const key = `${decodeURIComponent(image[1])}/${image[2].startsWith("images/") ? decodeURIComponent(image[2].slice("images/".length)) : `linked/${image[2].slice("linked-images/".length)}`}`;
      const found = state.images.get(key);
      if (!found) return send(response, 404);
      response.writeHead(200, { "Content-Type": found.type, "Cache-Control": "private, max-age=86400, immutable", "Content-Security-Policy": "default-src 'none'; sandbox" });
      return response.end(found.body);
    }
    if (path === "/api/projects") return send(response, 200, { projects: state.projects, default: "p1", error: null });
    if (path === "/api/automations/waiting") return send(response, 200, { runs: state.waiting, error: null });
    if (path === "/api/automations/takeover") {
      const result = state.takeOverResult ?? { ok: true, error: null };
      state.takeOverResult = null;
      if (result.ok) state.waiting = state.waiting.filter(run => run.runId !== (body as { runId: string }).runId);
      return send(response, 200, result);
    }
    if (path === "/api/options") return send(response, 200, state.options);
    const queuePath = /^\/api\/sessions\/([^/]+)\/queue(?:\/([^/]+)\/remove)?$/.exec(path);
    if (queuePath) {
      const id = decodeURIComponent(queuePath[1]);
      if (!sessionOf(id)) return send(response, 404);
      if (!queuePath[2]) return send(response, 200, { items: state.queues.get(id) ?? [], error: null });
      const status = state.unqueueStatus;
      state.unqueueStatus = null;
      if (status) return send(response, 200, { status, error: null });
      const item = decodeURIComponent(queuePath[2]), held = state.queues.get(id) ?? [];
      state.queues.set(id, held.filter(prompt => prompt.id !== item));
      return send(response, 200, { status: held.some(prompt => prompt.id === item) ? "removed" : "not_found", error: null });
    }
    const steerPath = /^\/api\/sessions\/([^/]+)\/steer\/([^/]+)\/withdraw$/.exec(path);
    if (steerPath) return send(response, 200, { withdrawn: state.steers.delete(decodeURIComponent(steerPath[2])), error: null });
    const filePath = /^\/api\/sessions\/([^/]+)\/file$/.exec(path);
    if (filePath) {
      const link = url.searchParams.get("link") ?? "", whole = state.linkedFiles.get(`${decodeURIComponent(filePath[1])}/${url.searchParams.get("seq")}/${link}`);
      if (whole === undefined) return send(response, 400, { error: "This file is not there." });
      const page = 16_000, offset = url.searchParams.get("offset"), before = url.searchParams.get("before");
      const place = offset === null && before === null ? /#L(\d+)/u.exec(link) : null, lines = whole.split("\n");
      const markStart = place ? lines.slice(0, Number(place[1]) - 1).reduce((sum, line) => sum + line.length + 1, 0) : undefined;
      const markEnd = place ? markStart! + lines[Number(place[1]) - 1].length + 1 : undefined;
      const start = before !== null ? Math.max(0, Number(before) - page) : offset !== null ? Number(offset) : place ? Math.max(0, markStart! - page / 2) : 0;
      const end = before !== null ? Number(before) : Math.min(whole.length, start + page);
      return send(response, 200, { path: `C:\\work\\${link.split("#")[0].replaceAll("/", "\\")}`, output: whole.slice(start, end), outputOffset: start, outputLength: whole.length, markStart, markEnd, error: null });
    }
    const outputPath = /^\/api\/sessions\/([^/]+)\/tools\/([^/]+)\/output$/.exec(path);
    if (outputPath) {
      const key = `${decodeURIComponent(outputPath[1])}/${decodeURIComponent(outputPath[2])}`, whole = state.toolOutputs.get(key);
      if (whole === undefined) return send(response, 200, { error: "The output was not read." });
      const page = 16_000, offset = url.searchParams.get("offset"), before = url.searchParams.get("before");
      const start = before !== null ? Math.max(0, Number(before) - page) : offset !== null ? Number(offset) : state.tools.get(key)?.command ? Math.max(0, whole.length - page) : 0;
      const end = before !== null ? Number(before) : Math.min(whole.length, start + page);
      return send(response, 200, { output: whole.slice(start, end), outputOffset: start, outputLength: whole.length, error: null });
    }
    const toolPath = /^\/api\/sessions\/([^/]+)\/tools\/([^/]+)$/.exec(path);
    if (toolPath) {
      const call = state.tools.get(`${decodeURIComponent(toolPath[1])}/${decodeURIComponent(toolPath[2])}`);
      return send(response, 200, call ?? { error: "The tool call was not read." });
    }
    const jobPath = /^\/api\/sessions\/([^/]+)\/jobs(?:\/([^/]+)(\/stop)?)?$/.exec(path);
    if (jobPath) {
      const id = decodeURIComponent(jobPath[1]), jobId = jobPath[2] && decodeURIComponent(jobPath[2]);
      if (!sessionOf(id)) return send(response, 404);
      const jobs = state.jobs.get(id) ?? [];
      if (!jobId) return send(response, 200, { jobs, error: null });
      const job = jobs.find(job => job.id === jobId);
      if (!job) return send(response, 404);
      if (jobPath[3] && request.method === "POST") {
        state.jobs.set(id, jobs.map(item => item.id === jobId ? { ...item, state: "cancelled", endedAt: new Date().toISOString() } : item));
        return send(response, 200, { ok: true, error: null });
      }
      return send(response, 200, { job, text: state.jobOutput.get(jobId) ?? "", truncated: false, error: null });
    }
    const found = /^\/api\/sessions\/([^/]+)(?:\/(events\/latest|events|prompt|abort|archive|restore|choices|ask|ask\/answer|ask\/cancel|permissions|permissions\/resolve|takeover))?$/.exec(path);
    if (found) {
      const id = decodeURIComponent(found[1]), action = found[2];
      // An archived session is out of the list, and can still be restored.
      if (action === "restore") {
        const back = archived.get(id);
        if (!back) return send(response, 404, { error: "No such session." });
        archived.delete(id);
        state.sessions.push(back);
        return send(response, 200, { ok: true });
      }
      const target = sessionOf(id);
      if (!target) return send(response, 404, { error: "No such session." });
      if (!action) return send(response, 200, target);
      if (action === "events") {
        const since = Number(url.searchParams.get("since") ?? 0);
        const events = (state.events.get(id) ?? []).filter(e => e.seq > since);
        return send(response, 200, { since: Math.max(since, ...events.map(e => e.seq)), events, error: null });
      }
      if (action === "events/latest") {
        const before = url.searchParams.get("before");
        if (before !== null && state.olderFails) return send(response, 500, { error: "The journal cannot be read." });
        if (before !== null) await state.olderHeld.get(id);
        const events = (state.events.get(id) ?? []).filter(e => before === null || e.seq < Number(before));
        // As the plugin: back to the messages of a page, then forward to the first prompt.
        let start = 0;
        if (state.messagesPerPage !== null) {
          let messages = 0;
          for (let index = events.length - 1; index >= 0; index--) {
            if ((events[index].kind === "user.message" || events[index].kind === "assistant.message") && ++messages >= state.messagesPerPage) { start = index; break; }
          }
          const prompt = events.findIndex((e, index) => index >= start && e.kind === "user.message");
          if (prompt > 0) start = prompt;
        }
        const page = events.slice(start);
        return send(response, 200, { since: page.at(-1)?.seq ?? 0, before: page[0]?.seq ?? null, hasMore: start > 0, events: page, error: null });
      }
      if (action === "choices") return send(response, 200, choicesOf(target));
      if (action === "permissions") return send(response, 200, { requests: state.approvals.get(id) ?? [], hasMore: false, error: null, automation: state.automations.get(id) ?? null });
      if (action === "takeover") {
        const result = state.takeOverResult ?? { ok: true, error: null };
        state.takeOverResult = null;
        const run = state.automations.get(id);
        if (result.ok && run) state.automations.set(id, { ...run, waiting: false });
        return send(response, 200, result);
      }
      if (action === "permissions/resolve") {
        const result = state.approvalResult ?? { ok: true, status: "resolved", error: null };
        state.approvalResult = null;
        const { requestId } = body as { requestId: string };
        if (result.ok) state.approvals.set(id, (state.approvals.get(id) ?? []).filter(request => request.id !== requestId));
        return send(response, 200, result);
      }
      if (action === "ask") return send(response, 200, { ask: state.asks.get(id) ?? null, error: null });
      if (action === "ask/answer" || action === "ask/cancel") {
        const result = state.askResult ?? { ok: true, status: action === "ask/answer" ? "admitted" : "cancelled", error: null };
        state.askResult = null;
        const { askId } = body as { askId: string };
        if (state.askAfter) state.asks.set(id, state.askAfter);
        else if (result.ok && !result.pending && state.asks.get(id)?.id === askId) state.asks.delete(id);
        state.askAfter = null;
        return send(response, 200, result);
      }
      if (action === "prompt") {
        if (state.promptError) {
          const error = state.promptError;
          state.promptError = null;
          return send(response, 200, { ok: false, error });
        }
        // A send or a queue that carries a selection: the session runs on it now, as alta would say.
        const { mode, model, reasoning, agent, permissionMode } = body as { mode: string } & PromptSelection;
        if (mode !== "steer" && (model || reasoning || agent || permissionMode)) {
          const choices = choicesOf(target), current = { ...choices.current };
          if (model) Object.assign(current, { model, modelName: choices.models.find(m => m.id === model)?.name ?? model, reasoning: reasoning ?? null });
          else if (reasoning) current.reasoning = reasoning;
          if (agent) Object.assign(current, { agent, agentName: choices.prompts.find(p => p.id === agent)?.name ?? agent });
          if (permissionMode) current.permissionMode = permissionMode === "provider" ? null : permissionMode;
          state.choices.set(id, { ...choices, current });
          Object.assign(target, { model: { ...target.model!, id: current.model, name: current.modelName, reasoning: current.reasoning }, agent: { id: current.agent!, name: current.agentName } });
        }
        // What takes the prompt back, as the plugin answers it.
        if (mode === "queue") {
          const item: QueuedPrompt = { id: `queue-${++takeBackSeq}`, text: (body as { text: string }).text, at: new Date().toISOString(), state: "queued" };
          state.queues.set(id, [...state.queues.get(id) ?? [], item]);
          return send(response, 200, { ok: true, mode, queueItemId: item.id });
        }
        if (mode === "steer") {
          const withdrawalId = `steer-${++takeBackSeq}`;
          state.steers.add(withdrawalId);
          return send(response, 200, { ok: true, mode, withdrawalId });
        }
        return send(response, 200, { ok: true, mode });
      }
      if (action === "archive") { state.sessions = state.sessions.filter(s => s.id !== id); archived.set(id, target); return send(response, 200, { ok: true }); }
      return send(response, 200, { ok: true });
    }
    return send(response, 404, { error: "No such route." });
  }
  const archived = new Map<string, Session>();

  const server = createServer((request, response) => {
    handle(request, response).catch(error => { if (!response.headersSent) send(response, 500, { error: String(error) }); });
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;

  return {
    origin: `http://127.0.0.1:${port}`,
    state,
    requests,
    /** Tells the open event streams of a live update (and keeps it on the session). */
    push(live: Live) {
      const target = sessionOf(live.id);
      if (target) Object.assign(target, { live, lastActivity: live.at ?? target.lastActivity });
      for (const stream of streams) stream.write(`event: session\ndata: ${JSON.stringify(live)}\n\n`);
    },
    /** Ends the event streams, as a computer that went to sleep. */
    dropStreams() { for (const stream of streams) stream.end(); streams.clear(); },
    get streamCount() { return streams.size; },
    close: () => new Promise<void>(resolve => { for (const stream of streams) stream.destroy(); server.closeAllConnections?.(); server.close(() => resolve()); }),
  };
}

/** A session of the fixture. */
export function session(id: string, title: string, projectId: string, minutesAgo: number, live: Partial<Live> = {}, extra: Partial<Session> = {}): Session {
  const at = new Date(Date.now() - minutesAgo * 60_000).toISOString();
  return {
    id, title, projectId, project: projectId === "p1" ? "parser" : "site", space: projectId === "p1" ? fixtureSpaces[0] : fixtureSpaces[1],
    model: { provider: "codex", providerName: "Codex", id: "gpt-6.1-sol", name: "GPT-6.1 Sol", reasoning: "low" }, agent: { id: "default", name: "Default" },
    worktree: null, parentId: null, summary: `Summary of ${title}`, lastActivity: at,
    live: { id, projectId, live: "idle", kind: "assistant", line: `Last answer of ${title}`, at, activity: null, canSteer: false, ...live },
    ...extra,
  };
}

/** What the composer of a Codex session offers in the fixture. */
export const fixtureChoices: Omit<SessionChoices, "current"> = {
  models: [
    { id: "gpt-6.1-sol", name: "GPT-6.1 Sol", efforts: ["low", "medium", "high"], startEffort: "medium" },
    { id: "gpt-6.1-argon", name: "GPT-6.1 Argon", efforts: ["medium", "high", "xhigh"], startEffort: "high", imageInput: true },
    { id: "gpt-5.5", name: "GPT-5.5", efforts: ["low", "medium", "high"], startEffort: "medium" },
  ],
  // A provider whose settings list its models by name: the latest of each family first.
  modelsOrder: "name_version",
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }, { id: "review", name: "Review" }],
  permissionModes: [{ id: "default", name: "Ask first" }, { id: "acceptEdits", name: "Accept edits" }, { id: "bypassPermissions", name: "Bypass permissions" }],
  error: null,
};

/** The spaces of the fixture: the projects p1 and p2 are in one each. */
export const fixtureSpaces: readonly Space[] = [
  { id: "work", name: "Work", color: "#2d72d2", icon: null },
  { id: "home", name: "Home", color: "#d1980b", icon: null },
];

/** The files of the page, read from the sources: `mobile.html` with the bundle in place of its module, and `public/mobile`. */
export async function pageFiles(script: string, style: string) {
  const html = (await readFile(new URL("../../mobile.html", import.meta.url), "utf8"))
    .replace(`<script type="module" src="/src/mobile/main.ts"></script>`, `<script type="module" src="/app.js"></script><link rel="stylesheet" href="/app.css">`);
  const asset = async (name: string) => readFile(new URL(`../../public/mobile/${name}`, import.meta.url));
  return {
    "/index.html": { type: "text/html; charset=utf-8", body: html },
    "/app.js": { type: "text/javascript", body: script },
    "/app.css": { type: "text/css", body: style },
    "/mobile/boot.js": { type: "text/javascript", body: await asset("boot.js") },
    "/mobile/icon-192.png": { type: "image/png", body: await asset("icon-192.png") },
    "/mobile/apple-touch-icon.png": { type: "image/png", body: await asset("apple-touch-icon.png") },
    "/mobile/manifest.webmanifest": { type: "application/manifest+json", body: await asset("manifest.webmanifest") },
    "/sw.js": { type: "text/javascript", body: await asset("sw.js") },
  };
}