import assert from "node:assert/strict";
import test from "node:test";
import { regionChurnScript } from "./regionChurn";
import { edge, withCanvas } from "./statistics/browserHarness";

// What moves in the regions of the window, in headless Edge with the production styles: the marks of a tab of the
// title bar, the level of a microphone in a popover, the bar of a download in a toast. A box that moves there makes
// Chromium send the regions again at each frame, and Windows pays for it in the host: nothing there may move.

const options = { entry: "../regionChurn.mount.tsx", fixture: "regionFixture", rpcFixture: "../demo-api.ts", styles: ["../../node_modules/flexlayout-react/style/light.css", "../speech/speech.css", "../pluginButtons/pluginButtons.css"], width: 1300, height: 400 } as const;
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

// Chromium gives the window one child window for each rectangle of the area that moves it, and makes them again at each
// change of a region. Tabs cut that area between them, scrolled-out ones too: 27 tabs made 151 windows, and each popover
// that opened stalled the UI thread for seconds. The tabs that show are one hole in it, whatever their number.
test("the tab strip of the title bar is one region, whatever its tabs", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#tab-strip .flexlayout__tab_button') !== null`, "the tab strip");
    const region = (selector: string) => page.evaluate<string>(`getComputedStyle(document.querySelector('#tab-strip ${selector}')).getPropertyValue('app-region')`);
    assert.equal(await region(".flexlayout__tabset_tabbar_inner"), "no-drag", "the tabs that show do not move the window");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('#tab-strip .flexlayout__tabset_tabbar_inner *')].filter(element => getComputedStyle(element).getPropertyValue('app-region') !== 'none').length`), 0, "nothing inside it is a box of a region: neither a tab, nor a divider, nor a tab scrolled out of it");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('#tab-strip')).getPropertyValue('app-region')`), "drag", "the rest of the bar moves the window");
    assert.equal(await region(".flexlayout__tab_toolbar"), "no-drag", "the buttons after the tabs are one hole");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.window-brand .activity-rail')).getPropertyValue('app-region')`), "no-drag", "so are the buttons of the rail");
    // The rail ends with its buttons, at the end of the brand: the room before them is the window's.
    await page.until(`document.querySelector('.window-brand .activity-rail > *') !== null`, "the buttons of the rail");
    assert.deepEqual(await page.evaluate(`(() => {
      const rail = document.querySelector('.window-brand .activity-rail'), brand = rail.closest('.window-brand'), box = rail.getBoundingClientRect();
      return { before: Math.round(rail.firstElementChild.getBoundingClientRect().left - box.left), after: Math.round(brand.getBoundingClientRect().right - parseFloat(getComputedStyle(brand).paddingRight) - box.right) };
    })()`), { before: 0, after: 0 });
    // With few tabs the strip ends with them: the bar after them is the window's, and the buttons stay at its end.
    const widths = (tabs: number) => page.evaluate<{ strip: number; toolbar: number }>(`(() => {
      const bar = document.querySelector('#tab-strip'), tabs = bar.querySelector('.flexlayout__tabset_tabbar_inner_tab_container'); tabs.style.width = '${tabs}px';
      return { strip: bar.querySelector('.flexlayout__tabset_tabbar_inner').getBoundingClientRect().width, toolbar: bar.getBoundingClientRect().right - bar.querySelector('.flexlayout__tab_toolbar').getBoundingClientRect().right };
    })()`);
    assert.deepEqual(await widths(200), { strip: 200, toolbar: 0 });
    const crowded = await widths(2400);
    assert.ok(crowded.strip < 700 && crowded.strip > 500, `with many tabs the strip scrolls in the room of the bar: ${crowded.strip}`);
    assert.equal(crowded.toolbar, 0, "the buttons stay at the end of the bar");
    // The tabs keep their place: the bar above them is still the window's.
    assert.equal(await page.evaluate(`document.querySelector('#tab-strip .flexlayout__tabset_tabbar_inner').getBoundingClientRect().top - document.querySelector('#tab-strip').getBoundingClientRect().top`), 6, "the strip above the tabs moves the window");
    assert.deepEqual(page.problems, []);
  }, options);
});

// Blueprint scales a popover as it opens, inside a box that stays. Each frame of that sent the regions to the window
// again, as each frame of a scroll in a dialog did: a popover and a dialog are one box of a region, what moves inside is none.
test("what moves inside a popover or a dialog moves no box of a region of the window", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#popover-body') !== null && document.querySelector('#dialog-body') !== null`, "the popover and the dialog");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.bp6-overlay-content')).getPropertyValue('app-region')`), "no-drag", "a popover stays clickable over the title bar");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('#region-dialog')).getPropertyValue('app-region')`), "no-drag", "a dialog stays clickable over the title bar");
    await page.evaluate(`(() => { const sheet = new CSSStyleSheet(); sheet.replaceSync('@keyframes opening { from { transform: scale(.3); } to { transform: scale(1); } } #popover-body, #dialog-body { animation: opening .3s infinite; }');
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; })()`);
    const churn = await page.evaluate<Churn>(regionChurnScript(1000));
    assert.ok(churn.frames > 30, `the regions were measured: ${JSON.stringify(churn)}`);
    assert.equal(churn.moved, 0, `what moves inside moved a box of a region of the window: ${JSON.stringify(churn)}`);
    assert.deepEqual(page.problems, []);
  }, options);
});

// The toasts of every start (the cleanup of sessions, of the scratchpad) slid in as regions of the window: each frame of
// their entrance sent the regions to the window again, a burst of kernel work at the start that the mouse of the whole
// desktop waited for. A toast sits below the title bar: it is no region.
test("a toast that slides in moves no box of a region of the window", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#tab') !== null`, "the title bar");
    await page.evaluate(`(() => { const toast = document.createElement('div'); toast.id = 'sliding-toast'; toast.className = 'bp6-toast bp6-overlay-content';
      toast.style.cssText = 'position: absolute; left: 400px; top: 46px; width: 320px; height: 40px;'; toast.textContent = 'Session cleanup is running';
      document.querySelector('.ide-shell').append(toast);
      const sheet = new CSSStyleSheet(); sheet.replaceSync('@keyframes slide-in { from { transform: translateY(-40px); } to { transform: translateY(0); } } #sliding-toast { animation: slide-in .3s infinite; }');
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; })()`);
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('#sliding-toast')).getPropertyValue('app-region')`), "none", "a toast is no region");
    const churn = await page.evaluate<Churn>(regionChurnScript(1000));
    assert.ok(churn.frames > 30, `the regions were measured: ${JSON.stringify(churn)}`);
    assert.equal(churn.moved, 0, `the toast moved a box of a region of the window: ${JSON.stringify(churn)}`);
    assert.deepEqual(page.problems, []);
  }, options);
});
