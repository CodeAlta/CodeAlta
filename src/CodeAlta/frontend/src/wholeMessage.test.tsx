import test from "node:test";
import assert from "node:assert/strict";
import { renderToStaticMarkup } from "react-dom/server";
import type { MessageTextRequest, MessageTextResponse } from "#neoastra";
import { buildTimelineItems, wholeMessageBody } from "./timeline";
import { createMessageReader, maximumWholeMessageCharacters } from "./wholeMessage";
import { WholeMessageNote } from "./WholeMessageNote";

// A text served in parts of `size`, as toolCalls.readMessage serves one.
function server(text: string, size: number, requests: MessageTextRequest[] = []) {
  return async (request: MessageTextRequest): Promise<MessageTextResponse> => {
    requests.push(request);
    const part = text.slice(request.position, request.position + size);
    return { status: "ok", text: { text: part, length: text.length, more: request.position + part.length < text.length } };
  };
}

test("the reader of a message joins its parts, each asked from where the last ended", async () => {
  const requests: MessageTextRequest[] = [];
  const read = createMessageReader(server("abcdefghij", 4, requests), "epoch", "session");
  assert.deepEqual(await read("42", new AbortController().signal), { status: "ok", text: "abcdefghij", complete: true });
  assert.deepEqual(requests.map(request => [request.expectedEpoch, request.sessionId, request.offset, request.position]),
    [["epoch", "session", "42", 0], ["epoch", "session", "42", 4], ["epoch", "session", "42", 8]]);
});

test("the reader stops at its limit, and a refusal or a failed call is a failure", async () => {
  const huge = createMessageReader(server("x".repeat(maximumWholeMessageCharacters + 10), 1024 * 1024), "epoch", "session");
  const read = await huge("1", new AbortController().signal);
  assert.equal(read.status === "ok" && read.complete, false);
  assert.equal(read.status === "ok" && read.text.length, maximumWholeMessageCharacters);
  assert.deepEqual(await createMessageReader(async () => ({ status: "missing_record", text: null }), "e", "s")("1", new AbortController().signal), { status: "failed" });
  assert.deepEqual(await createMessageReader(async () => { throw new Error("closed"); }, "e", "s")("1", new AbortController().signal), { status: "failed" });
  // A part that says more follows but brings nothing would never end.
  assert.deepEqual(await createMessageReader(async () => ({ status: "ok", text: { text: "", length: 5, more: true } }), "e", "s")("1", new AbortController().signal), { status: "failed" });
});

test("a row the history page shortened is marked, the others are not", () => {
  const entry = { offset: "120", eventType: "contentCompleted", kind: "Assistant", text: "## Start of a long answer", textTruncated: true,
    detailsTruncated: false, bodyOmitted: false, timestamp: "2026-10-27T10:00:00Z", providerId: "p", sessionId: "s" };
  assert.equal(buildTimelineItems([entry as never])[0].textShortened, true);
  assert.equal(buildTimelineItems([{ ...entry, textTruncated: false, detailsTruncated: true } as never])[0].textShortened, undefined, "only its text counts");
});

test("the note under a shortened message offers to read the rest, says when it is loading, failed, or still partial", () => {
  const load = () => {};
  const idle = renderToStaticMarkup(<WholeMessageNote state={{ status: "idle" }} load={load} />);
  assert.match(idle, /The rest of this message is too long to show here\./);
  assert.match(idle, /message-shortened-load[^>]*>Show the whole message/);
  assert.doesNotMatch(renderToStaticMarkup(<WholeMessageNote state={{ status: "idle" }} load={null} />), /Show the whole message/, "nothing reads it here");
  assert.match(renderToStaticMarkup(<WholeMessageNote state={{ status: "loading" }} load={load} />), /disabled=""/);
  assert.match(renderToStaticMarkup(<WholeMessageNote state={{ status: "failed" }} load={load} />), /role="alert"[^>]*>The message could not be read\./);
  assert.equal(renderToStaticMarkup(<WholeMessageNote state={{ status: "ok", text: "whole", complete: true }} load={load} />), "");
  assert.match(renderToStaticMarkup(<WholeMessageNote state={{ status: "ok", text: "x".repeat(4096), complete: false }} load={load} />),
    /Only the first 4,096 characters of this message are shown\./);
});

test("the whole text of a message becomes its body as its row makes it", () => {
  const prompt = "Script run-1 (Research) ended:\nScript completed · 2.0 s · 1 call\nResult:\n## Whole result";
  const [workflow] = buildTimelineItems([{ offset: "7", eventType: "contentCompleted", kind: "User", provenance: "workflow", text: prompt,
    textTruncated: true, detailsTruncated: false, bodyOmitted: false, timestamp: "2026-10-27T10:00:00Z", providerId: "p", sessionId: "s" } as never]);
  assert.equal(wholeMessageBody(workflow, prompt), "## Whole result");
  assert.equal(wholeMessageBody({}, "plain text"), "plain text");
});
