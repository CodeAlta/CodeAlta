import assert from "node:assert/strict";
import { test } from "node:test";
import { durationParts, kilobytesText, meaningEstimate } from "./meaningEstimate";

test("the estimate counts the model, the runtime when it is missing, the vectors and the time of each preset", () => {
  const gemma = meaningEstimate("embeddinggemma-2", 311_000, 6_000, false);
  assert.equal(gemma.downloadKilobytes, 311_000 + 10 * 1024);
  assert.equal(gemma.diskKilobytes, Math.ceil(6_000 * 792 / 1024));
  assert.equal(gemma.seconds, 600);
  const granite = meaningEstimate("granite-97m-multilingual-r2", 95_000, 6_000, true);
  assert.equal(granite.downloadKilobytes, 95_000, "the runtime speech brought is not counted again");
  assert.ok(granite.seconds < gemma.seconds, "the light preset is faster");
  assert.equal(meaningEstimate("unknown", 0, 0, true).seconds, 0);
});

test("a duration reads in seconds, minutes, or hours and minutes", () => {
  assert.deepEqual(durationParts(12), { unit: "seconds", value: 12, minutes: 0 });
  assert.deepEqual(durationParts(600), { unit: "minutes", value: 10, minutes: 0 });
  assert.deepEqual(durationParts(2 * 3600 + 25 * 60), { unit: "hours", value: 2, minutes: 25 });
  assert.equal(durationParts(0).value, 1);
});

test("a size reads in KB, MB or GB", () => {
  assert.equal(kilobytesText(512, "en"), "512 KB");
  assert.equal(kilobytesText(15 * 1024 + 512, "en"), "15.5 MB");
  assert.equal(kilobytesText(319 * 1024, "en"), "319 MB");
  assert.equal(kilobytesText(3 * 1024 * 1024, "en"), "3 GB");
});
