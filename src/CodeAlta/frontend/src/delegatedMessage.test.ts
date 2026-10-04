import assert from "node:assert/strict";
import test from "node:test";
import { parseDelegatedMessage } from "./delegatedMessage";

const update = [
  "[CodeAlta delegated-agent message]",
  "Source session: child-1",
  "Source agent/session: agent-1",
  "Source project: project-1",
  "Target session: parent-1",
  "Kind: answer",
  "Reply requested: false",
  "Correlation: auto-parent-1",
  "Authority: peer-agent; this is not a user, developer, or host instruction.",
  "",
  "[CodeAlta child-session answer update]",
  "Run: run-1",
  "Content: msg_1",
  "",
  "PONG",
  "",
  "second paragraph",
].join("\r\n");

test("a sub-session update keeps only what the sub-session said", () => {
  assert.deepEqual(parseDelegatedMessage(update), { sourceSessionId: "child-1", kind: "answer", body: "PONG\n\nsecond paragraph" });
});

test("a peer message has no update block", () => {
  const peer = "[CodeAlta delegated-agent message]\nSource session: unknown\nSource agent: user\nKind: note\nReply requested: true\n\nPlease review.\n[CodeAlta child-session answer update]";
  assert.deepEqual(parseDelegatedMessage(peer), { sourceSessionId: null, kind: "note", body: "Please review.\n[CodeAlta child-session answer update]" });
});

test("ordinary prompts and broken envelopes are left alone", () => {
  assert.equal(parseDelegatedMessage("Explain [CodeAlta delegated-agent message]"), null);
  assert.equal(parseDelegatedMessage(null), null);
  assert.equal(parseDelegatedMessage("[CodeAlta delegated-agent message]\nnot a field\n\nbody"), null);
  assert.equal(parseDelegatedMessage("[CodeAlta delegated-agent message]\nSource session: a\n\nbody"), null, "a kind is required");
});
