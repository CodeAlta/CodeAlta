import assert from "node:assert/strict";
import test from "node:test";
import { hasFreeSeat, loadParts, parseSessionLimit, queuedLabel, queuedSince } from "./seats";
import { translate } from "../localization";

test("the limit of a machine is a number from 1 to 256, or empty for none", () => {
  assert.equal(parseSessionLimit(""), null);
  assert.equal(parseSessionLimit(" 3 "), 3);
  assert.equal(parseSessionLimit("256"), 256);
  for (const wrong of ["0", "257", "-1", "2.5", "three"]) assert.equal(parseSessionLimit(wrong), "invalid", wrong);
});

test("the load of a machine reads as its seats taken of its limit, then the runs that wait", () => {
  const say = (load: Parameters<typeof loadParts>[0]) => loadParts(load)?.map(part => translate("en", part.key, part.vars)).join(", ") ?? null;
  assert.equal(say({ working: 3, queued: 2, sessionLimit: 3 }), "3 of 3 at work, 2 queued");
  assert.equal(say({ working: 1, queued: 0, sessionLimit: null }), "1 at work");
  assert.equal(say({ working: null, queued: null, sessionLimit: null }), null, "an older machine says nothing");
  assert.equal(hasFreeSeat({ working: 2, queued: 0, sessionLimit: 3 }), true);
  assert.equal(hasFreeSeat({ working: 3, queued: 0, sessionLimit: 3 }), false);
  assert.equal(hasFreeSeat({ working: 1, queued: 1, sessionLimit: 3 }), false, "a run that waits goes first");
  assert.equal(hasFreeSeat({ working: 9, queued: 4, sessionLimit: null }), true);
});

test("a queued session names its machine and how many wait before it, in every language", () => {
  const next = queuedLabel("dgx-spark", 1);
  const third = queuedLabel("dgx-spark", 3);
  assert.equal(translate("en", next.key, next.vars), "Queued on dgx-spark (next)");
  assert.equal(translate("en", third.key, third.vars), "Queued on dgx-spark (2 before it)");
  for (const language of ["es", "fr", "de", "ja", "zh-CN"] as const) assert.match(translate(language, third.key, third.vars), /dgx-spark.*2|2.*dgx-spark/u, language);
});

test("a run that waits on the page of its machine says what it is and since when, in every language", () => {
  const at = "2026-11-10T07:21:00Z";
  const time = new Date(at).toLocaleString("en", { timeStyle: "short" });
  const say = (item: Parameters<typeof queuedSince>[0]) => { const since = queuedSince(item, "en"); return translate("en", since.key, since.vars); };
  assert.equal(say({ title: "SharpYaml", kind: "send", queuedAt: at }), `Prompt, queued at ${time}`);
  assert.equal(say({ title: "Nightly report", kind: "automation", queuedAt: at }), `Automation run, queued at ${time}`);
  assert.equal(say({ title: null, kind: "automation", queuedAt: at }), `Queued at ${time}`, "the title of a run without one already says what it is");
  for (const language of ["es", "fr", "de", "ja", "zh-CN"] as const) {
    const since = queuedSince({ title: "SharpYaml", kind: "send", queuedAt: at }, language);
    assert.notEqual(translate(language, since.key, since.vars), translate("en", since.key, since.vars), language);
  }
});
