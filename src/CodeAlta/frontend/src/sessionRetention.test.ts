import assert from "node:assert/strict";
import test from "node:test";
import { collectSessionRetention, createSessionRetention, type RetentionPublication, type RetentionStorage } from "./sessionRetention";
import { sessionTabsKey } from "./sessionTabs";
import { draftStorageKey } from "./promptDraft";
import { createImageDrafts } from "./promptImages";

const storage = (values: Record<string, string>): RetentionStorage => ({
  get length() { return Object.keys(values).length; },
  key: index => Object.keys(values)[index] ?? null,
  getItem: key => values[key] ?? null,
});
const tabs = (...ids: string[]) => JSON.stringify({ version: 1,
  open: ids.map(sessionId => ({ sessionId, projectId: null, path: null })), active: null });
const empty = () => collectSessionRetention(() => storage({}));
const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { resolve, promise }; };

test("retention collects all stored spaces and unmounted drafts, not a catalog or visible-space projection", () => {
  const reading = collectSessionRetention(() => storage({
    [sessionTabsKey]: tabs("visible"), [`${sessionTabsKey}.hidden`]: tabs("elsewhere", "visible"),
    [`${sessionTabsKey}.removed-space`]: tabs("not-in-catalog"),
    [draftStorageKey("unmounted")]: "unsent", [draftStorageKey("whitespace")]: "  ",
    [draftStorageKey("empty")]: "", "codealta.desktop.localPrompt.null": "new chat",
    "unrelated": "{",
  }));
  assert.equal(reading.complete, true);
  assert.deepEqual(reading.sessionIds, ["elsewhere", "not-in-catalog", "unmounted", "visible", "whitespace"]);
});

test("unavailable, changing, oversized and malformed storage cannot certify an empty protection set", () => {
  for (const raw of ["", "{", "null", "{}", tabs("ok").replace('"version":1', '"version":2'), tabs("ok").replace('"active":null', '"active":{}'), "x".repeat(65537)]) {
    const result = collectSessionRetention(() => storage({ [`${sessionTabsKey}.hidden`]: raw, [draftStorageKey("kept")]: "draft" }));
    assert.equal(result.complete, false, raw.slice(0, 40));
    assert.ok(result.sessionIds.includes("kept"));
  }
  assert.equal(collectSessionRetention(() => { throw Error("denied"); }).complete, false);
  assert.equal(collectSessionRetention(() => ({ length: 1, key: () => null, getItem: () => null })).complete, false);
  assert.equal(collectSessionRetention(() => ({ length: 1, key: () => sessionTabsKey, getItem: () => { throw Error("denied"); } })).complete, false);
  assert.equal(collectSessionRetention(() => storage({ [draftStorageKey(" ")]: "draft" })).complete, false);
  assert.equal(collectSessionRetention(() => storage({ [draftStorageKey("")]: "" })).complete, false);
  assert.equal(collectSessionRetention(() => storage({ [draftStorageKey("long")]: "x".repeat(32769) })).complete, false);
  let reads = 0;
  assert.equal(collectSessionRetention(() => ({ length: 1, key: () => sessionTabsKey, getItem: () => tabs(++reads === 1 ? "one" : "two") })).complete, false);
});

test("image-only session drafts remain protected offscreen; local images have no saved session", () => {
  const images = createImageDrafts();
  const image = { title: "Image", mediaType: "image/png", base64: "AA==" };
  for (const key of [JSON.stringify(["epoch", "image-only", "project", "/p"]), JSON.stringify(["local-draft", "epoch", "project", "/p"])])
    images.replace(key, images.get(key), [image]);
  const finish = images.beginRead(JSON.stringify(["epoch", "pasting", null, null]));
  const reading = collectSessionRetention(() => storage({}), images.retentionKeys());
  assert.equal(reading.complete, true);
  assert.deepEqual(reading.sessionIds, ["image-only", "pasting"]);
  finish?.();
  assert.equal(collectSessionRetention(() => storage({}), ["bad-image-key"]).complete, false);
});

test("claims are serialized with increasing revisions and ack precedes exposure", async () => {
  const requests: RetentionPublication[] = [];
  const ack = deferred<{ accepted: boolean; reason: string | null }>();
  const owner = createSessionRetention(request => { requests.push(request); return requests.length === 1 ? ack.promise : Promise.resolve({ accepted: true, reason: null }); }, empty, "page");
  let opened = false, drafted = false;
  const first = owner.admit("epoch", ["open"], () => true, () => { opened = true; });
  const second = owner.admit("epoch", ["draft"], () => true, () => { drafted = true; });
  await Promise.resolve(); await Promise.resolve();
  assert.equal(opened, false); assert.equal(drafted, false); assert.equal(requests.length, 1);
  ack.resolve({ accepted: true, reason: null });
  assert.equal(await first, true); assert.equal(await second, true);
  assert.equal(opened, true); assert.equal(drafted, true);
  assert.deepEqual(requests.map(value => value.revision), [1, 2]);
  assert.deepEqual(requests[1].sessionIds, ["draft", "open"]);
  assert.ok(requests.every(value => value.pageId === "page" && value.expectedEpoch === "epoch" && value.ready === false), "partial integration never advertises readiness");
});

test("retirement rejection, transport failure, stale navigation and disconnect refuse admission", async () => {
  for (const reply of [async () => ({ accepted: false, reason: "retiring" }), async () => { throw Error("closed"); }]) {
    const owner = createSessionRetention(reply, empty, "page");
    assert.equal(await owner.admit("epoch", ["saved"], () => true, () => assert.fail("must not expose")), false);
  }
  const ack = deferred<{ accepted: boolean; reason: string | null }>();
  const requests: RetentionPublication[] = [];
  const owner = createSessionRetention(request => { requests.push(request); return ack.promise; }, empty, "page");
  const admission = owner.admit("epoch", ["saved"], () => true, () => assert.fail("disconnected ack"));
  await Promise.resolve(); await Promise.resolve();
  owner.invalidate();
  ack.resolve({ accepted: true, reason: null });
  assert.equal(await admission, false);
  assert.equal(owner.hasClaim("epoch", "saved"), false);
  await owner.connect("new-epoch");
  assert.equal(requests.at(-1)?.ready, false);
  assert.equal(requests.at(-1)?.revision, 2);
  assert.ok(requests.at(-1)?.sessionIds.includes("saved"), "invalidation never silently drops IDs");
  assert.equal(await owner.admit("new-epoch", ["saved"], () => false, () => assert.fail("stale navigation")), false);
});

test("ambiguous restoration stays latched and IDs survive later empty reads", async () => {
  let reading = collectSessionRetention(() => storage({ [sessionTabsKey]: tabs("saved") }));
  const requests: RetentionPublication[] = [];
  const owner = createSessionRetention(async request => { requests.push(request); return { accepted: true, reason: null }; }, () => reading, "page");
  await owner.publish("epoch");
  reading = { complete: false, sessionIds: ["draft"] };
  await owner.publish("epoch");
  reading = empty();
  await owner.publish("epoch");
  assert.equal(owner.storageComplete(), false);
  assert.deepEqual(requests.at(-1)?.sessionIds, ["draft", "saved"]);
  assert.ok(requests.every(request => !request.ready));
});

test("render admission is observable only after ack and is revoked synchronously on invalidation", async () => {
  const ack = deferred<{ accepted: boolean; reason: string | null }>();
  const owner = createSessionRetention(() => ack.promise, empty, "page");
  let observed = 0;
  owner.subscribe(() => { observed++; });
  const claim = owner.admit("epoch", ["saved"], () => true, () => {});
  assert.equal(owner.hasClaim("epoch", "saved"), false);
  ack.resolve({ accepted: true, reason: null });
  await claim;
  assert.equal(owner.hasClaim("epoch", "saved"), true);
  const before = owner.getSnapshot();
  owner.invalidate();
  assert.equal(owner.hasClaim("epoch", "saved"), false);
  assert.ok(owner.getSnapshot() > before);
  assert.ok(observed >= 2);
});

test("a synchronous lifetime change during acknowledgement notification cannot expose an admission", async () => {
  const owner = createSessionRetention(async () => ({ accepted: true, reason: null }), empty, "page");
  owner.subscribe(() => { if (owner.hasClaim("epoch", "saved")) owner.invalidate(); });
  assert.equal(await owner.admit("epoch", ["saved"], () => true, () => assert.fail("invalidated during notification")), false);
});

test("storage/draft notifications cannot revive claims after disconnect without explicit reconnection", async () => {
  const requests: RetentionPublication[] = [];
  const owner = createSessionRetention(async request => { requests.push(request); return { accepted: true, reason: null }; }, empty, "page");
  await owner.connect("epoch");
  owner.invalidate();
  assert.equal(await owner.publish("epoch"), false);
  assert.equal(await owner.admit("epoch", ["saved"], () => true, () => assert.fail("disconnected")), false);
  assert.equal(requests.length, 1);
  assert.equal(await owner.connect("epoch"), true);
});

test("readiness requires acknowledged restoration including initial selection, not connection or observation", async () => {
  const requests: RetentionPublication[] = [];
  const held = deferred<{ accepted: boolean; reason: string | null }>();
  const owner = createSessionRetention(request => { requests.push(request); return request.ready ? held.promise : Promise.resolve({ accepted: true, reason: null }); },
    () => ({ complete: true, sessionIds: ["inactive-space", "orphan-draft"] }), "page");
  await owner.connect("epoch");
  await owner.publish("epoch");
  assert.ok(requests.every(request => !request.ready));
  let exposed = false;
  const restoring = owner.restore("epoch", ["implicit-first-selection"], () => true, () => { exposed = true; });
  await Promise.resolve(); await Promise.resolve();
  assert.equal(exposed, false);
  assert.deepEqual(requests.at(-1)?.sessionIds, ["implicit-first-selection", "inactive-space", "orphan-draft"]);
  assert.equal(requests.at(-1)?.ready, true);
  held.resolve({ accepted: true, reason: null });
  assert.equal(await restoring, true);
  assert.equal(exposed, true);
  owner.invalidate();
  await owner.connect("epoch");
  assert.equal(requests.at(-1)?.ready, false, "reconnection must qualify restoration again");
});

test("ambiguous storage cannot become ready through restoration", async () => {
  const requests: RetentionPublication[] = [];
  const owner = createSessionRetention(async request => { requests.push(request); return { accepted: true, reason: null }; },
    () => ({ complete: false, sessionIds: ["recoverable"] }), "page");
  await owner.restore("epoch", ["selected"], () => true, () => {});
  await owner.publish("epoch");
  assert.ok(requests.every(request => !request.ready));
});
