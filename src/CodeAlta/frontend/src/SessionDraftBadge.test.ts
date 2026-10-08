import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionDraftBadge, SessionDraftStatus } from "./SessionDraftBadge";
import { createDraftIndicators } from "./promptDraft";

test("draft badge renders accessible icon and text without prompt content or an extra focus stop", () => {
  const html = renderToStaticMarkup(createElement("button", { type: "button", "aria-pressed": false },
    createElement("span", null, "⚠ Unverified lineage"), createElement(SessionDraftBadge, { active: true })));
  assert.match(html, /⚠ Unverified lineage/);
  assert.match(html, /Edited draft/);
  assert.match(html, /aria-hidden="true"/);
  assert.doesNotMatch(html, /tabindex|draft content|title=|aria-label=/i);
  const inactive = renderToStaticMarkup(createElement("button", { type: "button" }, createElement(SessionDraftBadge, { active: false })));
  assert.doesNotMatch(inactive, /Edited draft|prompt/);
});

test("the badge of a session follows the indicators by itself, for the selected session and for another one", () => {
  const indicators = createDraftIndicators();
  const render = (sessionId: string, selectedId: string | null) =>
    renderToStaticMarkup(createElement(SessionDraftStatus, { indicators, sessionId, selectedId }));
  assert.doesNotMatch(render("one", "one"), /Edited draft/);
  const edit = indicators.edit("one", "draft", "");
  assert.match(render("one", "one"), /Edited draft/);
  // Another session shows the mark only once the edit is kept.
  assert.doesNotMatch(render("one", "two"), /Edited draft/);
  indicators.persisted("one", edit, true);
  assert.match(render("one", "two"), /Edited draft/);
  indicators.clear("one");
  assert.doesNotMatch(render("one", "one"), /Edited draft/);
});

test("typing in the selected session never changes what its badge follows", () => {
  const indicators = createDraftIndicators();
  const shown: boolean[] = [];
  // What useDraftIndicator reads at each publication: a component is rendered again only when it differs.
  const unsubscribe = indicators.subscribe(() => shown.push(indicators.visible("one", "one")));
  for (const text of ["d", "dr", "dra", "draf", "draft"]) indicators.persisted("one", indicators.edit("one", text, ""), true);
  unsubscribe();
  assert.ok(shown.length >= 5);
  assert.ok(shown.every(value => value));
});
