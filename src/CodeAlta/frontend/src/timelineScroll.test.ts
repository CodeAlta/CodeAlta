import test from "node:test";
import assert from "node:assert/strict";
import { bottomScrollTop, distanceFromBottom, preservePrependScrollTop, shouldFollowTimeline } from "./timelineScroll";

test("timeline follows initial and near-bottom content but respects reading position", () => {
  assert.equal(bottomScrollTop({ scrollHeight: 1200, clientHeight: 400 }), 800);
  assert.equal(distanceFromBottom({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), 50);
  assert.equal(shouldFollowTimeline({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), true);
  assert.equal(shouldFollowTimeline({ scrollTop: 300, scrollHeight: 1200, clientHeight: 400 }), false);
});

test("prepending older content preserves the visible anchor", () => {
  assert.equal(preservePrependScrollTop({ scrollTop: 240, scrollHeight: 1000, clientHeight: 500 }, 1350), 590);
});
