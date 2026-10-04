import assert from "node:assert/strict";
import test from "node:test";
import { skillInstructions } from "./skillDetail";

test("the instructions of a skill file are its text without the front matter", () => {
  assert.equal(skillInstructions("---\nname: a\ndescription: b\n---\n\n# Title\n\nUse it.\n"), "# Title\n\nUse it.");
  assert.equal(skillInstructions("﻿---\r\nname: a\r\n---\r\nBody"), "Body");
  assert.equal(skillInstructions("# No front matter\n\n---\n\ntext"), "# No front matter\n\n---\n\ntext");
  assert.equal(skillInstructions("---\nname: a\n---"), "");
  assert.equal(skillInstructions("--- not front matter"), "--- not front matter");
});
