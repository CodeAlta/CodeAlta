import assert from "node:assert/strict";
import test from "node:test";
import { sessionTime, timelineTime } from "./sessionTime";
import { locales } from "./localization";

const now = Date.parse("2026-09-22T12:00:00Z");
const source = (seconds: number) => new Date(now + seconds * 1000).toISOString();

test("relative labels use explicit locale, plural rules and unchanged duration boundaries", () => {
  const boundaries = [[5, "second"], [59, "second"], [60, "minute"], [119, "minute"], [120, "minute"],
    [3599, "minute"], [3600, "hour"], [7200, "hour"], [86399, "hour"], [86400, "day"],
    [172800, "day"], [604799, "day"], [604800, "week"], [1209600, "week"],
    [2591999, "week"], [2592000, "month"], [5184000, "month"], [31535999, "month"], [31536000, "year"], [63072000, "year"]] as const;
  const sizes = { second: 1, minute: 60, hour: 3600, day: 86400, week: 604800, month: 2592000, year: 31536000 };
  for (const locale of locales) {
    const intl = new Intl.RelativeTimeFormat(locale, { numeric: "auto", style: "short" });
    for (const [seconds, unit] of boundaries) for (const sign of [-1, 1]) {
      const raw = source(seconds * sign);
      const result = sessionTime(raw, locale, now);
      assert.equal(result.label, intl.format(sign * Math.floor(seconds / sizes[unit]), unit), `${locale}/${seconds}/${sign}`);
      assert.equal(result.title, raw);
      assert.equal(result.dateTime, raw);
    }
    for (const seconds of [-4, 0, 4]) assert.equal(sessionTime(source(seconds), locale, now).label, intl.format(0, "second"));
    assert.equal(sessionTime(source(4.499), locale, now).label, intl.format(0, "second"));
    assert.equal(sessionTime(source(4.5), locale, now).label, intl.format(5, "second"));
    assert.equal(sessionTime(source(-59.5), locale, now).label, intl.format(-1, "minute"), "existing rounding is preserved");
  }
  assert.equal(sessionTime(source(-86400), "en", now).label, "yesterday");
  assert.equal(sessionTime(source(86400), "en", now).label, "tomorrow");
  assert.notEqual(sessionTime(source(-120), "ja", now).label, sessionTime(source(-120), "en", now).label);
});

test("invalid, missing and unusable clocks preserve source rather than inventing activity", () => {
  for (const value of [null, undefined]) {
    assert.deepEqual(sessionTime(value, "en", now), { label: "", title: "", dateTime: undefined });
    assert.deepEqual(timelineTime(value, "en"), { label: "", title: "", dateTime: undefined });
  }
  for (const locale of locales) for (const value of ["", "unknown", "not a date"])
    assert.deepEqual(sessionTime(value, locale, now), { label: value, title: value, dateTime: undefined });
  const raw = "2026-09-22T14:00:00+02:00";
  assert.equal(sessionTime(raw, "fr", now).title, raw, "source offset is not rewritten in tooltip");
  assert.equal(sessionTime(raw, "fr", NaN).label, raw);
});

test("absolute timeline labels use selected language and retain exact timestamp provenance", () => {
  const raw = "2026-09-22T14:00:00.1234567+02:00";
  for (const locale of locales) {
    assert.deepEqual(timelineTime(raw, locale), {
      label: new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(raw)),
      title: raw, dateTime: "2026-09-22T12:00:00.123Z",
    });
    for (const value of ["", "unknown"]) assert.deepEqual(timelineTime(value, locale), { label: value, title: value, dateTime: undefined });
  }
});

test("unavailable Intl or locale data safely presents source without browser-default substitution", t => {
  const raw = source(-120);
  t.mock.method(Intl.RelativeTimeFormat, "supportedLocalesOf", () => []);
  t.mock.method(Intl.DateTimeFormat, "supportedLocalesOf", () => []);
  assert.equal(sessionTime(raw, "ja", now).label, raw);
  assert.equal(timelineTime(raw, "ja").label, raw);
  t.mock.restoreAll();
  t.mock.method(Intl, "RelativeTimeFormat", function () { throw new Error("unavailable"); });
  t.mock.method(Intl, "DateTimeFormat", function () { throw new Error("unavailable"); });
  assert.equal(sessionTime(raw, "es", now).label, raw);
  assert.equal(timelineTime(raw, "es").label, raw);
});
