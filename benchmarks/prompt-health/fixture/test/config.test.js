import { test } from "node:test";
import assert from "node:assert/strict";
import { parseTimeout } from "../src/config.js";

test("parseTimeout reads seconds and durations", () => {
  assert.equal(parseTimeout(30), 30);
  assert.equal(parseTimeout("45s"), 45);
  assert.equal(parseTimeout("2m"), 120);
  assert.equal(parseTimeout(0), 0);
});

test("parseTimeout rejects what is not a duration", () => {
  assert.throws(() => parseTimeout(-1), RangeError);
  assert.throws(() => parseTimeout("soon"), TypeError);
});
