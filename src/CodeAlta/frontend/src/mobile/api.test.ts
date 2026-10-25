import assert from "node:assert/strict";
import test from "node:test";
import { ApiError, createApi, failureText, memoryStore, SignedOutError, signedOutMessage, takeFragmentToken, tokenKey } from "./api";

type Call = { url: string; method: string; body: unknown };

// A server the test plays: each answer is a status and a body, given in order for each path.
function server(routes: Record<string, Array<[number, string?]>>) {
  const calls: Call[] = [];
  const fetch = async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const path = url.split("?")[0];
    const answer = routes[path]?.shift() ?? [404, JSON.stringify({ error: `no route ${path}` })];
    return new Response(answer[1] ?? null, { status: answer[0] });
  };
  return { fetch, calls };
}

test("the token of the QR link is taken from the fragment and the address is cleared", () => {
  let cleared = 0;
  assert.equal(takeFragmentToken("#k=abc%2Bdef", () => cleared++), "abc+def");
  assert.equal(cleared, 1);
  assert.equal(takeFragmentToken("#/session/1", () => cleared++), null);
  assert.equal(cleared, 1, "an address without a token is left as it is");
  assert.equal(takeFragmentToken("#k=", () => cleared++), null);
  assert.equal(cleared, 2, "an empty token still leaves the address");
});

test("signIn keeps an accepted token and forgets a refused one", async () => {
  const store = memoryStore();
  const { fetch, calls } = server({ "/api/login": [[204], [401]] });
  const api = createApi({ fetch, store, onSignedOut: () => assert.fail("signIn reports itself") });
  assert.equal(await api.signIn("good"), true);
  assert.equal(store.values[tokenKey], "good");
  assert.deepEqual(calls[0], { url: "/api/login", method: "POST", body: { token: "good" } });
  assert.equal(await api.signIn("bad"), false);
  assert.equal(store.values[tokenKey], undefined);
});

test("a 401 signs in again once with the kept token and repeats the call", async () => {
  const store = memoryStore({ [tokenKey]: "kept" });
  const page = JSON.stringify({ sessions: [], next: 0, hasMore: false, projects: [], machine: "PC", error: null });
  const { fetch, calls } = server({ "/api/sessions": [[401], [200, page]], "/api/login": [[204]] });
  let signedOut = 0;
  const api = createApi({ fetch, store, onSignedOut: () => signedOut++ });
  const read = await api.sessions(0, 6, []);
  assert.equal(read.machine, "PC");
  assert.deepEqual(calls.map(c => `${c.method} ${c.url}`), ["GET /api/sessions?offset=0&limit=6", "POST /api/login", "GET /api/sessions?offset=0&limit=6"]);
  assert.equal(signedOut, 0);
});

test("a refused sign-in forgets the token, reports the phone signed out and fails the call", async () => {
  const store = memoryStore({ [tokenKey]: "reset" });
  const { fetch, calls } = server({ "/api/projects": [[401]], "/api/login": [[401]] });
  let signedOut = 0;
  const api = createApi({ fetch, store, onSignedOut: () => signedOut++ });
  await assert.rejects(api.projects(), (error: Error) => error instanceof SignedOutError && error.message === signedOutMessage);
  assert.equal(signedOut, 1);
  assert.equal(store.values[tokenKey], undefined);
  assert.equal(calls.length, 2, "one sign-in, no second try of the call");
});

test("a 401 without a kept token is signed out at once; a second 401 after a sign-in too", async () => {
  let signedOut = 0;
  const first = server({ "/api/options": [[401]] });
  await assert.rejects(createApi({ fetch: first.fetch, store: memoryStore(), onSignedOut: () => signedOut++ }).options(), SignedOutError);
  assert.equal(first.calls.length, 1);
  const second = server({ "/api/options": [[401], [401]], "/api/login": [[204]] });
  await assert.rejects(createApi({ fetch: second.fetch, store: memoryStore({ [tokenKey]: "t" }), onSignedOut: () => signedOut++ }).options(), SignedOutError);
  assert.equal(signedOut, 2);
});

test("calls that meet a 401 together share one sign-in", async () => {
  const ok = JSON.stringify({ projects: [], default: null, error: null });
  const { fetch, calls } = server({ "/api/projects": [[401], [401], [200, ok], [200, ok]], "/api/login": [[204], [204]] });
  const api = createApi({ fetch, store: memoryStore({ [tokenKey]: "t" }), onSignedOut: () => assert.fail("still signed in") });
  await Promise.all([api.projects(), api.projects()]);
  assert.equal(calls.filter(c => c.url === "/api/login").length, 1);
});

test("an empty body is an empty answer, an error status carries the server's error, an unreadable success fails", async () => {
  const { fetch } = server({
    "/api/sessions/a/archive": [[200, ""]],
    "/api/sessions/b/archive": [[409, JSON.stringify({ error: "The session runs." })]],
    "/api/sessions/c/archive": [[500, "<html>oops</html>"]],
    "/api/sessions/d/archive": [[200, "<html>not json</html>"]],
    "/api/sessions/e/archive": [[200, JSON.stringify({ ok: false, error: "Refused." })]],
  });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  assert.deepEqual(await api.archive("a"), {});
  await assert.rejects(api.archive("b"), (error: Error) => error instanceof ApiError && error.status === 409 && error.message === "The session runs.");
  await assert.rejects(api.archive("c"), (error: Error) => error instanceof ApiError && error.message === "HTTP 500");
  await assert.rejects(api.archive("d"), (error: Error) => error instanceof ApiError && /cannot read/.test(error.message));
  await assert.rejects(api.archive("e"), (error: Error) => error.message === "Refused.", "a command whose answer says it failed is a failure");
});

test("a new session needs an id, and its send error is an answer, not a failure", async () => {
  const { fetch, calls } = server({ "/api/sessions": [[200, JSON.stringify({ id: "s1", sendError: "busy" })], [200, JSON.stringify({ error: "No such project." })]] });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  assert.deepEqual(await api.create({ project: "p", text: "hi", tier: "standard" }), { id: "s1", sendError: "busy" });
  assert.deepEqual(calls[0].body, { project: "p", text: "hi", tier: "standard" });
  await assert.rejects(api.create({ project: "p", text: "hi" }), /No such project/);
});

test("the paths of a session are encoded, and the project filter is a query of each project", async () => {
  const { fetch, calls } = server({});
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  await api.prompt("a/b", "x", "steer").catch(() => { });
  await api.sessions(6, 12, ["p 1", "p2"]).catch(() => { });
  await api.events("a/b", 42).catch(() => { });
  assert.deepEqual(calls.map(c => c.url), ["/api/sessions/a%2Fb/prompt", "/api/sessions?offset=6&limit=12&project=p+1&project=p2", "/api/sessions/a%2Fb/events?since=42"]);
  assert.deepEqual(calls[0].body, { text: "x", mode: "steer" });
});

test("a failure is told with the server's words, or with what to check when the computer was not reached", () => {
  assert.equal(failureText(new ApiError(400, "Bad."), "pc.ts.net"), "Bad.");
  assert.match(failureText(new TypeError("Failed to fetch"), "pc.ts.net"), /connection to pc\.ts\.net failed.*Tailscale/);
});
