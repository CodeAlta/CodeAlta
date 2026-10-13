import assert from "node:assert/strict";
import test from "node:test";
import { defaultTimelineEmphasis } from "./timelineEmphasis";
import { createTimelineEmphasisStore } from "./timelineEmphasisStore";

test("the first activation saves the preset, while restoring and re-enabling preserve saved choices", () => {
  let saved: string | null = null, writes = 0;
  const store = createTimelineEmphasisStore(() => saved, value => { saved = value; writes++; });
  assert.equal(writes, 0, "opening settings does not write the preset");
  assert.equal(store.getSnapshot().enabled, false);
  store.update({ ...store.getSnapshot(), enabled: true });
  assert.deepEqual(JSON.parse(saved!).types, defaultTimelineEmphasis.types);
  assert.equal(writes, 1);
  const legacy = { dim: true, opacity: 35, hoverOpacity: 80 };
  saved = JSON.stringify({ enabled: false, types: { tool: legacy } });
  const reopened = createTimelineEmphasisStore(() => saved, value => { saved = value; writes++; });
  assert.equal(writes, 1, "restoration neither overwrites nor migrates saved choices");
  for (const enabled of [true, false, true]) {
    reopened.update({ ...reopened.getSnapshot(), enabled });
    assert.deepEqual(reopened.getSnapshot().types.tool, legacy);
    assert.deepEqual(JSON.parse(saved!).types.tool, legacy);
  }
});

test("one window reads once and keeps a failed write for current and later subscribers", () => {
  let reads = 0, writes = 0, notifications = 0;
  const store = createTimelineEmphasisStore(() => { reads++; return null; }, () => { writes++; throw new Error("storage unavailable"); });
  const unsubscribe = store.subscribe(() => { notifications++; });
  const next = { ...defaultTimelineEmphasis, enabled: true };
  store.update(next);
  assert.equal(store.getSnapshot(), next);
  assert.deepEqual([reads, writes, notifications], [1, 1, 1]);
  unsubscribe();
  const later = store.subscribe(() => { notifications++; });
  assert.equal(store.getSnapshot(), next);
  store.update(next);
  assert.deepEqual([reads, writes, notifications], [1, 1, 1], "an unchanged snapshot needs no write or notification");
  store.update(defaultTimelineEmphasis);
  assert.deepEqual([reads, writes, notifications], [1, 2, 2]);
  later();
  store.update(next);
  assert.equal(notifications, 2, "unmounted consumers are no longer notified");
});

test("a new window restores the last successful write, not another window's unpersisted preference", () => {
  let kept: string | null = null, fail = false;
  const store = createTimelineEmphasisStore(() => kept, value => { if (fail) throw new Error("full"); kept = value; });
  const saved = { ...defaultTimelineEmphasis, enabled: true };
  store.update(saved);
  fail = true;
  store.update(defaultTimelineEmphasis);
  assert.equal(store.getSnapshot(), defaultTimelineEmphasis);
  const reopened = createTimelineEmphasisStore(() => kept, () => {});
  assert.deepEqual(reopened.getSnapshot(), saved);
  assert.equal(store.getSnapshot(), defaultTimelineEmphasis);
});

test("unavailable storage reads still give a window a usable in-memory preference", () => {
  const store = createTimelineEmphasisStore(() => { throw new Error("unavailable"); }, () => { throw new Error("unavailable"); });
  assert.equal(store.getSnapshot(), defaultTimelineEmphasis);
  const next = { ...defaultTimelineEmphasis, enabled: true };
  store.update(next);
  assert.equal(store.getSnapshot(), next);
});

test("a failed write remains visible to later consumers until a change saves successfully", () => {
  let fail = true, notifications = 0;
  const store = createTimelineEmphasisStore(() => null, () => { if (fail) throw new Error("full"); });
  const unsubscribe = store.subscribe(() => { notifications++; });
  assert.equal(store.getIssueSnapshot(), undefined);
  const next = { ...defaultTimelineEmphasis, enabled: true };
  store.update(next);
  assert.equal(store.getSnapshot(), next, "the failed save still applies in this window");
  assert.equal(store.getIssueSnapshot(), "unsaved");
  assert.equal(notifications, 1);
  unsubscribe();
  assert.equal(store.getIssueSnapshot(), "unsaved", "unmounting consumers does not lose the notice");
  fail = false;
  store.update({ ...next });
  assert.equal(store.getIssueSnapshot(), undefined, "a successful save clears the notice");
});
