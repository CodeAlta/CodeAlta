import assert from "node:assert/strict";
import test from "node:test";
import { cacheCountdown, cacheCountdownTick, reasoningShown, ringDash, shortStatusText, signInEnd } from "./promptSurface";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DraftUsage, SignInEnd, surfaceDensity } from "./PromptSurfaceParts";
import { mergeUsageObservation, persistedCacheExpiry, persistedSignInExpiry } from "../contextUsage";
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

test("the end of a sign-in shows in its last week, in days, hours or minutes, and in the danger colour in its last day", () => {
  assert.equal(signInEnd(null, at), null);
  assert.equal(signInEnd("not a time", at), null);
  assert.equal(signInEnd("2026-10-24T10:00:00Z", at), null, "further than a week: nothing is shown");
  assert.deepEqual(signInEnd("2026-10-18T13:00:00Z", at), { state: "soon", text: "3d", remainingMilliseconds: (3 * 24 + 3) * 3_600_000 });
  assert.equal(signInEnd("2026-10-16T22:00:00Z", at)?.text, "36h");
  assert.deepEqual(signInEnd("2026-10-15T15:30:00Z", at), { state: "ending", text: "5h", remainingMilliseconds: 5.5 * 3_600_000 });
  assert.equal(signInEnd("2026-10-15T10:00:20Z", at)?.text, "1m");
  assert.deepEqual(signInEnd("2026-10-15T09:00:00Z", at), { state: "ended", text: "", remainingMilliseconds: 0 });
  // The usage window says it whenever it is known.
  assert.equal(signInEnd("2026-11-14T10:00:00Z", at, Number.POSITIVE_INFINITY)?.text, "30d");
});

test("the end of the sign-in is read from a saved usage record, and kept across the events that do not say it", () => {
  assert.equal(persistedSignInExpiry("**Context:** 1 / 2 tokens\r\n\n**Sign-in expires:** 2026-11-24T03:01:53.1590000+00:00\r\n"), "2026-11-24T03:01:53.1590000+00:00");
  assert.equal(persistedSignInExpiry("**Sign-in expires:** soon"), null);
  const base = { sequence: "1", scope: "Session", source: "LocalProviderUsage", sourceUpdatedAt: null, eventTimestamp: null, hadInvalidValues: false, hadOmittedData: false,
    window: null, lastOperation: null, rateLimits: null, sessionTotal: null } as unknown as SessionUsageObservation;
  const said = { ...base, signInExpiresAt: "2026-11-24T03:01:53Z" } as SessionUsageObservation;
  assert.equal(mergeUsageObservation(said, { ...base, signInExpiresAt: null } as SessionUsageObservation).signInExpiresAt, "2026-11-24T03:01:53Z");
  assert.equal(mergeUsageObservation(said, { ...base, signInExpiresAt: "2026-12-24T03:01:53Z" } as SessionUsageObservation).signInExpiresAt, "2026-12-24T03:01:53Z");
});

test("the prompt warns of the end of the sign-in in its last week only", () => {
  const inDays = (days: number) => new Date(Date.now() + days * 24 * 3_600_000).toISOString();
  assert.equal(renderToStaticMarkup(createElement(SignInEnd, { expiresAt: inDays(20), provider: "Claude Code" })), "");
  assert.equal(renderToStaticMarkup(createElement(SignInEnd, { expiresAt: null, provider: "Claude Code" })), "");
  const soon = renderToStaticMarkup(createElement(SignInEnd, { expiresAt: inDays(2.5), provider: "Claude Code" }));
  assert.match(soon, /class="sign-in-end" data-state="soon"/);
  assert.match(soon, /<span>2d<\/span>/);
  assert.match(soon, /The sign-in of Claude Code ends in 2d/);
  assert.match(renderToStaticMarkup(createElement(SignInEnd, { expiresAt: inDays(-1), provider: "Claude Code" })), /data-state="ended"/);
});

test("a session that has not started shows its context meter only where the window of its model is known", () => {
  assert.equal(renderToStaticMarkup(createElement(DraftUsage, { contextTokens: null })), "");
  assert.equal(renderToStaticMarkup(createElement(DraftUsage, { contextTokens: 0 })), "");
  const known = renderToStaticMarkup(createElement(DraftUsage, { contextTokens: 200_000 }));
  assert.match(known, /class="context-usage-tokens">0\/200k</);
  assert.match(known, /aria-label="Context usage: 0% · 0\/200k"/);
});

test("a model whose efforts are all listed shows no effort it does not have; another keeps the saved one", () => {
  const haiku = { name: "Haiku 4.5", efforts: [] as string[], effortsComplete: true };
  const opus = { name: "Opus 5.5", efforts: ["Low", "High"], effortsComplete: true };
  assert.deepEqual(reasoningShown("Low", haiku), { effort: null, note: "none" });
  assert.deepEqual(reasoningShown(null, haiku), { effort: null, note: "none" });
  assert.deepEqual(reasoningShown("High", opus), { effort: "High", note: null });
  assert.deepEqual(reasoningShown("Medium", opus), { effort: null, note: "unused" });
  // A provider that says nothing of its list may still send the effort: it is shown as saved.
  assert.deepEqual(reasoningShown("Low", { name: "GPT", efforts: [] }), { effort: "Low", note: null });
  assert.deepEqual(reasoningShown("Low", undefined), { effort: "Low", note: null });
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
