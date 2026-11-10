import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../releaseNotes/releaseNotes.mount.tsx", fixture: "releaseNotesFixture", rpcFixture: "../demo-api.ts",
  styles: ["../style.css", "../releaseNotes/releaseNotes.css", "../timelineEmphasis/timelineEmphasis.css"], width: 1600, height: 1000, ratio: 1.5, reducedMotion: true } as const;
const title = `document.querySelector('.release-notes-title h1')?.textContent`;

test("the release notes say a newer version is out, show its notes, and update to it", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`${title} === '4.0.0'`, "the notes of the running version");
    assert.equal(await page.evaluate(`document.activeElement?.classList.contains('app-window-close')`), true, "the window opens with its close button focused, not its size");
    assert.equal(await page.evaluate(`document.querySelector('.release-notes-running').dataset.state`), "behind");
    assert.equal(await page.evaluate(`document.querySelector('.release-notes-running > span').textContent`), "CodeAlta 4.1.0 is out: you run 4.0.0.");
    // The picture of the notes is one of the site, as release notes show them.
    const picture = readFileSync(new URL("../../../../../site/img/alta-desktop-fleet-overview.webp", import.meta.url)).toString("base64");
    await page.evaluate(`releaseNotesFixture.state.picture = { mediaType: "image/webp", data: "${picture}" }`);
    await page.clickText(".release-notes-running .bp6-button", "What's new");
    await page.until(`${title} === '4.1.0 · Fleet and side questions' && !!document.querySelector('.release-notes-article img[src^="data:image/webp"]')`, "the notes of 4.1.0, with their picture");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.release-notes-meta .bp6-tag')].map(tag => tag.textContent).join()`), "Latest");
    await page.clickText(".release-notes-meta .bp6-button", "View on GitHub");
    assert.deepEqual(await page.evaluate(`releaseNotesFixture.state.opened`), ["https://github.com/CodeAlta/CodeAlta/releases/tag/4.1.0"]);
    await page.clickText(".release-notes-running .bp6-button", "Update to 4.1.0");
    assert.equal(await page.evaluate(`releaseNotesFixture.state.installs`), 1);
    await page.evaluate(`document.activeElement?.blur()`);
    await page.shot("release-notes");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a version is found by typing it, and the notes can stay closed at startup", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`${title} === '4.0.0'`, "the notes");
    await page.click(".release-notes-version");
    await page.until(`document.querySelectorAll('.release-notes-versions .bp6-menu-item').length === 4`, "the versions");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.release-notes-versions .release-notes-option')].map(option => option.textContent).join('|')`),
      "4.2.0-preview.1Pre-release|4.1.0Latest|4.0.0Yours|3.4.1");
    await page.evaluate(`(() => { const input = document.querySelector('.release-notes-versions input'); const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(input, '3.4'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await page.until(`document.querySelectorAll('.release-notes-versions .bp6-menu-item').length === 1`, "the one version that matches");
    await page.key("Enter", 13);
    await page.until(`${title} === '3.4.1'`, "the notes of 3.4.1");
    await page.click(".release-notes-footer .bp6-checkbox input");
    assert.equal(await page.evaluate(`releaseNotesFixture.state.hide`), true);
    // Larger and Smaller change the window a step at a time.
    const width = `document.querySelector('.release-notes-dialog .app-window').getBoundingClientRect().width`;
    const before = await page.evaluate<number>(width);
    await page.click(`.app-window-size button[aria-label="Smaller"]`);
    assert.ok(await page.evaluate<number>(width) < before - 100, "a step smaller");
    await page.click(`.app-window-size button[aria-label="Larger"]`);
    assert.ok(Math.abs(await page.evaluate<number>(width) - before) < 2, "and back");
    assert.deepEqual(page.problems, []);
  }, options);
});