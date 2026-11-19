import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

const options = { entry: "../brandIcon.mount.tsx", fixture: "brandIcons", width: 400, height: 200 } as const;

test("a logo that its page renders again keeps what it drew", { skip: !edge, timeout: testLimit(60_000) }, async () => {
  await withCanvas(async page => {
    await page.until("window.brandIcons", "the page is ready");
    const names = JSON.stringify(["claudecode", "gemini", "codex"]);
    await page.evaluate(`brandIcons.show(${names})`);
    assert.equal(await page.evaluate("document.querySelectorAll('svg.brand-icon > *').length > 3"), true, "each logo is drawn");
    // Every element put in a logo or taken out of it from now on.
    await page.evaluate(`window.changed = 0; window.drawn = [...document.querySelectorAll("svg.brand-icon")].map(svg => svg.firstElementChild);
      new MutationObserver(records => { for (const record of records) window.changed += record.addedNodes.length + record.removedNodes.length; })
        .observe(document.getElementById("root"), { childList: true, subtree: true })`);
    // The window renders for something else: the same logos, with what they were asked.
    for (let render = 0; render < 3; render++) await page.evaluate(`brandIcons.show(${names})`);
    await page.evaluate("new Promise(resolve => setTimeout(resolve, 0))");
    assert.equal(await page.evaluate("window.changed"), 0, "nothing is drawn again");
    assert.equal(await page.evaluate(`[...document.querySelectorAll("svg.brand-icon")].every((svg, index) => svg.firstElementChild === window.drawn[index])`), true);
    // Another size keeps the drawing too; another brand is drawn.
    await page.evaluate(`brandIcons.show(${names}, 24)`);
    await page.evaluate("new Promise(resolve => setTimeout(resolve, 0))");
    assert.equal(await page.evaluate("window.changed"), 0);
    assert.equal(await page.evaluate("document.querySelector('svg.brand-icon').getAttribute('width')"), "24");
    await page.evaluate(`brandIcons.show(["mistral"])`);
    assert.equal(await page.evaluate("document.querySelector('svg.brand-icon').dataset.brand"), "mistral");
    assert.equal(await page.evaluate("document.querySelectorAll('svg.brand-icon > *').length > 0"), true);
  }, options);
});
