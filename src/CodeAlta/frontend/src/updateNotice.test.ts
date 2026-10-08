import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { locales, translate } from "./localization";
import { availableUpdate, installedNotice, RecoveryUpdateNotice, updateStatus } from "./UpdateNotice";

const available = { status: "available", latestVersion: "1.3.0", command: "dotnet tool update -g CodeAlta", releaseNotes: "https://github.com/CodeAlta/CodeAlta/releases/tag/1.3.0", canInstall: false };

test("only a newer version with its command is announced", () => {
  assert.deepEqual(availableUpdate(available), { version: "1.3.0", command: "dotnet tool update -g CodeAlta", releaseNotes: true, canInstall: false });
  assert.equal(availableUpdate({ ...available, canInstall: true })?.canInstall, true);
  assert.equal(availableUpdate({ ...available, releaseNotes: null })?.releaseNotes, false);
  assert.equal(availableUpdate({ ...available, command: null }), null);
  assert.equal(availableUpdate({ ...available, status: "latest" }), null);
  assert.equal(availableUpdate(null), null);
  assert.equal(availableUpdate(undefined), null);
});

test("the About page says what the check found", () => {
  assert.equal(updateStatus(null)?.key, "Checking for updates…");
  assert.deepEqual(updateStatus(available), { key: "Version {version} is available.", parameters: { version: "1.3.0" } });
  assert.equal(updateStatus({ status: "latest", latestVersion: "1.3.0" })?.key, "You are running the latest version.");
  assert.equal(updateStatus({ status: "failed", latestVersion: null })?.key, "The update check failed.");
  // Nothing to say for a build that is not a published version.
  assert.equal(updateStatus({ status: "unavailable", latestVersion: null }), null);
  assert.equal(updateStatus({ status: "not_found", latestVersion: null }), null);
  for (const locale of locales) assert.ok(translate(locale, "CodeAlta {version} is available.", { version: "1.3.0" }).includes("1.3.0"));
});

test("the start after an update says how it went, once", () => {
  assert.deepEqual(installedNotice({ installed: "ok", currentVersion: "1.3.0" }),
    { key: "CodeAlta was updated to {version}.", parameters: { version: "1.3.0" }, intent: "success" });
  assert.equal(installedNotice({ installed: "failed", currentVersion: "1.2.0" })?.intent, "danger");
  assert.equal(installedNotice({ installed: null, currentVersion: "1.2.0" }), null);
});

test("the screen of a configuration that cannot be loaded offers the newer version that may have written it", () => {
  const never = () => assert.fail("rendering must not act");
  const render = (canInstall: boolean, failed: boolean, locale: typeof locales[number]) => renderToStaticMarkup(createElement(RecoveryUpdateNotice, {
    update: availableUpdate({ ...available, canInstall })!, locale, failed, onInstall: never, onOpenReleaseNotes: never }));
  for (const locale of locales) {
    // An installed tool updates itself and starts again: an older version shows nothing but this screen.
    const installed = render(true, false, locale);
    assert.ok(installed.includes(translate(locale, "CodeAlta {version} is available.", { version: "1.3.0" })), installed);
    assert.ok(installed.includes(translate(locale, "A newer version may have written this file. Update before you change it.")), installed);
    assert.ok(installed.includes(`>${translate(locale, "Update and restart")}<`), installed);
    assert.ok(!installed.includes('role="alert"'), installed);
    // Any other build is told the command, and an update that could not be started says so.
    const other = render(false, true, locale);
    assert.ok(other.includes("dotnet tool update -g CodeAlta") && !other.includes(`>${translate(locale, "Update and restart")}<`), other);
    assert.ok(other.includes(translate(locale, "Exit CodeAlta, then run this command in a terminal.")), other);
    assert.ok(other.includes('role="alert"'), other);
  }
});
