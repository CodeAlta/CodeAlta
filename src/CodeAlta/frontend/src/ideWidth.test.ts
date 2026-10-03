import assert from "node:assert/strict";
import test from "node:test";
import { parseIdeWidth, resizeIdeWidth, persistIdeWidth } from "./ideWidth";

test("Explorer width is bounded and malformed storage is refused", () => {
  assert.deepEqual(parseIdeWidth(null), { width: 272 });
  for (const value of ['{bad', 'null', '[]', '{"projects":280,"sessions":420}', '{"width":"300"}'])
    assert.deepEqual(parseIdeWidth(value), { width: 272 });
  assert.deepEqual(parseIdeWidth('{"width":999}'), { width: 720 });
  assert.deepEqual(parseIdeWidth('{"width":600}'), { width: 600 });
  assert.deepEqual(parseIdeWidth('{"width":1}'), { width: 220 });
  assert.deepEqual(parseIdeWidth('{"width":300.6}'), { width: 301 });
  // A value saved when the Explorer could also be collapsed from here keeps its width.
  assert.deepEqual(parseIdeWidth('{"width":300,"full":true}'), { width: 300 });
  assert.deepEqual(resizeIdeWidth({ width: 272 }, 200), { width: 472 });
  assert.deepEqual(resizeIdeWidth({ width: 700 }, 200), { width: 720 });
  assert.deepEqual(resizeIdeWidth({ width: 272 }, -200), { width: 220 });
  assert.equal(persistIdeWidth(() => { throw new Error("storage denied"); }, { width: 272 }), false);
  let saved = "legacy";
  assert.equal(persistIdeWidth(value => { saved = value; }, { width: 304 }), true);
  assert.equal(saved, '{"width":304}');
});
