import { isLocalMachine, localKey, machineOf, newRequestId, qualifyPage, qualifySession, sessionBase } from "./machines";
import type { ActionResult, ApprovalAnswer, ApprovalPage, ApprovalResult, AskAnswerRequest, AskPage, AskResult, EventPage, JobOutput, JobPage, LatestEventPage, LinkedFilePage, MachinePage, NewSessionRequest, NewSessionResult, Options, ProjectList, PromptAttachment, PromptMode, PromptSelection, QueuePage, Session, SessionChoices, SessionPage, TakeOverResult, ToolCallDetail, ToolOutputPage, ToolOutputPlace, UnqueueResult, UnsteerResult, WaitingPage, WaitingRun } from "./contract";

/** What the page says when the server no longer knows this phone. */
export const signedOutMessage = "This phone is no longer connected: scan the QR code of the Mobile tab again.";

/**
 * What the page says when the server does not know this phone and it never kept a key: it was opened without the token of
 * the QR link, as after the sign-in a network asks for first, when it lost the token of the address.
 */
export const notConnectedMessage = "This phone is not connected yet: scan the QR code of the Mobile tab. If the phone asked you to sign in first, scan it again now.";

/** The key of the kept key of this phone: an installed app does not share the browser's cookies, so it signs in again with it. */
export const tokenKey = "alta.token";

/** The part of `Storage` the page uses; reads and writes never throw (a private window may refuse them). */
export type KeyValueStore = Readonly<{ get(key: string): string | null; set(key: string, value: string): void; remove(key: string): void }>;

/** A store over `localStorage` that never throws. */
export function browserStore(storage: () => Storage): KeyValueStore {
  return {
    get: key => { try { return storage().getItem(key); } catch { return null; } },
    set: (key, value) => { try { storage().setItem(key, value); } catch { /* full or refused: the value is not kept */ } },
    remove: key => { try { storage().removeItem(key); } catch { /* refused: nothing to remove */ } },
  };
}

/** A store in memory, for the tests. */
export function memoryStore(values: Record<string, string> = {}): KeyValueStore & { values: Record<string, string> } {
  return { values, get: key => values[key] ?? null, set: (key, value) => { values[key] = value; }, remove: key => { delete values[key]; } };
}

/**
 * Takes the token of the QR link out of the address: `#k=<token>`, or `?k=<token>` behind a network whose sign-in loses
 * the fragment (a Dev Tunnel), comes once and leaves the address (and so the history) at once; `signIn` keeps it for the
 * installed app. `leave` is given what stays of the address after its path. Returns the token, or null when the address
 * has none.
 */
export function takeAddressToken(address: Readonly<{ search: string; hash: string }>, leave: (rest: string) => void): string | null {
  const inHash = /^#k=([^&]*)/u.exec(address.hash);
  const query = new URLSearchParams(address.search);
  const inQuery = query.get("k");
  if (!inHash && inQuery === null) return null;
  query.delete("k");
  const others = query.toString();
  leave((others ? `?${others}` : "") + (inHash ? "" : address.hash));
  if (!inHash) return inQuery || null;
  try { return decodeURIComponent(inHash[1]) || null; } catch { return null; }
}

/** The server answered with an error: its status and what it said. */
export class ApiError extends Error {
  /** The HTTP status. */
  readonly status: number;
  /** What else the server said of the failure: its `code` and the facts the code goes with (`machineId`, `sessionId`...), when it said any. */
  readonly code: string | null;
  readonly facts: Readonly<Record<string, unknown>>;
  constructor(status: number, message: string, facts: Readonly<Record<string, unknown>> = {}) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.facts = facts;
    this.code = typeof facts.code === "string" ? facts.code : null;
  }

  /**
   * Where the session lives, when this machine says that the session is not on it (409 `not_on_this_machine`): the machine, and the id
   * of the session there. A session the computer gave away, or the link of a machine that never had it.
   */
  get movedTo(): Readonly<{ machineId: string | null; sessionId: string | null }> | null {
    if (this.status !== 409 || this.code !== "not_on_this_machine") return null;
    const { machineId, sessionId } = this.facts;
    return { machineId: typeof machineId === "string" ? machineId : null, sessionId: typeof sessionId === "string" ? sessionId : null };
  }
}

/** The server does not know this phone: it kept no token, or the kept token did not sign it in again. */
export class SignedOutError extends Error {
  constructor(message = signedOutMessage) { super(message); this.name = "SignedOutError"; }
}

/** What the page tells of a failed call: its message, or what to check when the computer was not reached. */
export function failureText(error: unknown, host: string): string {
  if (error instanceof TypeError) return `the connection to ${host} failed. Check the network of this phone, then try again.`;
  return error instanceof Error ? error.message : String(error);
}

type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/** The header that carries the id of a command. */
export const requestIdHeader = "X-Alta-Request-Id";

/**
 * The one way the page calls the server. A 401 (the cookie expired, or a home-screen app that never had it) signs in
 * again once with the kept token and repeats the call; when that fails too, the token is forgotten, `onSignedOut` is
 * called with what to tell the person and the call ends with a `SignedOutError`. A phone that kept no token was never
 * connected here, which it is told instead. Any other status that is not a success ends with an `ApiError` carrying the
 * server's `error`; an empty body is an empty answer, never a JSON error.
 */
export function createApi(options: Readonly<{ fetch: FetchLike; store: KeyValueStore; onSignedOut: (message: string) => void; onSignedIn?: () => void }>) {
  const { store } = options;
  // The id of this computer in the fleet, once the page has read the machines: the sessions of other machines name it by that id.
  let localDevice: string | null = null;
  let signingIn: Promise<boolean> | null = null;

  const request = (method: string, path: string, body?: unknown, requestId?: string) => options.fetch(path, {
    method, credentials: "same-origin", cache: "no-store",
    headers: { Accept: "application/json", ...(body === undefined ? {} : { "Content-Type": "application/json" }), ...(requestId ? { [requestIdHeader]: requestId } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  async function read<T>(response: Response): Promise<T> {
    const text = await response.text();
    let data: unknown = null;
    if (text.trim()) {
      try { data = JSON.parse(text); }
      catch { if (response.ok) throw new ApiError(response.status, "The computer sent an answer the page cannot read."); }
    }
    if (!response.ok) {
      const said = data && typeof data === "object" && typeof (data as { error?: unknown }).error === "string" ? (data as { error: string }).error : null;
      throw new ApiError(response.status, said || `HTTP ${response.status}`, data && typeof data === "object" ? data as Record<string, unknown> : {});
    }
    return (data ?? {}) as T;
  }

  /**
   * Signs in with the pairing token of the QR code, or the key this phone kept: true when the server set its cookie, false
   * when it refused. The server gives the phone a key of its own, which is kept in place of the token to sign in again.
   */
  async function login(token: string): Promise<boolean> {
    const response = await request("POST", "/api/login", { token });
    if (response.status === 401 || response.status === 403) return false;
    const answer = await read<{ key?: unknown }>(response);
    if (typeof answer.key === "string" && answer.key) store.set(tokenKey, answer.key);
    options.onSignedIn?.();
    return true;
  }

  function signOut(message = signedOutMessage): never {
    store.remove(tokenKey);
    options.onSignedOut(message);
    throw new SignedOutError(message);
  }

  // Calls that meet a 401 together share one sign-in.
  function signInAgain(token: string): Promise<boolean> {
    return signingIn ??= login(token).finally(() => { signingIn = null; });
  }

  // A command (a POST) carries an id of its own in a header, made once: the request that is made again after a sign-in is the same one,
  // so that the computer, or the machine it sends it to, takes it once. A new session and its first prompt go in one request, under one id.
  async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const requestId = method === "POST" ? newRequestId() : undefined;
    let response = await request(method, path, body, requestId);
    if (response.status === 401) {
      const token = store.get(tokenKey);
      if (!token) signOut(notConnectedMessage);
      if (!await signInAgain(token)) signOut();
      response = await request(method, path, body, requestId);
      if (response.status === 401) signOut();
    }
    return read<T>(response);
  }

  // A command whose answer says it failed is a failure, whatever its status.
  async function command<T extends { error?: string | null }>(path: string, body?: unknown): Promise<T> {
    const result = await call<T>("POST", path, body ?? {});
    if (result.error) throw new ApiError(200, result.error);
    return result;
  }

  // The routes of a session: those of this computer for a session of its own, those of its machine for another one.
  const session = (key: string) => sessionBase(localKey(key, localDevice));
  // The routes of a machine: this computer's own for `local` and for none (what an older CodeAlta, which has no machines, answers).
  const machineRoute = (machine: string | null | undefined, local: string, remote: string) => isLocalMachine(machine) ? local : `/api/machines/${encodeURIComponent(machine!)}${remote}`;
  return {
    login,
    pushKey: () => call<{ publicKey: string }>("GET", "/api/push"),
    subscribePush: (subscription: PushSubscriptionJSON) => call<void>("POST", "/api/push/subscription", subscription),
    unsubscribePush: () => call<void>("POST", "/api/push/unsubscribe"),
    /** Signs in with the token of the QR link: true when it is accepted (and kept), false when refused (and forgotten). */
    async signIn(token: string): Promise<boolean> {
      store.set(tokenKey, token);
      if (await login(token)) return true;
      store.remove(tokenKey);
      return false;
    },
    /** Tells the page the id of this computer in the fleet (`deviceId` of its machine), so that the sessions that name it by it are its own. */
    setLocalDevice(deviceId: string | null | undefined) { localDevice = deviceId || null; },
    /** The machines the computer reaches, itself first; an older CodeAlta has none of this and answers 404. */
    machines: () => call<MachinePage>("GET", "/api/machines"),
    /**
     * A page of the list: of this computer when `machine` is null (an older CodeAlta, or a computer that reaches none), else `all`,
     * `local` or a machine. The sessions come named by their machines (`machines.ts`).
     */
    sessions: async (offset: number, limit: number, projects: readonly string[], machine: string | null = null) => {
      const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
      for (const project of projects) query.append("project", project);
      if (machine !== null) query.set("machine", machine);
      const page = await call<SessionPage>("GET", `/api/sessions?${query}`);
      // A list of one machine does not say it on each session: the page does.
      const single = machine !== null && machine !== "all" && !isLocalMachine(machine);
      return qualifyPage(single ? { ...page, sessions: (page.sessions ?? []).map(s => ({ ...s, machineId: s.machineId ?? machine })), parents: page.parents?.map(s => ({ ...s, machineId: s.machineId ?? machine })) } : page, localDevice);
    },
    session: async (id: string) => {
      const read = await call<Session>("GET", session(id));
      return qualifySession(isLocalMachine(machineOf(id)) ? read : { ...read, machineId: read.machineId ?? machineOf(id) }, localDevice);
    },
    events: (id: string, since: number) => call<EventPage>("GET", `${session(id)}/events?since=${since}`),
    /** A page of the timeline from its end: the latest events, or those before a sequence number. */
    latestEvents: (id: string, before?: number) => call<LatestEventPage>("GET", `${session(id)}/events/latest${before === undefined ? "" : `?before=${before}`}`),
    jobs: (id: string) => call<JobPage>("GET", `${session(id)}/jobs`),
    job: (id: string, jobId: string) => call<JobOutput>("GET", `${session(id)}/jobs/${encodeURIComponent(jobId)}`),
    stopJob: (id: string, jobId: string) => command<ActionResult>(`${session(id)}/jobs/${encodeURIComponent(jobId)}/stop`),
    projects: (machine?: string | null) => call<ProjectList>("GET", machineRoute(machine, "/api/projects", "/projects")),
    options: (machine?: string | null) => call<Options>("GET", machineRoute(machine, "/api/options", "/options")),
    /** Starts a session on this computer, or on the machine named. */
    create: async (request: NewSessionRequest, machine?: string | null) => {
      const result = await call<NewSessionResult>("POST", machineRoute(machine, "/api/sessions", "/sessions"), request);
      if (!result.id) throw new ApiError(200, result.error || "The session was not created.");
      return result;
    },
    choices: (id: string) => call<SessionChoices>("GET", `${session(id)}/choices`),
    /** Moves an idle session to another provider of its space, as the provider chooser of the desktop does. */
    selectProvider: (id: string, provider: string) => command<ActionResult>(`${session(id)}/provider`, { provider }),
    /** Sends a prompt; a send or a queue carries what was chosen of the session (a steer takes nothing). Files go with a send. */
    prompt: (id: string, text: string, mode: PromptMode, selection: PromptSelection = {}, attachments: readonly PromptAttachment[] = []) =>
      command<ActionResult>(`${session(id)}/prompt`, { text, mode, ...(attachments.length ? { attachments } : {}), ...(mode === "steer" ? {} : selection) }),
    /** The prompts that wait in the queue of the session. */
    queue: (id: string) => call<QueuePage>("GET", `${session(id)}/queue`),
    /** Removes a prompt from the queue, unless the session is taking it. */
    unqueue: (id: string, itemId: string) => command<UnqueueResult>(`${session(id)}/queue/${encodeURIComponent(itemId)}/remove`),
    /** Takes back a steered prompt, unless the run gave it to the model. */
    unsteer: (id: string, withdrawalId: string) => command<UnsteerResult>(`${session(id)}/steer/${encodeURIComponent(withdrawalId)}/withdraw`),
    /** One tool call, whole; `since` is the sequence number before its first event. */
    tool: (id: string, activityId: string, since?: number) => call<ToolCallDetail>("GET", `${session(id)}/tools/${encodeURIComponent(activityId)}${since === undefined ? "" : `?since=${since}`}`),
    /** A page of the output of a tool call. */
    toolOutput: (id: string, activityId: string, since: number | undefined, place: ToolOutputPlace) => {
      const query = new URLSearchParams();
      if (since !== undefined) query.set("since", String(since));
      if (place.offset !== undefined) query.set("offset", String(place.offset));
      if (place.before !== undefined) query.set("before", String(place.before));
      const search = query.toString();
      return call<ToolOutputPage>("GET", `${session(id)}/tools/${encodeURIComponent(activityId)}/output${search ? `?${search}` : ""}`);
    },
    /** A page of a file that the answer `seq` links: the server finds the link in that answer. */
    linkedFile: (id: string, seq: number, link: string, place: ToolOutputPlace) => {
      const query = new URLSearchParams({ seq: String(seq), link });
      if (place.offset !== undefined) query.set("offset", String(place.offset));
      if (place.before !== undefined) query.set("before", String(place.before));
      return call<LinkedFilePage>("GET", `${session(id)}/file?${query}`);
    },
    /** A data file that a chart of the message `seq` names, below the folder of the session: the server finds the chart in that message. */
    chartData: async (id: string, seq: number, path: string): Promise<{ status: string; text?: string }> => {
      const reply = await call<{ status?: string; text?: string | null }>("GET", `${session(id)}/chart-data?${new URLSearchParams({ seq: String(seq), path })}`);
      return { status: reply.status ?? "failed", text: reply.text ?? undefined };
    },
    abort: (id: string) => command<ActionResult>(`${session(id)}/abort`),
    archiveChildren: (id: string) => call<{ hasChildren?: boolean; error?: string | null }>("GET", `${session(id)}/archive-children`),
    archive: (id: string, withChildren = false) => command<ActionResult & { changed?: readonly string[] | null }>(`${session(id)}/archive${withChildren ? "?withChildren=true" : ""}`),
    restore: (id: string) => command<ActionResult>(`${session(id)}/restore`),
    /** The ask the session waits on; an answer that did not reach it says why in its result, not as an error. */
    ask: (id: string) => call<AskPage>("GET", `${session(id)}/ask`),
    answerAsk: (id: string, request: AskAnswerRequest) => call<AskResult>("POST", `${session(id)}/ask/answer`, request),
    cancelAsk: (id: string, request: Pick<AskAnswerRequest, "actionId" | "askId" | "revision">) => call<AskResult>("POST", `${session(id)}/ask/cancel`, request),
    /** What the session asks permission for; an answer that did not reach it says why in its result, not as an error. */
    permissions: (id: string) => call<ApprovalPage>("GET", `${session(id)}/permissions`),
    resolvePermission: (id: string, answer: ApprovalAnswer) => call<ApprovalResult>("POST", `${session(id)}/permissions/resolve`, answer),
    /** Takes over the run of the automation that started the session, while it waits for the person. */
    takeOver: (id: string) => call<TakeOverResult>("POST", `${session(id)}/takeover`, {}),
    /** The runs that wait for the person, of this computer and of the machines of their servers. */
    waiting: () => call<WaitingPage>("GET", "/api/automations/waiting"),
    /** Takes a run that waits over; one of a machine of a server is signed by the computer for the person. */
    takeOverRun: (run: WaitingRun) => call<TakeOverResult>("POST", "/api/automations/takeover",
      run.machineId ? { runId: run.runId, machineId: run.machineId, automationId: run.automationId } : { runId: run.runId }),
    diagram: (body: Readonly<{ svg: string; background?: string; text?: string; dark?: boolean }>) => call<{ url?: string }>("POST", "/api/diagrams", body),
  };
}

/** The calls of the page. */
export type Api = ReturnType<typeof createApi>;
