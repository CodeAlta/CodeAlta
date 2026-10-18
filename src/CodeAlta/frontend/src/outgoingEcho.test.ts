import test from "node:test";
import assert from "node:assert/strict";
import { matchesOutgoing, matchesOutgoingText } from "./outgoingEcho";

test("image echoes reconcile only within the submitted run", () => {
  const text = "Describe this\n\nLocal image: image.png";
  assert.equal(matchesOutgoingText(text, "Describe this", 1, "run", "run"), true);
  assert.equal(matchesOutgoingText(text, "Describe this", 1, "run", "other"), false);
  assert.equal(matchesOutgoingText(text, "Describe this", 1, null, "run"), false);
  assert.equal(matchesOutgoingText(text, "Describe this", 0, "run", "run"), false);
  assert.equal(matchesOutgoingText("attachment", "", 1, "run", "run"), true);
  assert.equal(matchesOutgoingText("Different prompt", "Describe this", 1, "run", "run"), false);
});

test("a prompt is recognized by the identity of its Send, whatever the host recorded of its text", () => {
  const echo = { clientRequestId: "send-1", text: "Look at @src/app.ts", imageCount: 0, runId: null };
  // A project reference becomes a link and a line names the file: the text is not what was typed.
  const recorded = "Look at [src/app.ts](src/app.ts)\nFile: /project/src/app.ts";
  assert.equal(matchesOutgoing(echo, { clientRequestId: "send-1", text: recorded, runId: "run" }), true);
  assert.equal(matchesOutgoing(echo, { clientRequestId: "send-2", text: echo.text, runId: "run" }), false, "Another Send with the same text is another prompt.");
  // Without an identity (a record of an older version), the text decides, as before.
  assert.equal(matchesOutgoing(echo, { clientRequestId: null, text: recorded, runId: "run" }), false);
  assert.equal(matchesOutgoing(echo, { text: echo.text, runId: "run" }), true);
  assert.equal(matchesOutgoing({ ...echo, text: "" , imageCount: 1, runId: "run" }, { text: "attachment", runId: "run" }), true);
});
