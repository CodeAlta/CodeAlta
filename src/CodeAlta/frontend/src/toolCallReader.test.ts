import assert from "node:assert/strict";
import test from "node:test";
import { createToolCallCache } from "./toolCallReader";

type Request = { expectedEpoch: string; sessionId: string; offset: string; outputOffset: string | null; part: string | null; position: number };
const view = (overrides: object = {}) => ({ status: "ok", text: null, call: { kind: "ToolCall", phase: "Completed", name: "shell_command", timestamp: "2026-10-07T08:00:00Z",
  message: null, command: "git status", workingDirectory: null, exitCode: 0, error: null,
  arguments: { text: '{"command":"git status"}', length: 24, more: false }, output: { text: "clean", length: 5, more: false }, diff: null,
  readFiles: ["a.cs", 7], modifiedFiles: [], ...overrides } });

test("a tool call is read once, kept, and found by the offsets of its row", async () => {
  const requests: Request[] = [];
  const cache = createToolCallCache(async request => { requests.push(request); return view(); });
  const reader = cache.reader("epoch", "session");
  assert.equal(reader.peek("40", "30"), undefined);
  const [first, second] = await Promise.all([reader.read("40", "30"), reader.read("40", "30")]);
  assert.equal(first, second, "Two readers of the same call share one read.");
  assert.equal(first.status, "ready");
  assert.deepEqual(first.status === "ready" && [first.record.command, first.record.exitCode, first.record.output, first.record.readFiles],
    ["git status", 0, { text: "clean", partial: false }, ["a.cs"]]);
  assert.deepEqual(requests, [{ expectedEpoch: "epoch", sessionId: "session", offset: "40", outputOffset: "30", part: null, position: 0 }]);
  assert.equal(reader.peek("40", "30"), first);
  await reader.read("40", "30");
  assert.equal(requests.length, 1, "A record that was read is not read again.");
  // Another phase of the call is another record, and another session or host another reader.
  await reader.read("20", null);
  await cache.reader("epoch", "other").read("40", "30");
  await cache.reader("next", "session").read("40", "30");
  assert.equal(requests.length, 4);
  assert.deepEqual(cache.usage(), { entries: 4, characters: 4 * 29 });
});

test("a long text is read part after part, up to the limit of the page", async () => {
  const output = "0123456789".repeat(10);
  const requests: Request[] = [];
  const serve = (request: Request) => {
    requests.push(request);
    const slice = (from: number) => ({ text: output.slice(from, from + 30), length: output.length, more: from + 30 < output.length });
    return request.part === null ? view({ output: slice(0) }) : request.part === "output" ? { status: "ok", call: null, text: slice(request.position) } : { status: "invalid" };
  };
  const whole = await createToolCallCache(async request => serve(request)).reader("e", "s").read("1", null);
  assert.deepEqual(whole.status === "ready" && whole.record.output, { text: output, partial: false });
  assert.deepEqual(requests.map(request => [request.part, request.position]), [[null, 0], ["output", 30], ["output", 60], ["output", 90]]);
  // Beyond the limit the text is partial, and a part that cannot be read ends the text where it is.
  const limited = await createToolCallCache(async request => serve(request), { maximumTextCharacters: 50 }).reader("e", "s").read("1", null);
  assert.deepEqual(limited.status === "ready" && [limited.record.output!.text.length, limited.record.output!.partial], [60, true]);
  const broken = await createToolCallCache(async request => request.part === null ? serve(request) : { status: "read_failed" }).reader("e", "s").read("1", null);
  assert.deepEqual(broken.status === "ready" && [broken.record.output!.text.length, broken.record.output!.partial], [30, true]);
});

test("a refusal that may lift is asked again later, another one is remembered, and capacity is retried", async () => {
  let status = "read_failed", calls = 0;
  const cache = createToolCallCache(async () => { calls++; return status === "ok" ? view() : { status, call: null, text: null }; }, { retryDelayMilliseconds: 1 });
  const reader = cache.reader("e", "s");
  assert.deepEqual(await reader.read("1", null), { status: "failed" });
  assert.equal(reader.peek("1", null), undefined, "A failed read is not kept.");
  status = "ok";
  assert.equal((await reader.read("1", null)).status, "ready");
  status = "missing_record"; calls = 0;
  assert.deepEqual(await reader.read("2", null), { status: "failed" });
  assert.deepEqual(await reader.read("2", null), { status: "failed" });
  assert.equal(calls, 1, "A record that is not there is not asked for twice.");
  status = "capacity"; calls = 0;
  assert.deepEqual(await reader.read("3", null), { status: "failed" });
  assert.equal(calls, 3, "The host admits a few reads at a time: a busy one is asked again twice.");
  // A transport that throws and a reply of another shape are failed reads.
  const throwing = createToolCallCache(async () => { throw new Error("closed"); }).reader("e", "s");
  assert.deepEqual(await throwing.read("1", null), { status: "failed" });
  for (const malformed of [null, {}, { status: "ok" }, { status: "ok", call: { kind: "ToolCall" } }, view({ output: { text: 1, length: 1, more: false } })])
    assert.deepEqual(await createToolCallCache(async () => malformed).reader("e", "s").read("1", null), { status: "failed" });
});

test("the oldest records leave when the cache is full", async () => {
  const cache = createToolCallCache(async request => view({ output: { text: "x".repeat(10), length: 10, more: false }, arguments: null, command: request.offset }),
    { maximumEntries: 2 });
  const reader = cache.reader("e", "s");
  await reader.read("1", null);
  await reader.read("2", null);
  reader.peek("1", null);
  await reader.read("3", null);
  assert.equal(reader.peek("2", null), undefined, "The record that was not looked at for the longest leaves first.");
  assert.notEqual(reader.peek("1", null), undefined);
  assert.deepEqual(cache.usage(), { entries: 2, characters: 20 });
  const small = createToolCallCache(async () => view({ output: { text: "x".repeat(100), length: 100, more: false } }), { maximumCharacters: 50 });
  assert.equal((await small.reader("e", "s").read("1", null)).status, "ready");
  assert.deepEqual(small.usage(), { entries: 0, characters: 0 }, "A record larger than the cache is served and not kept.");
});
