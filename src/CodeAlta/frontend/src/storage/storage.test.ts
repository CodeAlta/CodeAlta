import assert from "node:assert/strict";
import test from "node:test";
import { locales, translate, type MessageKey } from "../localization";
import { storageMessages } from "./messages";
import { measuredPercentage, retentionAmount, retentionUnits, storageBytes, storageCategories, storageSize } from "./storage";

test("retention drafts never truncate, clamp or interpret incomplete/fractional/exponential input", () => {
  for (const value of ["", " ", "0", "-1", "1.5", "1.", ".5", "NaN", "Infinity", "1e3", "1e", "+1", "2147483648", "999999999999999999999999"])
    assert.equal(retentionAmount(value), null, value);
  for (const [draft, amount] of [["1", 1], ["45", 45], ["0012", 12], ["2147483647", 2147483647]] as const)
    assert.equal(retentionAmount(draft), amount);
  assert.deepEqual(retentionUnits, ["days", "weeks", "months", "years"]);
});
test("categories retain stable order and whole-measurement shares including zero bytes", () => {
  assert.deepEqual(storageCategories.map(([id]) => id), ["sessions", "scratchpad", "savedStatistics", "database", "configuration", "extensions", "projects", "cacheLogs", "other", "developer"]);
  assert.equal(measuredPercentage(250, 1000), .25);
  assert.equal(measuredPercentage(0, 0), 0);
  assert.equal(storageSize(0, "en"), "0 B");
  assert.equal(storageSize(1536, "en"), "1.5 KiB");
  assert.equal(storageBytes("0"), 0);
  assert.equal(storageBytes("9223372036854775807"), Number("9223372036854775807"));
  for (const malformed of ["", "NaN", "Infinity", "-1", "0.5", "9".repeat(400)]) assert.equal(storageBytes(malformed), 0);
});
test("Storage messages have complete localized placeholders in every shell language", () => {
  for (const key of Object.keys(storageMessages) as MessageKey[]) {
    const placeholders = (key.match(/\{\w+\}/g) ?? []).sort();
    for (const locale of locales) {
      const translated = translate(locale, key);
      assert.ok(translated.length > 0);
      assert.deepEqual((translated.match(/\{\w+\}/g) ?? []).sort(), placeholders, `${locale}: ${key}`);
      if (locale !== "en") assert.notEqual(translated, key, `${locale}: ${key}`);
    }
  }
});
