import assert from "node:assert/strict";
import test from "node:test";
import { ApiError, createApi, failureText, memoryStore, notConnectedMessage, SignedOutError, signedOutMessage, takeAddressToken, tokenKey, requestIdHeader } from "./api";

type Call = { url: string; method: string; body: unknown; requestId?: string };

// A server the test plays: each answer is a status and a body, given in order for each path.
function server(routes: Record<string, Array<[number, string?]>>) {
  const calls: Call[] = [];
  const fetch = async (url: string, init?: RequestInit) => {
    const requestId = (init?.headers as Record<string, string> | undefined)?.[requestIdHeader];
    calls.push({ url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined, ...(requestId ? { requestId } : {}) });
    const path = url.split("?")[0];
    const answer = routes[path]?.shift() ?? [404, JSON.stringify({ error: `no route ${path}` })];
    return new Response(answer[1] ?? null, { status: answer[0] });
  };
  return { fetch, calls };
}

test("the token of the QR link is taken from the fragment and the address is cleared", () => {
  const left: string[] = [];
  assert.equal(takeAddressToken({ search: "", hash: "#k=abc%2Bdef" }, rest => left.push(rest)), "abc+def");
  assert.deepEqual(left, [""]);
  assert.equal(takeAddressToken({ search: "?a=1", hash: "#k=abc" }, rest => left.push(rest)), "abc");
  assert.deepEqual(left, ["", "?a=1"], "what the query holds stays");
  assert.equal(takeAddressToken({ search: "", hash: "#/session/1" }, rest => left.push(rest)), null);
  assert.equal(left.length, 2, "an address without a token is left as it is");
  assert.equal(takeAddressToken({ search: "", hash: "#k=" }, rest => left.push(rest)), null);
  assert.equal(left.length, 3, "an empty token still leaves the address");
});

test("the token of the QR link is taken from the query, where the sign-in of a tunnel keeps it", () => {
  // A Dev Tunnel sends the phone to the sign-in of GitHub first, which loses the fragment of the address and keeps its query.
  const left: string[] = [];
  assert.equal(takeAddressToken({ search: "?k=abc-def_1", hash: "" }, rest => left.push(rest)), "abc-def_1");
  assert.equal(takeAddressToken({ search: "?a=1&k=abc%2Bdef&b=2", hash: "#/session/s1" }, rest => left.push(rest)), "abc+def");
  assert.deepEqual(left, ["", "?a=1&b=2#/session/s1"], "the token leaves the address; what else it holds stays");
  assert.equal(takeAddressToken({ search: "?a=1", hash: "" }, rest => left.push(rest)), null);
  assert.equal(left.length, 2, "an address without a token is left as it is");
  assert.equal(takeAddressToken({ search: "?k=", hash: "" }, rest => left.push(rest)), null);
  assert.deepEqual(left.slice(2), [""], "an empty token still leaves the address");
});

test("signIn keeps the key the computer gives in place of the token, and forgets a refused one", async () => {
  const store = memoryStore();
  const { fetch, calls } = server({ "/api/login": [[200, JSON.stringify({ key: "phone-key" })], [204], [401]] });
  const api = createApi({ fetch, store, onSignedOut: () => assert.fail("signIn reports itself") });
  assert.equal(await api.signIn("good"), true);
  assert.equal(store.values[tokenKey], "phone-key", "the pairing token is not kept");
  assert.equal(await api.signIn("good"), true);
  assert.equal(store.values[tokenKey], "good", "a computer that gives no key: the token is kept");
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
  const said: string[] = [];
  const first = server({ "/api/options": [[401]] });
  await assert.rejects(createApi({ fetch: first.fetch, store: memoryStore(), onSignedOut: message => said.push(message) }).options(), SignedOutError);
  assert.equal(first.calls.length, 1);
  const second = server({ "/api/options": [[401], [401]], "/api/login": [[204]] });
  await assert.rejects(createApi({ fetch: second.fetch, store: memoryStore({ [tokenKey]: "t" }), onSignedOut: message => said.push(message) }).options(), SignedOutError);
  assert.deepEqual(said, [notConnectedMessage, signedOutMessage]);
});

test("a phone that never kept a key is told to scan the code, not that it is no longer connected", async () => {
  // The sign-in a network asks for first can lose the token of the QR link: the page opens with neither a token nor a key.
  const { fetch } = server({ "/api/sessions": [[401]] });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  await assert.rejects(api.sessions(0, 6, []), (error: Error) => error instanceof SignedOutError && error.message === notConnectedMessage);
  assert.match(notConnectedMessage, /scan the QR code/);
  assert.match(notConnectedMessage, /again/, "the person who just signed in to the tunnel scans it again");
  assert.doesNotMatch(notConnectedMessage, /no longer/);
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
  await api.latestEvents("a/b").catch(() => { });
  await api.latestEvents("a/b", 42).catch(() => { });
  assert.deepEqual(calls.map(c => c.url), ["/api/sessions/a%2Fb/prompt", "/api/sessions?offset=6&limit=12&project=p+1&project=p2", "/api/sessions/a%2Fb/events?since=42",
    "/api/sessions/a%2Fb/events/latest", "/api/sessions/a%2Fb/events/latest?before=42"]);
  assert.deepEqual(calls[0].body, { text: "x", mode: "steer" });
});

test("a failure is told with the server's words, or with what to check when the computer was not reached", () => {
  assert.equal(failureText(new ApiError(400, "Bad."), "pc.ts.net"), "Bad.");
  assert.match(failureText(new TypeError("Failed to fetch"), "pc.ts.net"), /connection to pc\.ts\.net failed.*network of this phone/);
});

test("a session of another machine is read and driven through the routes of its machine, and a machine of the list is asked for its own projects and options", async () => {
  const { fetch, calls } = server({});
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  const remote = "@m 2/a/b";
  await api.session(remote).catch(() => { });
  await api.events(remote, 3).catch(() => { });
  await api.latestEvents(remote, 9).catch(() => { });
  await api.choices(remote).catch(() => { });
  await api.tool(remote, "t1").catch(() => { });
  await api.jobs(remote).catch(() => { });
  await api.queue(remote).catch(() => { });
  await api.ask(remote).catch(() => { });
  await api.permissions(remote).catch(() => { });
  await api.projects("m 2").catch(() => { });
  await api.options("m 2").catch(() => { });
  await api.projects("local").catch(() => { });
  await api.options(null).catch(() => { });
  await api.machines().catch(() => { });
  const base = "/api/machines/m%202/sessions/a%2Fb";
  assert.deepEqual(calls.map(c => c.url), [base, `${base}/events?since=3`, `${base}/events/latest?before=9`, `${base}/choices`, `${base}/tools/t1`, `${base}/jobs`, `${base}/queue`,
    `${base}/ask`, `${base}/permissions`, "/api/machines/m%202/projects", "/api/machines/m%202/options", "/api/projects", "/api/options", "/api/machines"]);
});

test("the list asks for the machines it shows, and names the sessions of the others by their machines", async () => {
  const sessionOf = (id: string, extra: object = {}) => ({ id, title: id, projectId: "p", project: "p", summary: null, lastActivity: null, live: null, ...extra });
  const page = (sessions: object[], parents?: object[]) => JSON.stringify({ sessions, ...(parents ? { parents } : {}), next: sessions.length, hasMore: false, projects: [], machine: "PC", error: null });
  const { fetch, calls } = server({ "/api/sessions": [
    [200, page([sessionOf("a"), sessionOf("a", { machineId: "m2", machine: "Mac mini" }), sessionOf("c", { machineId: "m2", parentId: "a" })], [sessionOf("p", { machineId: "m2" })])],
    [200, page([sessionOf("x")])], [200, page([sessionOf("y", { machineId: "m2" })])], [200, page([sessionOf("z")])],
  ] });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  const all = await api.sessions(0, 6, [], "all");
  assert.deepEqual(all.sessions.map(s => [s.id, s.parentId ?? null]), [["a", null], ["@m2/a", null], ["@m2/c", "@m2/a"]], "the same id on two machines is two sessions");
  assert.equal(all.parents![0].id, "@m2/p");
  // One machine is asked for by its id: its sessions are of that machine though they do not say so.
  assert.equal((await api.sessions(0, 6, ["p1"], "m2")).sessions[0].id, "@m2/x");
  assert.equal((await api.sessions(0, 6, [], "m2")).sessions[0].id, "@m2/y");
  assert.equal((await api.sessions(0, 6, [], "local")).sessions[0].id, "z");
  assert.deepEqual(calls.map(c => c.url), ["/api/sessions?offset=0&limit=6&machine=all", "/api/sessions?offset=0&limit=6&project=p1&machine=m2",
    "/api/sessions?offset=0&limit=6&machine=m2", "/api/sessions?offset=0&limit=6&machine=local"]);
  // Without a machine the list is this computer's own, as an older CodeAlta answers it.
  const older = server({ "/api/sessions": [[200, page([sessionOf("q")])]] });
  await createApi({ fetch: older.fetch, store: memoryStore(), onSignedOut: () => { } }).sessions(0, 6, []);
  assert.equal(older.calls[0].url, "/api/sessions?offset=0&limit=6");
});

test("a session read alone is of the machine of its route, and the id of this computer in the fleet is this computer", async () => {
  const body = JSON.stringify({ id: "s1", title: "t", projectId: null, project: null, summary: null, lastActivity: null, live: null });
  const { fetch, calls } = server({ "/api/machines/m2/sessions/s1": [[200, body]], "/api/sessions/s1": [[200, body]], "/api/sessions/s2/events": [[200, JSON.stringify({ since: 0, events: [], error: null })]] });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  api.setLocalDevice("device-local");
  assert.equal((await api.session("@m2/s1")).id, "@m2/s1");
  assert.equal((await api.session("@device-local/s1")).id, "s1");
  await api.events("@device-local/s2", 0);
  assert.deepEqual(calls.map(c => c.url), ["/api/machines/m2/sessions/s1", "/api/sessions/s1", "/api/sessions/s2/events?since=0"], "this computer is asked on its own routes");
});

test("every command carries an id of its own in a header, made once and sent again with the request that is retried", async () => {
  const { fetch, calls } = server({
    "/api/machines/m2/sessions/s1/prompt": [[401], [200, JSON.stringify({ ok: true })], [200, JSON.stringify({ ok: true })]],
    "/api/machines/m2/sessions": [[200, JSON.stringify({ id: "n1", machineId: "m2" })]],
    "/api/machines/m2/sessions/s1/abort": [[200, JSON.stringify({ ok: true })]],
    "/api/machines/m2/sessions/s1/ask/answer": [[200, JSON.stringify({ ok: true, status: "admitted", error: null })]],
    "/api/sessions/s1/prompt": [[200, JSON.stringify({ ok: true })]],
    "/api/login": [[200, JSON.stringify({ key: "k" })]],
  });
  const api = createApi({ fetch, store: memoryStore({ [tokenKey]: "t" }), onSignedOut: () => assert.fail("still signed in") });
  await api.prompt("@m2/s1", "hello", "send", { model: "gpt" });
  await api.prompt("@m2/s1", "again", "send");
  await api.create({ project: "p", text: "go" }, "m2");
  await api.abort("@m2/s1");
  await api.answerAsk("@m2/s1", { actionId: "act", askId: "a", revision: "1", answers: [] });
  await api.prompt("s1", "local", "send");
  const prompts = calls.filter(c => c.url === "/api/machines/m2/sessions/s1/prompt");
  assert.equal(prompts.length, 3, "the first send met a 401, signed in again and was sent again");
  const first = prompts[0].requestId!;
  assert.match(first, /^[0-9a-f-]{36}$/);
  assert.deepEqual(prompts[0], prompts[1], "the retry is the same request, with the same id");
  assert.deepEqual(prompts[1].body, { text: "hello", mode: "send", model: "gpt" }, "the id is in the header, not in what is sent");
  assert.notEqual(prompts[2].requestId, first, "another prompt is another request");
  // A new session and its first prompt are one request, under one id.
  for (const url of ["/api/machines/m2/sessions", "/api/machines/m2/sessions/s1/abort", "/api/machines/m2/sessions/s1/ask/answer", "/api/sessions/s1/prompt"])
    assert.match(calls.find(c => c.url === url)!.requestId!, /^[0-9a-f-]{36}$/, url);
  assert.deepEqual(calls.find(c => c.url === "/api/machines/m2/sessions")!.body, { project: "p", text: "go" });
  assert.deepEqual(calls.find(c => c.url === "/api/sessions/s1/prompt")!.body, { text: "local", mode: "send" }, "a command of this computer has its id too");
  assert.equal(new Set(calls.filter(c => c.url !== "/api/login").map(c => c.requestId)).size, 6, "an id per command");
  assert.equal(calls.find(c => c.url === "/api/login")!.requestId, undefined, "the sign-in is not a command");
  // A read carries none.
  const reads = server({ "/api/machines/m2/sessions/s1/events": [[200, JSON.stringify({ since: 0, events: [], error: null })]] });
  await createApi({ fetch: reads.fetch, store: memoryStore(), onSignedOut: () => { } }).events("@m2/s1", 0);
  assert.equal(reads.calls[0].body, undefined);
  assert.equal(reads.calls[0].requestId, undefined);
});

test("a session that a machine does not have says where it is: the 409 of not_on_this_machine", async () => {
  const moved = { error: "Not on this machine.", code: "not_on_this_machine", machineId: "m3", sessionId: "s9" };
  const { fetch } = server({ "/api/sessions/s1": [[409, JSON.stringify(moved)]], "/api/sessions/s2": [[409, JSON.stringify({ error: "Busy." })]], "/api/machines/m2/sessions/s1": [[409, JSON.stringify({ ...moved, machineId: "local" })]] });
  const api = createApi({ fetch, store: memoryStore(), onSignedOut: () => { } });
  const error = await api.session("s1").catch((e: unknown) => e) as ApiError;
  assert.ok(error instanceof ApiError);
  assert.deepEqual([error.status, error.code, error.movedTo], [409, "not_on_this_machine", { machineId: "m3", sessionId: "s9" }]);
  assert.equal(error.message, "Not on this machine.");
  const busy = await api.session("s2").catch((e: unknown) => e) as ApiError;
  assert.deepEqual([busy.code, busy.movedTo], [null, null], "any other 409 is no move");
  assert.equal(((await api.session("@m2/s1").catch((e: unknown) => e)) as ApiError).movedTo?.machineId, "local");
});
