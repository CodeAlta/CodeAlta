import assert from "node:assert/strict";
import test from "node:test";
import { locales, translate, type MessageKey } from "../localization";
import { storageMessages } from "./messages";
import { categoryBytes, measuredPercentage, oldScratchpadText, retentionAmount, retentionUnits, shareText, storageBytes, storageCategories, storageSize } from "./storage";

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
test("a share reads as a percentage of the whole, and never as 0% when it holds bytes", () => {
  assert.equal(shareText(250, 1000, "en"), "25%");
  assert.equal(shareText(1, 10_000, "en"), "<0.1%");
  assert.equal(shareText(0, 1000, "en"), "0%");
  assert.equal(shareText(0, 0, "en"), "0%");
  assert.equal(shareText(1234, 10_000, "de"), "12,3\u00a0%");
});
test("the cleanup card says what of the scratchpad the next start removes, and nothing when that is nothing", () => {
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
  assert.equal(oldScratchpadText({ oldScratchpadBytes: String(3 * 1024 * 1024), oldScratchpadFiles: "12" }, "en", t), "The next start removes 3 MiB of old scratchpad files (12 files).");
  assert.equal(oldScratchpadText({ oldScratchpadBytes: "10", oldScratchpadFiles: "1" }, "en", t), "The next start removes 10 B of old scratchpad files (1 file).");
  assert.equal(oldScratchpadText({ oldScratchpadBytes: "0", oldScratchpadFiles: "0" }, "en", t), null);
  assert.equal(oldScratchpadText(null, "en", t), null, "before the scan, nothing is said");
});
test("categories without a row of their own add to Other state, so the table and the donut agree", () => {
  const bytes = categoryBytes([{ id: "sessions", bytes: "40" }, { id: "reviews", bytes: "60" }, { id: "browser", bytes: "5" }, { id: "models", bytes: "7" }, { id: "other", bytes: "1" }]);
  assert.equal(bytes.get("sessions"), 40);
  assert.equal(bytes.get("other"), 73);
  assert.equal(bytes.get("developer"), 0);
  assert.deepEqual([...bytes.keys()], storageCategories.map(([id]) => id));
  assert.equal([...bytes.values()].reduce((sum, value) => sum + value, 0), 113, "every measured byte is in one row");
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
