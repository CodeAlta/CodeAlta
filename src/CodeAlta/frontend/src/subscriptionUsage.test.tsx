import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { ProviderUsageResponse } from "#neoastra";
import { locales, translate } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";
import { elapsedPercent, hasSubscriptionUsage, limitAmount, limitIntent, limitLabel, limitsMarkdown, planLabel,
  resetMoment, sessionLimits, timeLeft, withLiveWindows, type UsageLimit } from "./subscriptionUsage";
import { UsageLimitList, usageNote } from "./UsageLimits";

const now = new Date("2026-10-08T09:00:00Z").getTime();
const limit = (change: Partial<UsageLimit> = {}): UsageLimit => ({ id: "five_hour", name: null, usedPercent: 40, resetsAt: "2026-10-08T11:00:00Z", windowMinutes: 300,
  used: null, total: null, unit: null, unlimited: false, remaining: null, currency: null, ...change });
const never = () => assert.fail("rendering must not act");
const render = (limits: UsageLimit[], locale: typeof locales[number] = "en") => renderToStaticMarkup(
  createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } }, createElement(UsageLimitList, { limits, now })));

test("a limit is named after its window, or after what the provider counts", () => {
  assert.deepEqual(limitLabel(limit()), { key: "5-hour limit", scope: null });
  assert.deepEqual(limitLabel(limit({ windowMinutes: 299 })), { key: "5-hour limit", scope: null });
  assert.equal(limitLabel(limit({ windowMinutes: 1440 })).key, "Daily limit");
  assert.deepEqual(limitLabel(limit({ id: "seven_day:opus", name: "Opus", windowMinutes: 10080 })), { key: "Weekly limit", scope: "Opus" });
  assert.equal(limitLabel(limit({ windowMinutes: 31 * 24 * 60 })).key, "Monthly limit");
  assert.equal(limitLabel(limit({ windowMinutes: 45 })).key, "Usage limit");
  assert.equal(limitLabel(limit({ windowMinutes: null })).key, "Usage limit");
  // A quota is named after what it counts, whatever the length of its period.
  assert.equal(limitLabel(limit({ id: "premium_interactions", unit: "requests", windowMinutes: 44640 })).key, "Premium requests");
  assert.equal(limitLabel(limit({ id: "premium_interactions", unit: "credits" })).key, "AI credits");
  assert.equal(limitLabel(limit({ id: "chat" })).key, "Chat");
  assert.equal(limitLabel(limit({ id: "completions" })).key, "Code completions");
  assert.equal(limitLabel(limit({ id: "extra_usage" })).key, "Usage credits");
  assert.equal(limitLabel(limit({ id: "credits" })).key, "Credits");
  for (const type of ["codex", "copilot", "claude-code"]) assert.equal(hasSubscriptionUsage(type), true);
  for (const type of ["xai", "openai-chat", "", null, undefined]) assert.equal(hasSubscriptionUsage(type), false);
});

test("the time before a limit starts over, how far its period has gone, and its color", () => {
  assert.equal(timeLeft("2026-10-08T09:09:00Z", now), "9 min");
  assert.equal(timeLeft("2026-10-08T11:14:00Z", now), "2 h 14 min");
  assert.equal(timeLeft("2026-10-08T12:00:00Z", now), "3 h");
  assert.equal(timeLeft("2026-10-09T13:00:00Z", now), "1 d 4 h");
  assert.equal(timeLeft("2026-10-12T05:00:00Z", now), "4 d");
  assert.equal(timeLeft("2026-11-01T00:00:00Z", now), "24 d");
  assert.equal(timeLeft("2026-10-08T08:00:00Z", now), null, "A limit that started over has nothing left to wait for.");
  assert.equal(timeLeft(null, now), null);
  assert.equal(timeLeft("not a date", now), null);

  assert.equal(elapsedPercent(limit(), now), 60, "Two of the five hours are left.");
  assert.equal(elapsedPercent(limit({ resetsAt: "2026-10-08T14:00:00Z" }), now), 0);
  assert.equal(elapsedPercent(limit({ resetsAt: "2026-10-08T15:00:00Z" }), now), null, "An end beyond the length of the window says nothing.");
  assert.equal(elapsedPercent(limit({ windowMinutes: null }), now), null);
  assert.equal(elapsedPercent(limit({ resetsAt: null }), now), null);

  assert.deepEqual([null, 10, 74.9, 75, 89, 90, 140].map(limitIntent), ["none", "none", "none", "warning", "warning", "danger", "danger"]);
  assert.equal(planLabel("individual_max"), "Individual max");
  assert.equal(planLabel("pro"), "Pro");
  assert.equal(planLabel(" "), null);
  assert.equal(planLabel(null), null);

  // The time today, the day this week, the date after.
  const sameDay = new Date(now + 2 * 3600 * 1000).toISOString(), inDays = new Date(now + 4 * 24 * 3600 * 1000).toISOString();
  assert.match(resetMoment(sameDay, now, "en-US")!, /^\d{1,2}:\d{2}/);
  assert.match(resetMoment(inDays, now, "en-US")!, /^(Sun|Mon|Tues|Wednes|Thurs|Fri|Satur)day \d{1,2}:\d{2}/);
  assert.match(resetMoment("2026-11-01T00:00:00Z", now, "en-US")!, /^(October 31|November 1)$/, "A billing period ends on a day.");
  assert.equal(resetMoment(null, now), null);
});

test("the limits a session reported are its two windows, which bring the answer of the provider up to date", () => {
  const live = sessionLimits({ name: "five_hour", planType: null, primary: { usedPercent: 6, resetsAt: "2026-10-08T11:00:00Z", windowDurationMinutes: "300" },
    secondary: { usedPercent: 52, resetsAt: "2026-10-12T05:00:00Z", windowDurationMinutes: "10080" } });
  assert.deepEqual(live.map(value => [value.id, value.usedPercent, value.windowMinutes]), [["session:primary", 6, 300], ["session:secondary", 52, 10080]]);
  assert.deepEqual(live.map(value => limitLabel(value).key), ["5-hour limit", "Weekly limit"]);
  assert.deepEqual(sessionLimits(null), []);
  assert.deepEqual(sessionLimits({ name: null, planType: null, primary: { usedPercent: null, resetsAt: null, windowDurationMinutes: null }, secondary: null }), []);
  assert.equal(sessionLimits({ name: null, planType: null, primary: { usedPercent: 3, resetsAt: null, windowDurationMinutes: "many" }, secondary: null })[0].windowMinutes, null);

  // The provider answers more than the two windows: the limit of a model and the credits spent stay as it said them.
  const week = limit({ id: "seven_day", usedPercent: 50, windowMinutes: 10080, resetsAt: "2026-10-12T04:59:58.7Z" });
  const fable = limit({ id: "seven_day:fable", name: "Fable", usedPercent: 66, windowMinutes: 10080, resetsAt: "2026-10-12T04:59:59.7Z" });
  const spent = limit({ id: "extra_usage", usedPercent: null, windowMinutes: null, resetsAt: null, used: 20.99, currency: "EUR" });
  const merged = withLiveWindows([limit({ usedPercent: 5 }), week, fable, spent], live);
  assert.deepEqual(merged.map(value => [value.id, value.usedPercent]), [["five_hour", 6], ["seven_day", 52], ["seven_day:fable", 66], ["extra_usage", null]],
    "A turn that ran since the answer says more use of the same period.");
  assert.equal(merged[1].resetsAt, week.resetsAt);
  assert.deepEqual([merged[2], merged[3]], [fable, spent]);
  assert.equal(withLiveWindows([limit({ usedPercent: 9 })], live)[0].usedPercent, 9, "The use of a period only grows: the larger figure is the newer one.");
  const startedOver = sessionLimits({ name: null, planType: null, primary: { usedPercent: 1, resetsAt: "2026-10-08T16:00:00Z", windowDurationMinutes: "300" }, secondary: null });
  assert.deepEqual(withLiveWindows([limit({ usedPercent: 97 })], startedOver).map(value => [value.usedPercent, value.resetsAt]), [[1, "2026-10-08T16:00:00Z"]],
    "A window that ends later has started over since the answer.");
  assert.equal(withLiveWindows([limit({ usedPercent: 2, resetsAt: "2026-10-08T16:00:00Z" })], live)[0].usedPercent, 2, "A figure of the period before says nothing of this one.");
  assert.equal(withLiveWindows([limit({ usedPercent: 2, resetsAt: null })], live)[0].usedPercent, 6);
  assert.deepEqual(withLiveWindows([week], []), [week]);
  assert.deepEqual(withLiveWindows([], live), []);
});

test("the limits are meters with what is counted and when they start over, in every language", () => {
  const limits = [limit({ usedPercent: 6 }), limit({ id: "seven_day", usedPercent: 92, windowMinutes: 10080, resetsAt: "2026-10-12T05:00:00Z" }),
    limit({ id: "seven_day:opus", name: "Opus", usedPercent: 140, windowMinutes: 10080, resetsAt: null }),
    limit({ id: "premium_interactions", usedPercent: 23, used: 4678, total: 20000, unit: "credits", windowMinutes: 44640, resetsAt: "2026-11-01T00:00:00Z" }),
    limit({ id: "chat", usedPercent: null, unlimited: true, windowMinutes: null, resetsAt: null }),
    limit({ id: "credits", usedPercent: null, remaining: 62500, unit: "credits", windowMinutes: null, resetsAt: null }),
    limit({ id: "extra_usage", usedPercent: null, used: 20.99, currency: "EUR", windowMinutes: null, resetsAt: null })];
  for (const locale of locales) {
    const html = render(limits, locale);
    for (const key of ["5-hour limit", "Weekly limit", "AI credits", "Chat", "Credits", "Usage credits", "Unlimited"] as const)
      assert.ok(html.includes(`>${translate(locale, key).replace(/'/g, "&#x27;")}`), `${locale}: ${key}`);
    assert.ok(html.includes(`<strong>${translate(locale, "{amount} spent", { amount: limitAmount(20.99, "EUR", locale) })}</strong>`), `${locale}: money spent without a limit is the figure of its row`);
    assert.ok(html.includes(translate(locale, "{percent}% used", { percent: 6 })), locale);
    assert.ok(html.includes(translate(locale, "Resets in {time}", { time: "2 h" })), locale);
    assert.ok(html.includes("<small>Opus</small>"), "A limit of a model names it.");
    assert.equal(html.split('role="meter"').length - 1, 4, "A limit without a share of use has no meter.");
  }

  const html = render(limits);
  assert.ok(html.includes('data-intent="danger"') && html.includes('data-intent="none"'));
  assert.ok(html.includes('aria-valuenow="100"') && html.includes("width:100%"), "A limit that is passed fills its meter and still says by how much.");
  assert.ok(html.includes("140% used"));
  assert.ok(html.includes("4,678 of 20,000 credits"));
  assert.ok(html.includes(">62,500 left<"), "A balance of credits is named by its row.");
  assert.ok(html.includes(">€20.99 spent<"), "Money is written in its currency, with its cents.");
  // With a monthly limit, the credits spent are a share of it, and the amounts say of how much.
  const capped = render([limit({ id: "extra_usage", usedPercent: 30, used: 15, total: 50, currency: "EUR", windowMinutes: null, resetsAt: null })]);
  assert.ok(capped.includes("30% used") && capped.includes(">€15.00 of €50.00<") && !capped.includes("spent"));
  assert.ok(render([limit({ id: "credits", usedPercent: null, remaining: 19.78, currency: "EUR", windowMinutes: null, resetsAt: null })]).includes(">€19.78 left<"));
  assert.equal(limitAmount(1234.5, null, "en-US"), "1,235");
  assert.equal(limitAmount(1234.5, "not a code", "en-US"), "1,235", "What is not a currency code is not given to the formatter, which would throw.");
  assert.ok(html.includes("left:60%"), "The mark says how far the five hours have gone.");
  assert.equal(render([]), '<div class="subscription-limits"></div>');
});

test("why a provider shows no usage, and the copy of the usage as Markdown", () => {
  const response = (status: string, tool: string | null = null): ProviderUsageResponse => ({ status, key: "codex", supported: true, plan: null, limits: [], observedAt: null, tool });
  assert.equal(usageNote(null), null);
  assert.equal(usageNote(response("ok")), null);
  assert.deepEqual(usageNote(response("signed_out")), { key: "Sign in to see the usage." });
  assert.deepEqual(usageNote(response("not_available")), { key: "No usage is reported for this account." });
  assert.deepEqual(usageNote(response("failed")), { key: "The usage could not be read." });
  assert.deepEqual(usageNote(response("unknown_provider")), { key: "The usage could not be read." });
  assert.equal(usageNote(response("stale_epoch")), null, "A page of another host says so by itself.");
  // Codex and Claude Code are asked through their own program: the note names the one that is missing.
  assert.deepEqual(usageNote(response("tool_missing", "Codex CLI")), { key: "{tool} is not installed on this computer. Install it to see the usage.", tool: "Codex CLI" });
  assert.deepEqual(usageNote(response("tool_signed_out", "Codex CLI")), { key: "Sign in to {tool} with this account to see the usage.", tool: "Codex CLI" });
  assert.deepEqual(usageNote(response("tool_missing")), { key: "No usage is reported for this account." });
  for (const locale of locales) assert.ok(translate(locale, "{tool} is not installed on this computer. Install it to see the usage.", { tool: "Claude Code" }).includes("Claude Code"), locale);

  assert.deepEqual(limitsMarkdown([], "pro", now), []);
  assert.deepEqual(limitsMarkdown([limit(), limit({ id: "premium_interactions", usedPercent: 23, used: 4678, total: 20000, unit: "credits", resetsAt: null }),
    limit({ id: "seven_day:opus", name: "Opus", windowMinutes: 10080, usedPercent: null, unlimited: true, resetsAt: null }),
    limit({ id: "extra_usage", windowMinutes: null, usedPercent: null, resetsAt: null, used: 20.99, currency: "EUR" }),
    limit({ id: "extra_usage", windowMinutes: null, usedPercent: 30, resetsAt: null, used: 15, total: 50, currency: "USD" })], "max", now),
  ["", "## Subscription usage", "", "- Plan: max", "- 5-hour limit: 40% used · resets in 2 h", "- AI credits: 23% used · 4,678 of 20,000 credits", "- Weekly limit (Opus): unlimited",
    "- Usage credits: €20.99 spent", "- Usage credits: 30% used · $15.00 of $50.00"]);
});
