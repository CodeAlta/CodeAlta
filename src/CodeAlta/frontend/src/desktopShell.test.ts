import assert from "node:assert/strict";
import test from "node:test";
import { closeBehavior, closeBehaviorLabel, closeBehaviors, closeQuestion, entryAddedNotice, exitQuestion, keepRunningPlace, nextChoice } from "./desktopShell";
import { locales, translate } from "./localization";

test("the exit question counts the sessions it would stop and the terminals whose command it would end", () => {
  const asked = (sessions: number, terminals = 0, locale: typeof locales[number] = "en") => exitQuestion(sessions, terminals).map(question => translate(locale, question.key, question.parameters));
  assert.deepEqual(asked(1), ["A session is running. Exiting CodeAlta stops it."]);
  assert.deepEqual(asked(3), ["3 sessions are running. Exiting CodeAlta stops them."]);
  assert.deepEqual(asked(0, 1), ["A terminal is running a command. Exiting CodeAlta ends it."]);
  assert.deepEqual(asked(0, 4), ["4 terminals are running a command. Exiting CodeAlta ends them."]);
  assert.deepEqual(asked(2, 1), ["2 sessions are running. Exiting CodeAlta stops them.", "A terminal is running a command. Exiting CodeAlta ends it."]);
  for (const locale of locales) {
    if (locale === "en") continue;
    assert.notDeepEqual(asked(1, 1, locale), asked(1, 1));
    assert.ok(asked(3, 5, locale)[0].includes("3") && asked(3, 5, locale)[1].includes("5"));
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
