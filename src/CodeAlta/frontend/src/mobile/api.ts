import type { ActionResult, EventPage, NewSessionRequest, NewSessionResult, Options, ProjectList, PromptMode, Session, SessionPage } from "./contract";

/** What the page says when the server no longer knows this phone. */
export const signedOutMessage = "This phone is no longer connected: scan the QR code of the Mobile tab again.";

/** The key of the kept token: an installed app does not share the browser's cookies, so it signs in again with it. */
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
 * Takes the token of the QR link out of the address: `#k=<token>` comes once and leaves the address (and so the
 * history) at once; `signIn` keeps it for the installed app. Returns the token, or null when the address has none.
 */
export function takeFragmentToken(hash: string, clearAddress: () => void): string | null {
  const found = /^#k=([^&]*)/u.exec(hash);
  if (!found) return null;
  clearAddress();
  try { return decodeURIComponent(found[1]) || null; } catch { return null; }
}

/** The server answered with an error: its status and what it said. */
export class ApiError extends Error {
  /** The HTTP status. */
  readonly status: number;
  constructor(status: number, message: string) { super(message); this.name = "ApiError"; this.status = status; }
}

/** The server does not know this phone any more, and the kept token did not sign it in again. */
export class SignedOutError extends Error {
  constructor() { super(signedOutMessage); this.name = "SignedOutError"; }
}

/** What the page tells of a failed call: its message, or what to check when the computer was not reached. */
export function failureText(error: unknown, host: string): string {
  if (error instanceof TypeError) return `the connection to ${host} failed. Check that this phone is on Tailscale, then try again.`;
  return error instanceof Error ? error.message : String(error);
}

type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * The one way the page calls the server. A 401 (the cookie expired, or a home-screen app that never had it) signs in
 * again once with the kept token and repeats the call; when that fails too, the token is forgotten, `onSignedOut` is
 * called and the call ends with a `SignedOutError`. Any other status that is not a success ends with an `ApiError`
 * carrying the server's `error`; an empty body is an empty answer, never a JSON error.
 */
export function createApi(options: Readonly<{ fetch: FetchLike; store: KeyValueStore; onSignedOut: () => void; onSignedIn?: () => void }>) {
  const { store } = options;
  let signingIn: Promise<boolean> | null = null;

  const request = (method: string, path: string, body?: unknown) => options.fetch(path, {
    method, credentials: "same-origin", cache: "no-store",
    headers: body === undefined ? { Accept: "application/json" } : { Accept: "application/json", "Content-Type": "application/json" },
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
      throw new ApiError(response.status, said || `HTTP ${response.status}`);
    }
    return (data ?? {}) as T;
  }

  /** Signs in with a token: true when the server set its cookie, false when it refused the token. */
  async function login(token: string): Promise<boolean> {
    const response = await request("POST", "/api/login", { token });
    if (response.status === 401 || response.status === 403) return false;
    await read<unknown>(response);
    options.onSignedIn?.();
    return true;
  }

  function signOut(): never {
    store.remove(tokenKey);
    options.onSignedOut();
    throw new SignedOutError();
  }

  // Calls that meet a 401 together share one sign-in.
  function signInAgain(token: string): Promise<boolean> {
    return signingIn ??= login(token).finally(() => { signingIn = null; });
  }

  async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
    let response = await request(method, path, body);
    if (response.status === 401) {
      const token = store.get(tokenKey);
      if (!token || !await signInAgain(token)) signOut();
      response = await request(method, path, body);
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

  const session = (id: string) => `/api/sessions/${encodeURIComponent(id)}`;
  return {
    login,
    /** Signs in with the token of the QR link: true when it is accepted (and kept), false when refused (and forgotten). */
    async signIn(token: string): Promise<boolean> {
      store.set(tokenKey, token);
      if (await login(token)) return true;
      store.remove(tokenKey);
      return false;
    },
    sessions: (offset: number, limit: number, project: string) => {
      const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
      if (project) query.set("project", project);
      return call<SessionPage>("GET", `/api/sessions?${query}`);
    },
    session: (id: string) => call<Session>("GET", session(id)),
    events: (id: string, since: number) => call<EventPage>("GET", `${session(id)}/events?since=${since}`),
    projects: () => call<ProjectList>("GET", "/api/projects"),
    options: () => call<Options>("GET", "/api/options"),
    create: async (request: NewSessionRequest) => {
      const result = await call<NewSessionResult>("POST", "/api/sessions", request);
      if (!result.id) throw new ApiError(200, result.error || "The session was not created.");
      return result;
    },
    prompt: (id: string, text: string, mode: PromptMode) => command<ActionResult>(`${session(id)}/prompt`, { text, mode }),
    abort: (id: string) => command<ActionResult>(`${session(id)}/abort`),
    archive: (id: string) => command<ActionResult>(`${session(id)}/archive`),
    restore: (id: string) => command<ActionResult>(`${session(id)}/restore`),
    diagram: (body: Readonly<{ svg: string; background?: string; text?: string; dark?: boolean }>) => call<{ url?: string }>("POST", "/api/diagrams", body),
  };
}

/** The calls of the page. */
export type Api = ReturnType<typeof createApi>;
