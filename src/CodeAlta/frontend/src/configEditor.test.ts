import assert from "node:assert/strict";
import test from "node:test";
import { canSaveConfig, configReadNotice, configSaveNotice, maximumConfigLength } from "./configEditor";
import { locales, messages, translate } from "./localization";

const baseline = { content: "a = 1\n", revision: "r1" };
const valid = { valid: true, message: null, line: null, column: null, warning: null };

test("save is offered only for a changed, validated, in-limit text while idle", () => {
  assert.equal(canSaveConfig(baseline, "a = 2\n", valid, false), true);
  assert.equal(canSaveConfig(baseline, baseline.content, valid, false), false, "unchanged");
  assert.equal(canSaveConfig(null, "a = 2\n", valid, false), false, "nothing read yet");
  assert.equal(canSaveConfig(baseline, "a = 2\n", null, false), false, "validation pending");
  assert.equal(canSaveConfig(baseline, "a = ", { ...valid, valid: false, message: "bad" }, false), false, "invalid");
  assert.equal(canSaveConfig(baseline, "a = 2\n", valid, true), false, "busy");
  // A provider of a newer version is left out, not an error: the file is saved with it.
  assert.equal(canSaveConfig(baseline, "a = 2\n", { ...valid, warning: "providers.future is left out" }, false), true);
  assert.equal(canSaveConfig(baseline, "#".repeat(maximumConfigLength + 1), valid, false), false, "too large");
});

test("every save and read outcome has a translated notice and only success is reported as saved", () => {
  const outcomes = ["ok", "apply_failed", "invalid", "conflict", "too_large", "stale_epoch", "unavailable", "write_failed", "anything-else"];
  for (const status of outcomes) for (const applied of [false, true]) {
    const notice = configSaveNotice({ status, providersApplied: 3 }, applied);
    assert.ok(Object.hasOwn(messages, notice.key), notice.key);
    assert.equal(notice.intent === "success", status === "ok", status);
    for (const locale of locales) if (locale !== "en") assert.notEqual(translate(locale, notice.key, notice.parameters), notice.key);
  }
  assert.match(translate("en", configSaveNotice({ status: "ok", providersApplied: 3 }, true).key, { count: 3 }), /Providers applied: 3/);
  assert.equal(configReadNotice("ok"), null);
  for (const status of ["unavailable", "stale_epoch", "too_large", "read_failed"]) {
    const notice = configReadNotice(status)!;
    assert.ok(Object.hasOwn(messages, notice.key), notice.key);
    assert.notEqual(notice.intent, "success");
  }
});

test("a provider of a newer version is said to be left out in every language", () => {
  const text = "This version of CodeAlta does not know these providers. They are left out here and kept in the configuration file; a newer version of CodeAlta can use them.";
  for (const locale of locales) {
    if (locale !== "en") assert.notEqual(translate(locale, text), text, locale);
    assert.ok(translate(locale, "type {type}", { type: "future-provider" }).includes("future-provider"), locale);
  }
});
