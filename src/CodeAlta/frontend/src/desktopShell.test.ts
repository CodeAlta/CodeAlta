import assert from "node:assert/strict";
import test from "node:test";
import { closeBehavior, closeBehaviorLabel, closeBehaviors, closeQuestion, entryAddedNotice, exitQuestion, keepRunningPlace, nextChoice } from "./desktopShell";
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

test("what closing the window does is named as the host names it, and in every language", () => {
  assert.deepEqual([...closeBehaviors], ["ask", "keep", "exit"]);
  assert.deepEqual(closeBehaviors.map(closeBehavior), ["ask", "keep", "exit"]);
  // A name of a newer or older host asks: nothing is hidden or exited without the user.
  for (const other of ["", "tray", "Keep", "true"]) assert.equal(closeBehavior(other), "ask");
  assert.deepEqual(closeBehaviors.map(behavior => translate("en", closeBehaviorLabel(behavior))), ["Ask each time", "Keep running", "Exit CodeAlta"]);
  for (const locale of locales) {
    if (locale === "en") continue;
    for (const key of [...closeBehaviors.map(closeBehaviorLabel), "When the window is closed", "Keep CodeAlta running?", "Remember my choice"] as const)
      assert.notEqual(translate(locale, key), key, `${locale}: ${key}`);
  }
});

test("the question about the closed window names where the application stays", () => {
  assert.ok(translate("en", closeQuestion("windows")).includes("notification area"));
  assert.ok(translate("en", closeQuestion("macos")).includes("menu bar"));
  assert.ok(translate("en", closeQuestion("linux")).includes("system tray"));
  assert.equal(closeQuestion("other"), closeQuestion("windows"));
  for (const locale of locales) {
    if (locale === "en") continue;
    for (const platform of ["windows", "macos", "linux"]) assert.notEqual(translate(locale, closeQuestion(platform)), closeQuestion(platform), `${locale}: ${platform}`);
  }
});

test("the arrow keys move between the answers of a question, around the ends", () => {
  assert.equal(nextChoice(2, "ArrowLeft", 3), 1);
  assert.equal(nextChoice(0, "ArrowLeft", 3), 2);
  assert.equal(nextChoice(2, "ArrowRight", 3), 0);
  assert.equal(nextChoice(1, "ArrowDown", 3), 2);
  assert.equal(nextChoice(1, "ArrowUp", 3), 0);
  // Any other key, and a key pressed outside the answers, moves nothing.
  for (const key of ["Enter", "Tab", " ", "Escape", "a"]) assert.equal(nextChoice(1, key, 3), -1);
  assert.equal(nextChoice(-1, "ArrowLeft", 3), -1);
  assert.equal(nextChoice(3, "ArrowRight", 3), -1);
});
