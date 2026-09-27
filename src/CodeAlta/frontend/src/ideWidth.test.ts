import assert from "node:assert/strict";
import test from "node:test";
import { parseIdeWidth, resizeIdeWidth, persistIdeWidth } from "./ideWidth";

test("Explorer width is bounded, full-content preserves its restore width and malformed storage is refused", () => {
  assert.deepEqual(parseIdeWidth(null), { width: 272, full: false });
  for (const value of ['{bad', 'null', '[]', '{"projects":280,"sessions":420}', '{"width":300}', '{"width":300,"full":"true"}'])
    assert.deepEqual(parseIdeWidth(value), { width: 272, full: false });
  assert.deepEqual(parseIdeWidth('{"width":999,"full":false}'), { width: 360, full: false });
  assert.deepEqual(parseIdeWidth('{"width":1,"full":false}'), { width: 220, full: false });
  assert.deepEqual(parseIdeWidth('{"width":300.6,"full":false}'), { width: 301, full: false });
  assert.deepEqual(parseIdeWidth('{"width":300,"full":true}'), { width: 300, full: true });
  assert.deepEqual(parseIdeWidth('{"width":"300","full":true}'), { width: 272, full: false });
  assert.deepEqual(resizeIdeWidth({ width: 272, full: true }, 200), { width: 360, full: false });
  assert.deepEqual(resizeIdeWidth({ width: 272, full: false }, -200), { width: 220, full: false });
  assert.equal(persistIdeWidth(() => { throw new Error("storage denied"); }, { width: 272, full: true }), false);
  let saved = "legacy";
  assert.equal(persistIdeWidth(value => { saved = value; }, { width: 304, full: true }), true);
  assert.equal(saved, '{"width":304,"full":true}');
});
