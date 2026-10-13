import assert from "node:assert/strict";
import test from "node:test";
import { createSystemPromptCache } from "./systemPromptReader";

type Request = { expectedEpoch: string; sessionId: string; offset: string; part: string | null; position: number };
const text = (value: string) => ({ text: value, length: value.length, more: false });
const view = (overrides: object = {}) => ({ status: "ok", text: null, change: null, prompt: { changed: true, hash: "hash-2", name: "Default", sourcePath: "prompts/default.prompt.md",
  timestamp: "2026-10-13T09:50:36Z", totalTokens: 30, systemTokens: 10, developerTokens: 20, applied: true, lossy: false,
  system: text("System"), developer: text("Developer two"),
  previous: { offset: "10", timestamp: "2026-10-13T09:49:40Z", system: text("System"), developer: text("Developer") }, change: { added: 1, removed: 1 }, ...overrides } });

test("a prompt is read once with the one it replaced, kept, and found by the offset of its row", async () => {
  const requests: Request[] = [];
  const cache = createSystemPromptCache(async request => { requests.push(request); return view(); });
  const reader = cache.reader("epoch", "session");
  assert.equal(reader.peek("90"), undefined);
  const [first, second] = await Promise.all([reader.read("90"), reader.read("90")]);
  assert.equal(first, second, "Two readers of the same prompt share one read.");
  assert.deepEqual(first, { status: "ready", record: { changed: true, hash: "hash-2", name: "Default", sourcePath: "prompts/default.prompt.md",
    timestamp: "2026-10-13T09:50:36Z", tokens: 30, systemTokens: 10, developerTokens: 20, applied: true, lossy: false,
    system: { text: "System", partial: false }, developer: { text: "Developer two", partial: false },
    previous: { timestamp: "2026-10-13T09:49:40Z", system: { text: "System", partial: false }, developer: { text: "Developer", partial: false } },
    change: { added: 1, removed: 1 } } });
  assert.deepEqual(requests, [{ expectedEpoch: "epoch", sessionId: "session", offset: "90", part: null, position: 0 }]);
  assert.equal(reader.peek("90"), first);
  await reader.read("90");
  assert.equal(requests.length, 1, "A record that was read is not read again.");
  // What the window read is what the row of the prompt asks for: nothing more is read for it.
  assert.deepEqual(reader.peekChange("90"), { added: 1, removed: 1 });
  assert.deepEqual(await reader.readChange("90"), { added: 1, removed: 1 });
  assert.equal(requests.length, 1);
  // Another session or host is another reader, and a rewind writes the journal again: the same offset names another record.
  await cache.reader("epoch", "other").read("90");
  await cache.reader("next", "session").read("90");
  const rewound = cache.reader("epoch", "session", 1);
  assert.equal(rewound.peek("90"), undefined);
  assert.equal(rewound.peekChange("90"), undefined);
  await rewound.read("90");
  assert.equal(requests.length, 4);
  assert.deepEqual(requests[3], requests[0]);
  assert.deepEqual(cache.usage(), { entries: 4, characters: 4 * 34, changes: 4 });
});

test("the first prompt of a session has nothing before it, and a record the host cannot give is a failed read", async () => {
  const first = await createSystemPromptCache(async () => view({ changed: false, previous: null, change: null, sourcePath: null, name: null })).reader("e", "s").read("10");
  assert.deepEqual(first.status === "ready" && [first.record.changed, first.record.previous, first.record.change, first.record.name, first.record.sourcePath], [false, null, null, null, null]);
  for (const reply of [{ status: "ok", prompt: null }, { status: "ok", prompt: { changed: true, system: "text" } }, { status: "ok", prompt: { system: text("x") } },
    { status: "ok", prompt: { changed: true, system: null, developer: null, previous: { system: null, developer: null } } }, "nothing", null]) {
    assert.deepEqual(await createSystemPromptCache(async () => reply).reader("e", "s").read("10"), { status: "failed" }, JSON.stringify(reply));
  }
});

test("a long text is read part after part, at the offset of its own record, up to the limit of the page", async () => {
  const long = "0123456789".repeat(10), before = "abcdefghij".repeat(7);
  const requests: Request[] = [];
  const slice = (value: string, from: number) => ({ text: value.slice(from, from + 30), length: value.length, more: from + 30 < value.length });
  const serve = (request: Request) => {
    requests.push(request);
    if (request.part === null) return view({ developer: slice(long, 0), previous: { offset: "10", timestamp: "", system: slice(before, 0), developer: text("Developer") } });
    // The rest of a text is asked for where its record is: the prompt at 90, the one it replaced at 10.
    return request.offset === "90" && request.part === "developer" ? { status: "ok", text: slice(long, request.position) }
      : request.offset === "10" && request.part === "system" ? { status: "ok", text: slice(before, request.position) } : { status: "invalid" };
  };
  const whole = await createSystemPromptCache(async request => serve(request)).reader("e", "s").read("90");
  assert.deepEqual(whole.status === "ready" && [whole.record.developer, whole.record.previous!.system, whole.record.system],
    [{ text: long, partial: false }, { text: before, partial: false }, { text: "System", partial: false }]);
  assert.deepEqual(requests.map(request => [request.offset, request.part, request.position]),
    [["90", null, 0], ["10", "system", 30], ["10", "system", 60], ["90", "developer", 30], ["90", "developer", 60], ["90", "developer", 90]]);
  // Beyond the limit the text is partial, and a part that cannot be read ends the text where it is.
  const limited = await createSystemPromptCache(async request => serve(request), { maximumTextCharacters: 50 }).reader("e", "s").read("90");
  assert.deepEqual(limited.status === "ready" && [limited.record.developer!.text.length, limited.record.developer!.partial], [60, true]);
  const broken = await createSystemPromptCache(async request => request.part === null ? serve(request) : { status: "read_failed" }).reader("e", "s").read("90");
  assert.deepEqual(broken.status === "ready" && [broken.record.developer!.text.length, broken.record.developer!.partial, broken.record.previous!.system!.partial], [30, true, true]);
});

test("a refusal that may lift is asked again later, another one is remembered, and capacity is retried", async () => {
  let status = "read_failed", calls = 0;
  const cache = createSystemPromptCache(async () => { calls++; return status === "ok" ? view() : { status, prompt: null, text: null, change: null }; }, { retryDelayMilliseconds: 1 });
  const reader = cache.reader("e", "s");
  assert.deepEqual(await reader.read("90"), { status: "failed" });
  assert.equal(reader.peek("90"), undefined, "A failed read of the journal is not kept.");
  status = "capacity";
  assert.deepEqual(await reader.read("90"), { status: "failed" });
  assert.equal(calls, 4, "A host that is busy is asked three times.");
  status = "missing_record";
  assert.deepEqual(await reader.read("90"), { status: "failed" });
  status = "ok";
  assert.deepEqual(await reader.read("90"), { status: "failed" }, "An offset that is no prompt stays one.");
  assert.equal(calls, 5);
  // A transport that fails is a failed read, not an exception of the window.
  assert.deepEqual(await createSystemPromptCache(async () => { throw new Error("closed"); }).reader("e", "s").read("90"), { status: "failed" });
});

test("the rows ask for the size of a change one after the other, and each answer is kept", async () => {
  const requests: Request[] = [];
  let running = 0, most = 0, status = "ok";
  const cache = createSystemPromptCache(async request => {
    requests.push(request);
    most = Math.max(most, ++running);
    await new Promise(resolve => setTimeout(resolve, 2));
    running--;
    return status !== "ok" ? { status } : { status: "ok", prompt: null, text: null, change: request.offset === "10" ? null : { added: Number(request.offset), removed: 1 } };
  }, { maximumChanges: 3, retryDelayMilliseconds: 1 });
  const reader = cache.reader("e", "s");
  assert.equal(reader.peekChange("90"), undefined);
  const answers = await Promise.all([reader.readChange("90"), reader.readChange("90"), reader.readChange("70"), reader.readChange("10")]);
  assert.deepEqual(answers, [{ added: 90, removed: 1 }, { added: 90, removed: 1 }, { added: 70, removed: 1 }, null]);
  assert.equal(most, 1, "Each one costs the host a pass over the journal: they do not run together.");
  assert.deepEqual(requests.map(request => [request.offset, request.part, request.position]), [["90", "change", 0], ["70", "change", 0], ["10", "change", 0]]);
  // A prompt that replaced none is remembered as such, and is not asked again.
  assert.deepEqual([reader.peekChange("90"), reader.peekChange("10")], [{ added: 90, removed: 1 }, null]);
  await reader.readChange("10");
  assert.equal(requests.length, 3);
  // A refusal that may lift leaves the row without its numbers and is asked again; the oldest answers make room.
  status = "capacity";
  assert.equal(await cache.reader("e", "s", 0).readChange("50"), null);
  assert.equal(reader.peekChange("50"), undefined);
  status = "ok";
  assert.deepEqual(await reader.readChange("50"), { added: 50, removed: 1 });
  assert.equal(reader.peekChange("90"), undefined);
  assert.equal(cache.usage().changes, 3);
});
