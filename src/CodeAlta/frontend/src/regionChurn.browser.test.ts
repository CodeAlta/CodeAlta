import assert from "node:assert/strict";
import test from "node:test";
import { regionChurnScript } from "./regionChurn";
import { edge, withCanvas } from "./statistics/browserHarness";

// What moves in the regions of the window, in headless Edge with the production styles: the marks of a tab of the
// title bar, the level of a microphone in a popover, the bar of a download in a toast. A box that moves there makes
// Chromium send the regions again at each frame, and Windows pays for it in the host: nothing there may move.

const options = { entry: "../regionChurn.mount.tsx", fixture: "regionFixture", rpcFixture: "../demo-api.ts", styles: ["../speech/speech.css", "../pluginButtons/pluginButtons.css"], width: 1300, height: 400 } as const;
type Churn = { regions: number; frames: number; moved: number };

test("nothing that animates in a region of the window moves a box", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#tab .activity-spinner') && document.querySelector('.speech-level > span') && document.querySelector('.painted-progress')
      && document.querySelector('.window-brand .plugin-button-badge[data-kind=busy]')`, "the marks, the level, the bar and the busy ring");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.plugin-button-badge[data-kind=busy]')).getPropertyValue('app-region')`), "no-drag", "the ring is in a region");
    // The ring is painted, whatever the tone of its button: its arc is what turns.
    assert.equal(await page.evaluate(`document.querySelector('.plugin-button[data-tone] .plugin-button-badge[data-kind=busy]') !== null`), true, "the button has a tone");
    assert.match(await page.evaluate<string>(`getComputedStyle(document.querySelector('.plugin-button-badge[data-kind=busy]')).backgroundImage`), /conic-gradient/, "the arc of the ring is painted");
    await page.until(`document.querySelector('.speech-level > span').style.getPropertyValue('--speech-level-bar') !== '0%'`, "the level of the microphone");

    // The measure sees a box that moves: the mark of #164 as it was, scaled in a tab of the title bar.
    await page.evaluate(`(() => { const sheet = new CSSStyleSheet(); sheet.replaceSync('@keyframes old-mark { 0%, 100% { transform: scale(.8); } 50% { transform: scale(1); } } #tab .session-background { animation: old-mark .6s ease-in-out infinite; }');
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; window.oldMark = sheet; })()`);
    const before = await page.evaluate<Churn>(regionChurnScript(1000));
    assert.ok(before.moved > before.frames / 2, `the measure sees the mark of #164 move: ${JSON.stringify(before)}`);
    await page.evaluate(`document.adoptedStyleSheets = document.adoptedStyleSheets.filter(sheet => sheet !== window.oldMark)`);

    // The marks of a tab, the level of the microphone, the bar of the download: they change, and no box moves.
    const after = await page.evaluate<Churn>(regionChurnScript(1500));
    assert.ok(after.regions >= 4 && after.frames > 30, `the regions were measured: ${JSON.stringify(after)}`);
    assert.equal(after.moved, 0, `a box moved in a region of the window: ${JSON.stringify(after)}`);
    assert.deepEqual(page.problems, []);
  }, options);
});
