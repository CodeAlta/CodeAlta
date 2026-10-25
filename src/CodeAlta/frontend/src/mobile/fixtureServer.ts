import { readFile } from "node:fs/promises";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import type { Live, Options, PromptSelection, Session, SessionChoices, Space, TimelineEvent } from "./contract";

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
    token: "secret",
    cookies: new Set<string>(),
    sessions: [] as Session[],
    events: new Map<string, TimelineEvent[]>(),
    /** The choices of the sessions; a session without is on the Codex ones (`fixtureChoices`). */
    choices: new Map<string, SessionChoices>(),
    options: null as Options | null,
    projects: [{ id: "p1", path: "C:/code/parser", name: "parser", space: fixtureSpaces[0] }, { id: "p2", path: "C:/code/site", name: "site", space: fixtureSpaces[1] }],
    /** The spaces the phone sees (empty: every space). */
    spaces: [...fixtureSpaces] as Space[],
    created: 0,
    /** What the next `POST /api/sessions` answers besides its id. */
    createAnswer: {} as Record<string, unknown>,
  };
  const requests: FixtureRequest[] = [];
  const streams = new Set<ServerResponse>();
  let cookieSeq = 0;

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
      if ((body as { token?: string } | undefined)?.token !== state.token) return send(response, 401);
      const cookie = `c${++cookieSeq}`;
      state.cookies.add(cookie);
      return send(response, 204, undefined, { "Set-Cookie": `alta_mobile=${cookie}; Path=/; HttpOnly; SameSite=Strict` });
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
      const page = all.slice(offset, offset + limit);
      return send(response, 200, { sessions: page, next: offset + page.length, hasMore: offset + page.length < all.length, projects, machine: "FIXTURE-PC", spaces: state.spaces, error: null });
    }
    if (path === "/api/sessions" && request.method === "POST") {
      const id = `new${++state.created}`;
      const { project, text } = body as { project: string; text: string };
      state.sessions.push(session(id, `New: ${text}`, project, 0));
      return send(response, 200, { id, ...state.createAnswer });
    }
    if (path === "/api/projects") return send(response, 200, { projects: state.projects, default: "p1", error: null });
    if (path === "/api/options") return send(response, 200, state.options);
    const found = /^\/api\/sessions\/([^/]+)(?:\/(events|prompt|abort|archive|restore|choices))?$/.exec(path);
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
      if (action === "choices") return send(response, 200, choicesOf(target));
      if (action === "prompt") {
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
    { id: "gpt-6.1-argon", name: "GPT-6.1 Argon", efforts: ["medium", "high", "xhigh"], startEffort: "high" },
    { id: "gpt-5.5", name: "GPT-5.5", efforts: ["low", "medium", "high"], startEffort: "medium" },
  ],
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
