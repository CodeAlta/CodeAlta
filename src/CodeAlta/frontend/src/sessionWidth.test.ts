import assert from "node:assert/strict";
import test from "node:test";
import { clampSessionWidth, defaultSessionWidth, minimumSessionWidth, sessionWidthAt, sessionWidthsOf, validSessionWidth, withSessionWidth } from "./sessionWidth";

test("a width is a whole percentage between the least and the whole space", () => {
  assert.deepEqual([40, 55, 100].map(validSessionWidth), [true, true, true]);
  assert.deepEqual([39, 101, 55.5, Number.NaN].map(validSessionWidth), [false, false, false, false]);
  assert.deepEqual([12, 40.4, 69.6, 250, Number.NaN, Number.POSITIVE_INFINITY].map(clampSessionWidth), [minimumSessionWidth, 40, 70, defaultSessionWidth, defaultSessionWidth, defaultSessionWidth]);
});

test("dragging an edge moves both: the conversation stays in the middle of its space", () => {
  // A space of 1000 from 200: its middle is at 700.
  assert.equal(sessionWidthAt(450, 200, 1000), 50, "the left edge at a quarter");
  assert.equal(sessionWidthAt(950, 200, 1000), 50, "the right edge at three quarters");
  assert.equal(sessionWidthAt(200, 200, 1000), 100, "the edge of the space");
  assert.equal(sessionWidthAt(120, 200, 1000), 100, "beyond the space");
  assert.equal(sessionWidthAt(700, 200, 1000), minimumSessionWidth, "the middle: the least width");
  assert.equal(sessionWidthAt(353, 200, 1000), 69);
  assert.equal(sessionWidthAt(10, 0, 0), defaultSessionWidth, "a space that has no width asks for nothing");
});

test("the sessions shown with a width of their own follow what the host says", () => {
  const listed = sessionWidthsOf([{ sessionId: "A-1", percent: 60 }, { sessionId: "b", percent: 20 }, { sessionId: "", percent: 70 }]);
  assert.deepEqual([...listed], [["a-1", 60]]);
  assert.deepEqual([...sessionWidthsOf(null)], []);
  const more = withSessionWidth(listed, "B", 45);
  assert.deepEqual([...more], [["a-1", 60], ["b", 45]]);
  assert.equal(withSessionWidth(more, "b", 45), more, "nothing changed: the same map, and no new render");
  // A width of 0 says the session follows the setting of the user again.
  assert.deepEqual([...withSessionWidth(more, "A-1", 0)], [["b", 45]]);
  assert.equal(withSessionWidth(listed, "unknown", 0), listed);
});
