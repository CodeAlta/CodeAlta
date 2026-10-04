import assert from "node:assert/strict";
import test from "node:test";
import { entryAddedNotice, exitQuestion, keepRunningPlace } from "./desktopShell";
import { locales, translate } from "./localization";

test("the exit question counts the sessions it would stop", () => {
  assert.equal(translate("en", exitQuestion(1).key, exitQuestion(1).parameters), "A session is running. Exiting CodeAlta stops it.");
  assert.equal(translate("en", exitQuestion(3).key, exitQuestion(3).parameters), "3 sessions are running. Exiting CodeAlta stops them.");
  for (const locale of locales) {
    if (locale === "en") continue;
    assert.notEqual(translate(locale, exitQuestion(1).key), exitQuestion(1).key);
    assert.ok(translate(locale, exitQuestion(3).key, exitQuestion(3).parameters).includes("3"));
  }
});

test("the place the application stays in is named as the platform names it", () => {
  assert.ok(translate("en", keepRunningPlace("windows")).includes("notification area"));
  assert.ok(translate("en", keepRunningPlace("macos")).includes("menu bar"));
  assert.ok(translate("en", keepRunningPlace("linux")).includes("system tray"));
  assert.equal(keepRunningPlace("other"), keepRunningPlace("windows"));
});

test("the first start says where the application was added", () => {
  assert.ok(translate("en", entryAddedNotice("windows")).includes("Start Menu"));
  assert.ok(translate("en", entryAddedNotice("macos")).includes("Applications folder"));
  assert.ok(translate("en", entryAddedNotice("linux")).includes("applications menu"));
  for (const locale of locales) if (locale !== "en") assert.notEqual(translate(locale, entryAddedNotice("macos")), entryAddedNotice("macos"));
});
