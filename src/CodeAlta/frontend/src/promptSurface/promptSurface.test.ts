import assert from "node:assert/strict";
import test from "node:test";
import { cacheCountdown, cacheCountdownTick, ringDash, shortStatusText } from "./promptSurface";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DraftUsage, surfaceDensity } from "./PromptSurfaceParts";
import { mergeUsageObservation, persistedCacheExpiry } from "../contextUsage";
import type { SessionUsageObservation } from "#neoastra";

const at = Date.parse("2026-10-15T10:00:00Z");

test("the prompt cache counts down in minutes, warns in its last minutes and says when it expired", () => {
  assert.equal(cacheCountdown(null, at), null);
  assert.equal(cacheCountdown("not a time", at), null);
  assert.deepEqual(cacheCountdown("2026-10-15T10:37:00Z", at), { state: "live", text: "37m", remainingMilliseconds: 37 * 60_000 });
  // A part of a minute counts as a minute: "1m" is never shown for a cache that is gone.
  assert.equal(cacheCountdown("2026-10-15T10:00:20Z", at)?.text, "1m");
  assert.equal(cacheCountdown("2026-10-15T11:00:00Z", at)?.text, "1h");
  assert.equal(cacheCountdown("2026-10-15T11:05:00Z", at)?.text, "1h 5m");
  assert.equal(cacheCountdown("2026-10-15T10:03:00Z", at)?.state, "ending");
  assert.equal(cacheCountdown("2026-10-15T10:03:01Z", at)?.state, "live");
  assert.deepEqual(cacheCountdown("2026-10-15T09:59:00Z", at), { state: "expired", text: "expired", remainingMilliseconds: 0 });
  // It is drawn again every half minute, every few seconds near the end, and no more once expired.
  assert.equal(cacheCountdownTick(cacheCountdown("2026-10-15T10:37:00Z", at)), 30_000);
  assert.equal(cacheCountdownTick(cacheCountdown("2026-10-15T10:02:00Z", at)), 5_000);
  assert.equal(cacheCountdownTick(cacheCountdown("2026-10-15T09:00:00Z", at)), null);
});

test("the expiry of the prompt cache is read from a saved usage record, and live as the host gives it", () => {
  assert.equal(persistedCacheExpiry("**Context:** 1 / 2 tokens\r\n\n**Prompt cache expires:** 2026-10-15T10:37:00.0000000+00:00\r\n"), "2026-10-15T10:37:00.0000000+00:00");
  assert.equal(persistedCacheExpiry("**Prompt cache expires:** soon"), null);
  assert.equal(persistedCacheExpiry(null), null);
  const base = { sequence: "1", scope: "Session", source: "LocalProviderUsage", sourceUpdatedAt: null, eventTimestamp: null, hadInvalidValues: false, hadOmittedData: false,
    window: null, lastOperation: null, rateLimits: null, sessionTotal: null } as unknown as SessionUsageObservation;
  const written = { ...base, promptCacheExpiresAt: "2026-10-15T10:37:00Z" } as SessionUsageObservation;
  // The host keeps it across events that are not requests; a request that used no cache says it is no longer known.
  assert.equal(mergeUsageObservation(written, { ...base, promptCacheExpiresAt: null } as SessionUsageObservation).promptCacheExpiresAt, null);
  assert.equal(mergeUsageObservation(written, { ...base, promptCacheExpiresAt: "2026-10-15T11:00:00Z" } as SessionUsageObservation).promptCacheExpiresAt, "2026-10-15T11:00:00Z");
});

test("a session that has not started shows its context meter only where the window of its model is known", () => {
  assert.equal(renderToStaticMarkup(createElement(DraftUsage, { contextTokens: null })), "");
  assert.equal(renderToStaticMarkup(createElement(DraftUsage, { contextTokens: 0 })), "");
  const known = renderToStaticMarkup(createElement(DraftUsage, { contextTokens: 200_000 }));
  assert.match(known, /class="context-usage-tokens">0\/200k</);
  assert.match(known, /aria-label="Context usage: 0% · 0\/200k"/);
});

test("the surface shortens its labels as it narrows, in a fixed order", () => {
  assert.deepEqual([1600, 1040, 1039, 780, 779, 540, 539, 320, 0].map(surfaceDensity),
    ["wide", "wide", "medium", "medium", "narrow", "narrow", "tiny", "tiny", "wide"]);
});

test("the ring fills with the share of the context window, and plugin texts keep their first part", () => {
  const circumference = 2 * Math.PI * 6;
  assert.equal(ringDash(50, 6), `${(circumference / 2).toFixed(2)} ${circumference.toFixed(2)}`);
  assert.equal(ringDash(150, 6), `${circumference.toFixed(2)} ${circumference.toFixed(2)}`);
  assert.equal(ringDash(null, 6), `0.00 ${circumference.toFixed(2)}`);
  assert.equal(shortStatusText("1/1 · tools not loaded"), "1/1");
  assert.equal(shortStatusText("idle"), "idle");
});
