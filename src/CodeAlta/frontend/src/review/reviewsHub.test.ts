import assert from "node:assert/strict";
import test from "node:test";
import type { ReviewThreadDto, ReviewsListRequest } from "#neoastra";
import { checkoutCounts, createReviewsHub, reviewKey, type ReviewsApi } from "./reviewsHub";

const settled = () => new Promise<void>(resolve => setImmediate(resolve));
const noTimers = { set: () => 0, clear: () => { } };
const dto = (id: string, path: string, more: Partial<ReviewThreadDto> = {}): ReviewThreadDto => ({ id, target: "file", path, side: "new", startLine: 1, endLine: 1, anchor: "a",
  body: "b", suggestion: null, state: "draft", outdated: false, replies: [], round: null, createdAt: "", updatedAt: "", ...more });

/** A host with one store of threads per checkout, that records what it is asked and tells when told to. */
function host() {
  const threads = new Map<string, ReviewThreadDto[]>();
  const asked: string[] = [];
  let tell: (() => void) | null = null;
  let next = 1;
  const store = (checkout: string | null) => { const key = checkout ?? ""; if (!threads.has(key)) threads.set(key, []); return threads.get(key)!; };
  const api = {
    async list(request: ReviewsListRequest) {
      asked.push(`list:${request.checkout ?? "-"}:${request.target}:${request.path ?? "-"}:${request.includeResolved}`);
      if (request.expectedEpoch !== "epoch") return { status: "stale_epoch", threads: [], rounds: [], viewed: [], request: null, readOnly: null, message: null };
      return { status: "ok", threads: store(request.checkout).filter(thread => thread.target === request.target && (request.path === null || thread.path === request.path)),
        rounds: [], viewed: [], request: null, readOnly: null, message: null };
    },
    async add(request) {
      asked.push(`add:${request.path}:${request.startLine}-${request.endLine}`);
      const thread = dto(`c${next++}`, request.path, { startLine: request.startLine, endLine: request.endLine, body: request.body ?? "", suggestion: request.suggestion });
      store(request.checkout).push(thread);
      return { status: "ok", thread, message: null };
    },
    async update(request) {
      const list = store(request.checkout), index = list.findIndex(thread => thread.id === request.id);
      if (index < 0) return { status: "not_found", thread: null, message: null };
      list[index] = { ...list[index], body: request.body ?? list[index].body, suggestion: request.clearSuggestion ? null : request.suggestion ?? list[index].suggestion };
      return { status: "ok", thread: list[index], message: null };
    },
    async delete(request) {
      const list = store(request.checkout), index = list.findIndex(thread => thread.id === request.id);
      if (index >= 0) list.splice(index, 1);
      return { status: index >= 0 ? "ok" : "not_found", message: null };
    },
    async resolve(request) {
      const list = store(request.checkout), index = list.findIndex(thread => thread.id === request.id);
      list[index] = { ...list[index], state: request.resolved ? "resolved" : "open" };
      return { status: "ok", thread: list[index], message: null };
    },
    async resolveAddressed() { return { status: "ok", message: null }; },
    async reply() { return { status: "ok", thread: null, message: null }; },
    async viewed() { return { status: "ok", message: null }; },
    async submit(request) {
      asked.push(`submit:${request.verdict}:${request.sessionId ?? "new"}:${request.newSession}:${request.overall ?? "-"}`);
      return { status: "ok", sessionId: request.sessionId ?? "fresh", roundId: "r1", message: null };
    },
    async counts(request) {
      asked.push(`counts:${request.projectId}`);
      return { status: "ok", checkouts: [...threads].map(([checkout, list]) => ({ checkout: checkout || null, drafts: list.filter(thread => thread.state === "draft").length,
        open: 0, addressed: 0, missing: false })) };
    },
    async watch(_request, options) {
      return (async function* () {
        yield { revision: 0 };
        while (!options?.signal?.aborted) {
          await new Promise<void>(resolve => { tell = resolve; options?.signal?.addEventListener("abort", () => resolve(), { once: true }); });
          if (!options?.signal?.aborted) yield { revision: 1 };
        }
      })();
    },
  } as ReviewsApi;
  return { api, asked, threads: store, change: () => tell?.() };
}

const scope = { projectId: "p", checkout: null };
const plan = { kind: "file", path: ".alta/plans/p.md" } as const;

test("a review is read when it is first retained, again when the host tells of a change, and forgotten when let go", async () => {
  const server = host();
  server.threads(null).push(dto("c1", plan.path), dto("c2", "other.md"));
  const hub = createReviewsHub(server.api, noTimers);
  const release = hub.retainList(scope, plan);
  assert.equal(hub.list(scope, plan).loaded, false);
  const disconnect = hub.connect("epoch");
  await settled();
  const first = hub.list(scope, plan);
  assert.deepEqual([first.loaded, first.status, first.threads.map(thread => thread.id)], [true, "ok", ["c1"]], "only the threads of the file");
  assert.deepEqual(server.asked, [`list:-:file:${plan.path}:true`], "resolved threads are read too: a filter shows them");

  let published = 0;
  hub.subscribe(() => published++);
  server.change();
  await settled();
  assert.equal(hub.list(scope, plan), first, "the same answer keeps the same object");
  assert.equal(published, 0);
  server.threads(null).push(dto("c3", plan.path));
  server.change();
  await settled();
  assert.deepEqual(hub.list(scope, plan).threads.map(thread => thread.id), ["c1", "c3"]);
  assert.equal(published, 1);

  release();
  server.asked.length = 0;
  server.change();
  await settled();
  assert.deepEqual(server.asked, [], "nothing is read for a review no surface shows");
  assert.equal(hub.list(scope, plan).loaded, false);
  disconnect();
});

test("a change reads its checkout again before it is done, and leaves the reviews of other checkouts alone", async () => {
  const server = host();
  const hub = createReviewsHub(server.api, noTimers);
  const disconnect = hub.connect("epoch");
  const worktree = { projectId: "p", checkout: "/w" };
  hub.retainList(scope, plan); hub.retainList(worktree, plan); hub.retainCounts("p");
  await settled();
  server.asked.length = 0;
  const added = await hub.add(scope, plan, { side: "new", startLine: 2, endLine: 4, body: "Say why", suggestion: null });
  assert.deepEqual([added.ok, added.thread?.id, added.thread?.startLine], [true, "c1", 2]);
  assert.deepEqual(server.asked, [`add:${plan.path}:2-4`, `list:-:file:${plan.path}:true`, "counts:p"]);
  assert.deepEqual(hub.list(scope, plan).threads.map(thread => thread.id), ["c1"], "shown when the call says it is done");
  assert.equal(hub.list(worktree, plan).threads.length, 0);
  assert.deepEqual(checkoutCounts(hub.counts("p"), null), { checkout: null, drafts: 1, open: 0, addressed: 0, missing: false });
  assert.deepEqual(checkoutCounts(hub.counts("p"), "/elsewhere").drafts, 0);

  assert.equal((await hub.update(scope, "c1", "Say why, please", "x")).thread?.suggestion, "x");
  // A comment of a diff names its file; without one nothing is sent.
  const changes = { kind: "changes" } as const;
  server.asked.length = 0;
  assert.equal((await hub.add(scope, changes, { side: "old", startLine: 3, endLine: 3, body: "Why?", suggestion: null })).status, "invalid_request");
  assert.equal((await hub.add(scope, changes, { path: "src/a.ts", side: "old", startLine: 3, endLine: 3, body: "Why?", suggestion: null })).ok, true);
  assert.deepEqual(server.asked.filter(entry => entry.startsWith("add")), ["add:src/a.ts:3-3"]);
  assert.equal((await hub.update(scope, "c1", "Say why, please", null)).thread?.suggestion, null, "null takes the suggestion away");
  assert.equal((await hub.resolve(scope, "c1", true)).thread?.state, "resolved");
  assert.equal((await hub.remove(scope, "c1")).ok, true);
  assert.equal((await hub.remove(scope, "c1")).status, "not_found");
  assert.equal(hub.list(scope, plan).threads.length, 0);
  disconnect();
});

test("a round goes to a session or to a new one, without an empty overall comment", async () => {
  const server = host();
  const hub = createReviewsHub(server.api, noTimers);
  const disconnect = hub.connect("epoch");
  assert.deepEqual(await hub.submit(scope, plan, "changes_requested", "  ", { sessionId: "s1" }), { ok: true, status: "ok", message: null, sessionId: "s1", roundId: "r1" });
  assert.equal((await hub.submit(scope, plan, "approved", "Go", { newSession: true, like: "s1" })).sessionId, "fresh");
  assert.deepEqual(server.asked.filter(entry => entry.startsWith("submit")), ["submit:changes_requested:s1:false:-", "submit:approved:new:true:Go"]);
  disconnect();
  assert.equal((await hub.submit(scope, plan, "approved", null, { sessionId: "s1" })).status, "unavailable", "no host, no round");
});

test("another host starts from nothing, and a refusal of the host is what the review says", async () => {
  const server = host();
  server.threads(null).push(dto("c1", plan.path));
  const hub = createReviewsHub(server.api, noTimers);
  hub.retainList(scope, plan);
  let disconnect = hub.connect("epoch");
  await settled();
  assert.equal(hub.list(scope, plan).threads.length, 1);
  disconnect();
  assert.equal(hub.list(scope, plan).loaded, false, "nothing of the old host stays");
  disconnect = hub.connect("other");
  await settled();
  assert.deepEqual([hub.list(scope, plan).loaded, hub.list(scope, plan).status, hub.list(scope, plan).threads.length], [true, "stale_epoch", 0]);
  disconnect();
  assert.notEqual(reviewKey(scope, plan), reviewKey({ projectId: "p", checkout: "/w" }, plan));
  assert.notEqual(reviewKey(scope, { kind: "changes" }), reviewKey(scope, { kind: "branch" }));
});
