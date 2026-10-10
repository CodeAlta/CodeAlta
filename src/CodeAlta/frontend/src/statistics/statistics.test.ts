import assert from "node:assert/strict";
import test from "node:test";
import { messages, translate, locales } from "../localization";
import { createFixtureApi } from "./fixtureApi";
import { filterChoices, ignoredKinds, unsetFilters } from "./filters";
import { createFormatter, dayOfNumber, etaParts, sentenceCase } from "./format";
import {
  addDays, addMonths, allowedFrequencies, autoFrequency, bucketCount, decodeFrame, defaultFrame, encodeFrame, filterOf, firstDayOfWeek, frameReducer, initialFrame, knownDays,
  localToday, periodText, requestOf, resolveFrequency, resolvePeriod, weekDayIndex, type Frame,
} from "./frame";
import { statisticsContext } from "./canvasContext";
import { assumedBytesPerSecond, canReadMore, historyView, progressOf, readMoreChoices, readingSeconds, skippedToRetry, timeLeft } from "./history";
import { distributionOption, hatchFraction, periodOfBrush, ratioSeries, timeSeriesOption, unreadBuckets } from "./options";
import { QueryStore, joinRanges } from "./queryStore";
import { binSteps, boxStatsOfSteps, percentileOfSteps, stepsCount } from "./steps";
import type { DistributionStep, SeriesResult, StatisticsStatus } from "./types";
import { mcpParts, combineSeries } from "./pages/shared";

const today = "2026-10-09";
const fmt = createFormatter("en", { credits: amount => `${amount} AI credits`, none: "–" });
const status = (patch: Partial<StatisticsStatus>): StatisticsStatus => ({ state: "done", sessionsTotal: 0, sessionsDone: 0, bytesTotal: 0, bytesDone: 0, skippedCount: 0, skipped: [], pendingFlow: 0, revision: 0, ...patch });

test("periods resolve to local days, and the lengths the bar needs are known", () => {
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "today" }, today), { from: today, to: today });
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "7d" }, today), { from: "2026-10-03", to: today });
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "30d" }, today), { from: "2026-09-10", to: today });
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "month" }, today), { from: "2026-10-01", to: today });
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "last-month" }, today), { from: "2026-09-01", to: "2026-09-30" });
  assert.deepEqual(resolvePeriod({ kind: "preset", preset: "year" }, today), { from: "2026-01-01", to: today });
  assert.equal(resolvePeriod({ kind: "preset", preset: "all" }, today), null);
  assert.deepEqual(resolvePeriod({ kind: "custom", from: "2026-02-01", to: "2026-02-28" }, today), { from: "2026-02-01", to: "2026-02-28" });
  assert.equal(knownDays({ kind: "preset", preset: "all" }), null);
  assert.equal(knownDays({ kind: "preset", preset: "last-month" }, today), 30);
  assert.equal(knownDays({ kind: "custom", from: "2026-02-01", to: "2026-02-28" }), 28);
  assert.equal(periodText({ kind: "custom", from: "2026-02-01", to: "2026-02-28" }), "2026-02-01..2026-02-28");
  assert.equal(addMonths("2026-03-31", -1), "2026-02-28", "a month is clamped to its length");
  assert.equal(addDays("2026-12-31", 1), "2027-01-01");
  assert.match(localToday(new Date(2026, 9, 9, 23, 59)), /^2026-10-09$/);
});

test("auto frequency follows the plugin: hours for a day, days to 90, weeks to a year, months beyond", () => {
  assert.equal(autoFrequency(1), "hour");
  assert.equal(autoFrequency(2), "day");
  assert.equal(autoFrequency(90), "day");
  assert.equal(autoFrequency(91), "week");
  assert.equal(autoFrequency(366), "week");
  assert.equal(autoFrequency(367), "month");
  assert.equal(resolveFrequency("auto", 30), "day");
  assert.equal(resolveFrequency("auto", null), "day");
  assert.equal(resolveFrequency("week", 2), "week");
  assert.deepEqual(allowedFrequencies(1), ["hour", "day"]);
  assert.deepEqual(allowedFrequencies(30), ["hour", "day", "week", "month"]);
  assert.deepEqual(allowedFrequencies(400), ["day", "week", "month", "year"]);
  assert.deepEqual(allowedFrequencies(null), ["day", "week", "month", "year"]);
  assert.equal(bucketCount(30, "hour"), 720);
  assert.equal(bucketCount(30, "week"), 6);
});

test("the frame changes by actions, and an action that changes nothing returns the same frame", () => {
  const frame = defaultFrame;
  assert.equal(frameReducer(frame, { type: "page", page: "overview" }), frame);
  assert.equal(frameReducer(frame, { type: "comparison", comparison: "none" }), frame);
  assert.equal(frameReducer(frame, { type: "frequency", frequency: "auto" }), frame);
  assert.equal(frameReducer(frame, { type: "period", period: { kind: "preset", preset: "30d" } }), frame);
  const hourly = frameReducer(frame, { type: "frequency", frequency: "hour" });
  assert.equal(hourly.frequency, "hour");
  assert.equal(frameReducer(hourly, { type: "period", period: { kind: "preset", preset: "year" } }).frequency, "auto", "a frequency that no longer fits the period goes back to auto");
  assert.equal(frameReducer(hourly, { type: "period", period: { kind: "preset", preset: "today" } }).frequency, "hour");
  const filtered = frameReducer(frame, { type: "filter", key: "project", entry: { value: "p1", label: "CodeAlta" } });
  assert.deepEqual(filtered.filters, { project: { value: "p1", label: "CodeAlta" } });
  assert.equal(frameReducer(filtered, { type: "filter", key: "project", entry: { value: "p1", label: "CodeAlta" } }), filtered);
  assert.deepEqual(frameReducer(filtered, { type: "filter", key: "project", entry: null }).filters, {});
  assert.equal(frameReducer(frame, { type: "filter", key: "model", entry: null }), frame);
  assert.deepEqual(frameReducer(frame, { type: "view", view: { unit: "time" } }).view, { stackBy: "provider", unit: "time" });
  assert.equal(frameReducer(filtered, { type: "reset", frame }), frame);
});

test("the frame is a short query string that a reload reads back, and damaged text keeps the defaults", () => {
  const frame: Frame = { page: "cost", period: { kind: "custom", from: "2026-02-01", to: "2026-02-28" }, frequency: "week", comparison: "samePeriodLastYear",
    filters: { project: { value: "proj-1", label: "Code Alta & co" }, origin: { value: "agent" }, toolKind: { value: "shell" } }, view: { stackBy: "model", unit: "time" } };
  const text = encodeFrame(frame);
  assert.ok(text.length < 300);
  assert.deepEqual(decodeFrame(text), frame);
  assert.equal(decodeFrame(null), defaultFrame);
  assert.equal(decodeFrame(""), defaultFrame);
  const damaged = decodeFrame("page=nowhere&period=2026-13..x&freq=sometimes&cmp=big&origin=martian&toolKind=warp&unit=lightyears&stack=none");
  assert.deepEqual(damaged, { ...defaultFrame, filters: {} });
  assert.equal(decodeFrame("page=health").page, "health");
  assert.equal(decodeFrame("period=2026-03-01..2026-02-01").period.kind, "preset", "a range that ends before it starts is refused");
});

test("the request of a page is the frame: period, frequency, comparison, the filters that are set, the first day of the week", () => {
  const frame: Frame = { ...defaultFrame, period: { kind: "preset", preset: "last-month" }, frequency: "week", comparison: "previousPeriod",
    filters: { space: { value: "space-1" }, provider: { value: "claude" }, effort: { value: "high", label: "high" } } };
  assert.deepEqual(requestOf(frame, 1), { period: "last-month", frequency: "week", comparison: "previousPeriod", filter: { space: "space-1", provider: "claude", effort: "high" }, weekStart: "Monday" });
  assert.deepEqual(requestOf(defaultFrame, 0, { limit: 5, comparison: "none" }), { period: "30d", frequency: "auto", comparison: "none", weekStart: "Sunday", limit: 5 });
  assert.deepEqual(filterOf({}), {});
  const opened = initialFrame({ spaceId: "s", spaceName: "Work" });
  assert.deepEqual(opened.filters, { space: { value: "s", label: "Work" } });
  assert.deepEqual(initialFrame({ spaceId: "s", projectId: "p", projectName: "CodeAlta" }).filters, { project: { value: "p", label: "CodeAlta" } }, "a project is narrower than a space");
  assert.ok([0, 1, 6].includes(firstDayOfWeek("en-US")) && [0, 1, 6].includes(firstDayOfWeek("not a locale")));
});

test("the first day of the week is the one the plugin names, read from the name the host writes", () => {
  assert.equal(weekDayIndex("Sunday"), 0);
  assert.equal(weekDayIndex("monday"), 1);
  assert.equal(weekDayIndex("Saturday"), 6);
  assert.equal(weekDayIndex("Someday"), undefined);
  assert.equal(weekDayIndex(undefined), undefined);
  const alta = { context: { pluginKey: "builtin:statistics", canvasId: "statistics", instanceId: "i", spaceId: null, projectId: null, sessionId: null, key: null, input: null }, host: { openSession: () => { } } } as unknown as Parameters<typeof statisticsContext>[0];
  const directory = { spaces: [], projects: [] };
  assert.equal(statisticsContext(alta, true, { ...directory, weekStart: "Saturday" }).weekStart, 6);
  assert.equal(statisticsContext(alta, true, directory).weekStart, undefined, "a plugin that names none leaves the week of the language of the window");
  assert.equal(statisticsContext(alta, true, null).weekStart, undefined);
});

test("numbers, durations, costs and dates are written as the locale writes them", () => {
  assert.equal(fmt.duration(850), "850 ms");
  assert.equal(fmt.duration(4200), "4.2 s");
  assert.equal(fmt.duration(42_000), "42 s");
  assert.equal(fmt.duration(252_000), "4 min 12 s");
  assert.equal(fmt.duration(59_600), "1 min 00 s", "seconds round up into the minute");
  assert.equal(fmt.duration(3_900_000), "1 h 05");
  assert.equal(fmt.duration(Number.NaN), "–");
  assert.equal(fmt.compact(1_200_000), "1.2M");
  assert.equal(fmt.compact(950), "950");
  assert.equal(fmt.compact(12_300), "12.3K");
  assert.equal(fmt.bytes(1536), "1.5 KB");
  assert.equal(fmt.percent(0.425), "43%");
  assert.equal(fmt.percent(0.035), "3.5%");
  assert.equal(fmt.cost("usd", 1511.44), "$1,511.44");
  assert.equal(fmt.cost("AI credits", 8952), "8,952 AI credits");
  assert.equal(fmt.costShort("AI credits", 8952), "8,952");
  assert.equal(fmt.costShort("AI credits", 0.560457), "0.56", "a few credits are not rounded to a whole one");
  assert.equal(fmt.costShort("AI credits", 42.4), "42.4");
  assert.equal(fmt.costShort("usd", 1511.44), "$1,511");
  assert.equal(fmt.day("2026-04-20"), "Apr 20");
  assert.equal(fmt.range("2026-09-10", "2026-10-09"), "Sep 10 – Oct 9, 2026");
  assert.equal(fmt.range("2025-12-30", "2026-01-02"), "Dec 30, 2025 – Jan 2, 2026");
  assert.equal(fmt.bucket({ index: 0, start: "2026-10-09T14:00", label: "" }, "hour"), "14:00");
  assert.equal(fmt.bucket({ index: 0, start: "2026-10-01T00:00", label: "" }, "month"), "Oct");
  assert.equal(fmt.value("ratio", 0.5), "50%");
  assert.equal(fmt.value("tokens", 2_100_000_000), "2.1B");
  const french = createFormatter("fr", { credits: amount => `${amount} crédits`, none: "–" });
  assert.equal(french.number(1204).replace(/\s/g, " "), "1 204");
  assert.equal(french.dayLong("2026-04-20"), "20 avr. 2026");
  assert.equal(dayOfNumber(20260714), "2026-07-14");
  assert.equal(sentenceCase("about 40 seconds left.", "en"), "About 40 seconds left.");
  assert.equal(sentenceCase("", "en"), "");
  assert.deepEqual(etaParts(40), { unit: "second", count: 40 });
  assert.deepEqual(etaParts(3), { unit: "second", count: 5 });
  assert.deepEqual(etaParts(250), { unit: "minute", count: 4 });
  assert.deepEqual(etaParts(7300), { unit: "hour", count: 2 });
});

const series = (values: number[][], previous?: number[][]): SeriesResult => ({
  query: { period: "7d", from: "2026-10-03", to: "2026-10-09", frequency: "day", timeZone: "UTC", ...(previous ? { compareFrom: "2026-09-26", compareTo: "2026-10-02" } : {}),
    coverage: { complete: true, historyState: "done" }, ignoredFilters: [], notes: [] },
  metric: "tokens", unit: "tokens", group: "provider",
  buckets: Array.from({ length: 7 }, (_, index) => ({ index, start: `${addDays("2026-10-03", index)}T00:00`, label: "" })),
  series: values.map((line, index) => ({ key: `k${index}`, label: `Line ${index}`, values: line, ...(previous ? { previous: previous[index] } : {}), total: line.reduce((a, b) => a + b, 0), ...(previous ? { previousTotal: previous[index].reduce((a, b) => a + b, 0) } : {}) })),
});

test("a time chart stacks its lines, adds one dashed line for the compared period, and keeps the table and the plot box", () => {
  const result = series([[1, 2, 3, 4, 5, 6, 7], [2, 2, 2, 2, 2, 2, 2]], [[1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1]]);
  const built = timeSeriesOption({ result, kind: "bar", fmt, color: (_, index) => `#00000${index}`, previousName: "Previous period", otherName: "Other", muted: "#888888", brush: true });
  const lines = built.option.series as { name: string; type: string; stack?: string; data: number[]; color: string }[];
  assert.deepEqual(lines.map(line => line.name), ["Line 0", "Line 1", "Previous period"]);
  assert.deepEqual(lines.map(line => line.type), ["bar", "bar", "line"]);
  assert.equal(lines[0].stack, "total");
  assert.equal(lines[2].stack, undefined);
  assert.deepEqual(lines[2].data, [2, 2, 2, 2, 2, 2, 2]);
  assert.equal(lines[0].color, "#000000");
  assert.deepEqual((built.option.legend as { data: string[] }).data, ["Line 0", "Line 1", "Previous period"]);
  assert.equal(built.plot.bottom, 58, "room for the brush under the plot");
  assert.equal(built.boundaryGap, true);
  assert.deepEqual(built.table.columns, ["", "Line 0", "Line 1", "Previous period"]);
  assert.equal(built.table.rows.length, 7);
  assert.ok(Array.isArray((built.option as { dataZoom?: unknown[] }).dataZoom));
  const one = timeSeriesOption({ result: series([[1, 2, 3, 4, 5, 6, 7]]), kind: "line", fmt, color: () => "#111111", previousName: "p", otherName: "o", muted: "#888" });
  assert.equal(one.option.legend, undefined, "one line needs no legend");
  assert.equal(one.boundaryGap, false);
  assert.equal(one.plot.bottom, 30);
  assert.equal((one.option.series as { stack?: string }[])[0].stack, undefined, "lines never stack");
  const gaps = ratioSeries(series([[1, 2, 3, 0, 5, 6, 7]]), series([[2, 4, 0, 0, 10, 6, 7]]));
  assert.equal(gaps.unit, "ratio");
  assert.equal(gaps.series[0].values[0], 0.5);
  assert.ok(Number.isNaN(gaps.series[0].values[2]), "no input is a gap, not a zero");
  const drawn = timeSeriesOption({ result: gaps, kind: "line", fmt, color: () => "#111", previousName: "p", otherName: "o", muted: "#888" });
  assert.equal((drawn.option.series as { data: (number | null)[] }[])[0].data[2], null);
  const folded = timeSeriesOption({ result: { ...series([[1, 1, 1, 1, 1, 1, 1]]), series: [{ key: "other", label: "other", values: [1, 1, 1, 1, 1, 1, 1], total: 7 }] }, kind: "bar", fmt, color: () => "#111", previousName: "p", otherName: "Other", muted: "#888" });
  assert.equal((folded.option.series as { name: string; color: string }[])[0].name, "Other");
  assert.equal((folded.option.series as { name: string; color: string }[])[0].color, "#888", "other is muted");
});

test("what is not read yet is the first buckets before the day the numbers are complete from", () => {
  const buckets = series([[1, 2, 3, 4, 5, 6, 7]]).buckets;
  assert.equal(unreadBuckets(buckets, { complete: true, historyState: "done" }), 0);
  assert.equal(unreadBuckets(buckets, { complete: false, historyState: "reading", completeFrom: "2026-10-06" }), 3);
  assert.equal(unreadBuckets(buckets, { complete: false, historyState: "reading", completeFrom: "2026-10-03" }), 0);
  assert.equal(unreadBuckets(buckets, { complete: false, historyState: "needs-choice" }), 7, "nothing is read before the choice");
  assert.equal(unreadBuckets(buckets, { complete: false, historyState: "paused" }), 0, "no day to hatch from");
  assert.equal(hatchFraction(3, 7, true), 3 / 7);
  assert.equal(hatchFraction(3, 7, false), 0.5);
  assert.equal(hatchFraction(0, 7, true), 0);
  assert.equal(hatchFraction(9, 7, true), 1);
});

test("a brush on a time chart is the days its first and last bucket cover", () => {
  const result = series([[1, 2, 3, 4, 5, 6, 7]]);
  assert.equal(periodOfBrush(result, 0, 100), null, "the whole period is no change");
  assert.equal(periodOfBrush(result, 0, 50), "2026-10-03..2026-10-06");
  assert.equal(periodOfBrush(result, 50, 100), "2026-10-06..2026-10-09");
  assert.equal(periodOfBrush(result, 100 / 6, 100 / 6), "2026-10-04..2026-10-04");
  assert.equal(periodOfBrush({ ...result, buckets: [] }, 0, 50), null);
  const weekly: SeriesResult = { ...result, query: { ...result.query, frequency: "week", from: "2026-09-28", to: "2026-10-18" }, buckets: [0, 1, 2].map(index => ({ index, start: `${addDays("2026-09-28", index * 7)}T00:00`, label: "" })) };
  assert.equal(periodOfBrush(weekly, 0, 50), "2026-09-28..2026-10-11", "a week bucket ends the day before the next one starts");
  assert.equal(periodOfBrush(weekly, 50, 100), "2026-10-05..2026-10-18", "the last bucket ends with the period");
});

const steps: DistributionStep[] = [{ lower: 1000, upper: 1190, count: 10 }, { lower: 1190, upper: 1416, count: 30 }, { lower: 1416, upper: 1685, count: 40 }, { lower: 1685, count: 20 }];

test("steps give percentiles inside a step, bins that keep every value, and the box of a tool", () => {
  assert.equal(stepsCount(steps), 100);
  assert.equal(percentileOfSteps([], 0.5), undefined);
  const median = percentileOfSteps(steps, 0.5)!;
  assert.ok(median > 1190 && median < 1685, String(median));
  assert.ok(percentileOfSteps(steps, 0.9)! > median);
  assert.equal(percentileOfSteps(steps, 0)!, 1000);
  for (const maxBins of [1, 2, 3, 24]) {
    const bins = binSteps(steps, maxBins);
    assert.equal(bins.reduce((total, bin) => total + bin.count, 0), 100, `every value is in a bin of ${maxBins}`);
    assert.ok(bins.length <= maxBins);
  }
  const box = boxStatsOfSteps(steps)!;
  assert.ok(box.min <= box.q1 && box.q1 <= box.median && box.median <= box.q3 && box.q3 <= box.p90 && box.p90 <= box.max);
  assert.equal(boxStatsOfSteps([]), null);
  const built = distributionOption({ result: { query: series([[1]]).query, measure: "run-duration", unit: "ms", count: 100, p50: median, p90: 1650, steps }, fmt, name: "Runs", medianName: "Median", p90Name: "90th percentile" });
  const marks = ((built.option.series as { markLine?: { data: { name: string; xAxis: number }[] } }[])[0].markLine)!.data;
  assert.deepEqual(marks.map(mark => mark.name), ["Median", "90th percentile"]);
  assert.ok(marks[1].xAxis >= marks[0].xAxis);
  assert.equal(built.table.rows.reduce((total, row) => total + (row[1] as number), 0), 100);
});

test("the store keeps results by question, marks the days that changed, and forgets the least used", () => {
  const store = new QueryStore(3);
  const result = (from: string, to: string) => ({ query: { from, to } });
  store.set("a", result("2026-10-01", "2026-10-09"));
  store.set("b", result("2026-08-01", "2026-08-31"));
  store.set("c", { query: { from: "2026-09-01", to: "2026-09-30", compareFrom: "2026-08-01", compareTo: "2026-08-31" } });
  let notified = 0;
  store.subscribe(() => notified++);
  assert.equal(store.invalidate({ from: 20261008, to: 20261009 }), 1);
  assert.equal(store.get("a")!.stale, true);
  assert.equal(store.get("b")!.stale, false);
  assert.equal(notified, 1);
  assert.equal(store.invalidate({ from: 20260815, to: 20260815 }), 2, "a period compared with the days that changed is stale too");
  assert.equal(store.invalidate({ from: 19990101, to: 19990102 }), 0);
  assert.equal(notified, 2, "nobody is told when nothing was marked");
  store.set("d", result("2026-10-09", "2026-10-09"));
  assert.equal(store.size, 3);
  assert.equal(store.get("c"), undefined, "the one used least recently went");
  // A read that began before a change that touches it ends stale.
  const started = store.epoch;
  store.invalidate({ from: 20261009, to: 20261009 });
  store.set("e", result("2026-10-01", "2026-10-09"), started);
  assert.equal(store.get("e")!.stale, true);
  store.set("f", result("2026-10-01", "2026-10-09"));
  assert.equal(store.get("f")!.stale, false);
  assert.equal(store.invalidate(null) >= 1, true, "null is every day");
  store.clear();
  assert.equal(store.size, 0);
  assert.deepEqual(joinRanges(undefined, { from: 2, to: 3 }), { from: 2, to: 3 });
  assert.deepEqual(joinRanges({ from: 2, to: 3 }, { from: 1, to: 2 }), { from: 1, to: 3 });
  assert.equal(joinRanges({ from: 2, to: 3 }, null), null);
});

test("the history bar and the first-time card follow the status", () => {
  assert.equal(historyView(null), "starting");
  assert.equal(historyView(status({ state: "needsChoice" })), "choice");
  assert.equal(historyView(status({ state: "reading" })), "reading");
  assert.equal(historyView(status({ state: "paused" })), "paused");
  assert.equal(historyView(status({ state: "stoppedHere" })), "stopped");
  assert.equal(historyView(status({ state: "failed", error: "x" })), "failed");
  assert.equal(historyView(status({ state: "done" })), "none");
  assert.equal(historyView(status({ state: "done", skippedCount: 3 })), "skipped");
  // A stopped history keeps its sentence, and offers its skipped sessions beside it.
  assert.equal(historyView(status({ state: "stoppedHere", skippedCount: 3 })), "stopped");
  assert.equal(skippedToRetry(status({ state: "stoppedHere", skippedCount: 3 })), 3);
  assert.equal(skippedToRetry(status({ state: "done", skippedCount: 2 })), 2);
  assert.equal(skippedToRetry(status({ state: "stoppedHere" })), 0);
  assert.equal(skippedToRetry(status({ state: "reading", skippedCount: 3 })), 0, "the list is still being made");
  assert.equal(skippedToRetry(null), 0);
  assert.equal(progressOf(status({ sessionsTotal: 906, sessionsDone: 312 })).toFixed(3), "0.344");
  assert.equal(progressOf(status({ state: "reading", sessionsTotal: 0 })), 0);
  assert.equal(progressOf(status({ state: "done", sessionsTotal: 0 })), 1);
  assert.deepEqual(timeLeft(status({ etaSeconds: 40 })), { unit: "second", count: 40 });
  assert.equal(timeLeft(status({})), null);
  assert.equal(readingSeconds(assumedBytesPerSecond * 30), 30);
  assert.equal(readingSeconds(100, 10), 10);
  assert.deepEqual(readMoreChoices(status({ choice: "all" })), []);
  assert.deepEqual(readMoreChoices(status({ choice: "all", state: "stoppedHere" })), [{ kind: "all" }]);
  assert.deepEqual(readMoreChoices(status({ choice: "days:90" })).map(choice => choice.kind === "days" ? choice.days : choice.kind), [180, 365, "all"]);
  assert.deepEqual(readMoreChoices(status({ choice: "from-today" })).map(choice => choice.kind === "days" ? choice.days : choice.kind), [30, 90, 180, 365, "all"]);
  assert.deepEqual(readMoreChoices(status({})), []);
  assert.equal(canReadMore(status({ choice: "days:30", state: "done" })), true);
  assert.equal(canReadMore(status({ choice: "days:30", state: "reading" })), false);
});

test("filters list the values the plugin knows, and a chip knows when its filter does not apply", async () => {
  const api = createFixtureApi({ sessionCount: 120 });
  const models = await api.models({ period: "all", limit: 500 });
  const projects = await api.projects({ period: "all", limit: 500 });
  const sources = { models, projects, spaces: [{ id: "space-work", name: "Work" }], word: (_key: string, value: string) => value.toUpperCase() };
  assert.ok(filterChoices("project", sources).some(choice => choice.label === "CodeAlta"));
  assert.deepEqual(filterChoices("project", sources).map(choice => choice.label), [...filterChoices("project", sources).map(choice => choice.label)].sort((a, b) => a.localeCompare(b)));
  assert.ok(filterChoices("provider", sources).every((choice, index, all) => all.findIndex(other => other.value === choice.value) === index));
  assert.ok(filterChoices("model", sources).every(choice => choice.detail));
  assert.deepEqual(filterChoices("effort", sources).map(choice => choice.value).sort(), ["high", "low", "medium"]);
  assert.deepEqual(filterChoices("origin", sources).map(choice => choice.label), ["YOU", "AGENT", "AUTOMATION", "REMINDER"]);
  assert.equal(filterChoices("toolKind", sources).length, 8);
  assert.deepEqual(filterChoices("space", sources), [{ value: "space-work", label: "Work" }]);
  assert.deepEqual(filterChoices("model", { ...sources, models: null }), []);
  assert.deepEqual([...ignoredKinds(["origin", "nonsense", "model"])].sort(), ["model", "origin"]);
  assert.equal(unsetFilters({ project: { value: "p" } }).includes("project"), false);
  assert.equal(unsetFilters({}).length, 7);
});

test("the fixture API answers like the plugin: filters narrow, a comparison adds its line, a half-read history is not complete", async () => {
  const api = createFixtureApi({ sessionCount: 300 });
  const all = await api.summary({ period: "30d" });
  const tile = (result: typeof all, id: string) => result.tiles.find(item => item.id === id)!;
  assert.equal(all.query.from, "2026-09-10");
  assert.equal(all.query.frequency, "day");
  assert.equal(all.buckets.length, 30);
  assert.equal(tile(all, "runs").spark.length, 30);
  assert.equal(Math.round(tile(all, "runs").spark.reduce((a, b) => a + b, 0)), tile(all, "runs").value);
  const claude = await api.summary({ period: "30d", filter: { provider: "claude" } });
  assert.ok(tile(claude, "runs").value < tile(all, "runs").value);
  assert.ok(claude.costs.every(cost => cost.unit === "usd"), "only Claude reports dollars");
  const byProvider = await api.series({ period: "30d" }, "runs", "provider");
  assert.equal(Math.round(byProvider.series.reduce((total, line) => total + line.total, 0)), tile(all, "runs").value, "a series cut by a group adds up to the series without it");
  const compared = await api.series({ period: "7d", comparison: "previousPeriod" }, "tokens", null);
  assert.equal(compared.series[0].previous!.length, compared.series[0].values.length);
  assert.equal(compared.query.compareFrom, "2026-09-26");
  const hours = await api.series({ period: "today", frequency: "auto" }, "runs", null);
  assert.equal(hours.query.frequency, "hour");
  assert.equal(hours.buckets.length, 24);
  const ignored = await api.series({ period: "7d", filter: { origin: "you" } }, "tokens", null);
  assert.deepEqual(ignored.query.ignoredFilters, ["origin"], "the origin of a prompt does not filter tokens");
  assert.deepEqual((await api.series({ period: "7d", filter: { origin: "you" } }, "prompts", null)).query.ignoredFilters, []);
  await assert.rejects(api.series({ period: "7d" }, "nonsense", null), /not a metric/);
  await assert.rejects(api.summary({ period: "banana" }), /not a period/);
  const same = createFixtureApi({ sessionCount: 300 });
  assert.deepEqual((await same.summary({ period: "30d" })).tiles.map(item => item.value), all.tiles.map(item => item.value), "the same seed gives the same numbers");
  const calendar = await api.calendar({ period: "365d", frequency: "day" });
  assert.ok(calendar.days.length > 100 && calendar.maxActiveMs > 0);
  const week = await api.weekHour({ period: "90d", weekStart: "Monday" });
  assert.equal(week.weekdays[0], "Monday");
  assert.equal(week.activeMs.length, 7);
  const sessions = await api.sessions({ period: "30d", limit: 5 }, "tokens");
  assert.equal(sessions.rows.length, 5);
  assert.ok(sessions.rows[0].tokens >= sessions.rows[1].tokens);
  const detail = await api.session(sessions.rows[0].sessionId.slice(0, 12), true);
  assert.equal(detail.session.sessionId, sessions.rows[0].sessionId);
  const distribution = await api.distribution({ period: "30d" }, "run-duration", null);
  assert.equal(distribution.count, stepsCount(distribution.steps));
  assert.ok(distribution.p90! > distribution.p50!);

  const first = createFixtureApi({ scenario: "first-time", sessionCount: 120 });
  assert.equal((await first.status()).state, "needsChoice");
  const empty = await first.summary({ period: "2026-09-01..2026-10-08" });
  assert.equal(empty.query.coverage.complete, false);
  assert.equal(empty.query.coverage.historyState, "needs-choice");
  assert.equal(empty.tiles.find(item => item.id === "runs")!.value, 0, "nothing is read before the choice");
  const events: string[] = [];
  const stop = first.subscribe(event => events.push(event.kind === "status" ? event.status.state : "data"));
  await first.chooseHistory({ kind: "all" });
  const reading = await first.summary({ period: "all" });
  assert.equal(reading.query.coverage.historyState, "reading");
  first.control.advance(60);
  assert.equal((await first.status()).state, "reading");
  first.control.advance(10_000);
  assert.equal((await first.status()).state, "done");
  const done = await first.summary({ period: "all" });
  assert.equal(done.query.coverage.complete, true);
  assert.ok(done.tiles.find(item => item.id === "runs")!.value > 0);
  stop();
  assert.equal(first.control.listenerCount(), 0);
  assert.ok(events.includes("reading") && events.includes("data") && events.includes("done"));

  const partial = createFixtureApi({ scenario: "reading", sessionCount: 120 });
  const half = await partial.series({ period: "year" }, "runs", null);
  assert.equal(half.query.coverage.complete, false);
  assert.equal(half.query.coverage.completeFrom, "2026-07-14");
  assert.equal(unreadBuckets(half.buckets, half.query.coverage) > 0, true);

  const failing = createFixtureApi({ sessionCount: 120 });
  failing.control.failNext("summary", "No database.");
  await assert.rejects(failing.summary({ period: "7d" }), /No database/);
  assert.ok(await failing.summary({ period: "7d" }), "only the next call fails");
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(failing.summary({ period: "7d" }, controller.signal), { name: "AbortError" });
});

test("pages combine several results into one chart and read the names of MCP tools", () => {
  const first = series([[1, 2, 3, 4, 5, 6, 7]]), second = series([[2, 2, 2, 2, 2, 2, 2]]);
  const merged = combineSeries([{ result: first, key: "added", label: "Added" }, { result: second, key: "removed", label: "Removed", negate: true }], "lines")!;
  assert.deepEqual(merged.series.map(line => line.label), ["Added", "Removed"]);
  assert.deepEqual(merged.series[1].values, [-2, -2, -2, -2, -2, -2, -2]);
  assert.equal(merged.series[1].total, -14);
  assert.equal(combineSeries([], "x"), null);
  assert.deepEqual(mcpParts("mcp__github__issue_read"), { server: "github", tool: "issue_read" });
  assert.equal(mcpParts("read_file"), null);
});

test("every sentence of the canvas is translated in all the languages, with the same placeholders", async () => {
  const { statisticsMessages } = await import("./messages");
  const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort().join(",");
  for (const [key, row] of Object.entries(statisticsMessages)) {
    assert.equal(row.length, 5, key);
    for (const text of row) {
      assert.ok(text.trim().length > 0, key);
      assert.equal(placeholders(text), placeholders(key), `${key}: ${text}`);
    }
    assert.ok(key in messages, `${key} is in the dictionary`);
  }
  assert.equal(translate("fr", "Statistics of your sessions"), "Statistiques de vos sessions");
  assert.equal(translate("ja", "Reading the history"), "履歴を読み込み中");
  assert.equal(translate("de", "{count} sub-agents", { count: 3 }), "3 Unteragenten");
  for (const locale of locales) assert.ok(translate(locale, "Overview").length > 0);
});
