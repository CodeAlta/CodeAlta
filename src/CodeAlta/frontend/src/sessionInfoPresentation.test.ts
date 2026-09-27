import test from "node:test";
import assert from "node:assert/strict";
import { canonicalInfoCopy, infoFieldLabel } from "./sessionInfoPresentation";
import { locales, translate } from "./localization";
import type { SessionInfoView } from "./sessionInfo";

test("inspection translations have six-locale labels while canonical Copy keeps literal data and bounds", () => {
  const info: SessionInfoView = { id: "Unknown", title: "Saved metadata", titleTruncated: true, scope: "Global",
    scopeWarning: "literal warning", path: "/Saved metadata/<file>", provider: "Unknown", updatedAt: null, createdAt: null, canCopyId: true };
  const observations = { runtime: [["Observation", "raw backend error <literal>"], ["Observed model / reasoning", "Unknown / High"]] as const,
    usage: [["Observation", "no_observation"]] as const };
  const canonical = canonicalInfoCopy(info, false, observations)!;
  assert.ok(canonical.includes("Session ID\nUnknown\nTitle\nSaved metadata\nTitle shortened in the bounded snapshot."));
  assert.ok(canonical.includes("raw backend error <literal>"));
  assert.ok(canonical.includes("Unknown / High"));
  assert.ok(canonical.includes("/Saved metadata/<file>"));
  assert.ok(canonical.includes("Scope\nGlobal\nliteral warning"));
  for (const locale of locales) {
    for (const key of ["Browse saved sessions", "Session info", "Last-observed usage", "Copy displayed details", "Refresh statuses"] as const) {
      assert.ok(translate(locale, key));
      if (locale !== "en") assert.notEqual(translate(locale, key), key);
    }
    assert.equal(canonicalInfoCopy(info, false, observations), canonical);
  }
  assert.equal(infoFieldLabel("Observed model / reasoning"), "Observed model / reasoning");
  assert.equal(infoFieldLabel("free-form backend label"), null);
  assert.equal(canonicalInfoCopy({ ...info, title: "x".repeat(32769) }, false, observations), null);
});
