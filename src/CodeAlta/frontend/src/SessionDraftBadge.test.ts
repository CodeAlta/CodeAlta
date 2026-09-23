import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionDraftBadge } from "./SessionDraftBadge";

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
