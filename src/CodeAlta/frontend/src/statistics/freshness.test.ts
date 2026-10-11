import assert from "node:assert/strict";
import test from "node:test";
import { createFormatter } from "./format";

for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"]) {
  test(`data freshness includes the local date and seconds in ${locale}`, () => {
    const instant = "2026-10-09T12:34:56Z";
    const fmt = createFormatter(locale, { credits: value => value, none: "–" });
    const expected = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "medium" });
    assert.equal(fmt.dateTime(instant), expected.format(new Date(instant)));
    assert.ok(expected.formatToParts(new Date(instant)).some(part => part.type === "second" && part.value === "56"));
    assert.notEqual(fmt.dateTime(instant), fmt.dateTime("2026-10-09T12:34:57Z"));
    assert.equal(fmt.dateTime("not a timestamp"), "–");
  });
}
