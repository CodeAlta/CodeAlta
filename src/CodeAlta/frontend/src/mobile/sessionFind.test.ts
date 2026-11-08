import assert from "node:assert/strict";
import test from "node:test";
import { findPattern } from "../findInPage/findInPage";
import { activityHolds } from "./sessionFind";
import type { ActivityBlock } from "./timeline";

test("a folded activity holds what is looked for in its thoughts, in the titles of its calls and in what they work on", () => {
  const block: ActivityBlock = { kind: "activity", key: "a1", rows: [
    { type: "thought", key: "t1", text: "Weighing the **parser** first", at: null },
    { type: "tool", key: "c1", title: "read_file", phase: "completed", at: null, end: null, subject: "src/Lexer.cs" },
    { type: "tool", key: "c2", title: "Bash", phase: "completed", at: null, end: null },
  ] };
  const holds = (text: string, matchCase = false) => activityHolds(block, findPattern(text, { matchCase, wholeWord: false, regex: false })!);
  assert.equal(holds("PARSER"), true, "A thought, whatever the case.");
  assert.equal(holds("read_f"), true, "The title of a call.");
  assert.equal(holds("lexer.cs"), true, "What a call works on.");
  assert.equal(holds("bash"), true);
  assert.equal(holds("Lexer", true), true);
  assert.equal(holds("lexer", true), false, "Match case.");
  assert.equal(holds("output"), false);
  // A global pattern used again starts from the start of each text.
  const pattern = findPattern("parser")!;
  assert.equal(activityHolds(block, pattern), true);
  assert.equal(activityHolds(block, pattern), true);
});
