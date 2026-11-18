import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

// Ctrl+Tab in headless Edge: the keys of the window over the production switcher and its styles; a preview has its title in italics. A quick press goes to
// the tab shown before and the next one goes back; while Ctrl is held a list shows the tabs, and Escape shows none.

const options = { entry: "../tabSwitcher.mount.tsx", fixture: "tabSwitcherFixture", rpcFixture: "../demo-api.ts", width: 900, height: 500 } as const;
const control = 2, shift = 8;

test("Ctrl+Tab goes back and forth between the last two tabs, and a list shows the others while Ctrl is held", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const key = (type: "keyDown" | "keyUp", name: string, code: number, modifiers: number) =>
      page.command("Input.dispatchKeyEvent", { type, key: name, code: name, windowsVirtualKeyCode: code, modifiers });
    const front = () => page.evaluate<string>(`document.querySelector('#front').textContent`);
    const list = `[...document.querySelectorAll('.tab-switcher-row')].map(row => row.textContent + (row.getAttribute('aria-selected') === 'true' ? '*' : '')).join()`;
    // The list is drawn by React after the key: wait for it to say what is expected.
    const rows = async (expected: string[]) => { await page.until(`(${list}) === ${JSON.stringify(expected.join())}`, `the list ${expected.join()}`); };
    const tab = async (modifiers = control) => { await key("keyDown", "Tab", 9, modifiers); await key("keyUp", "Tab", 9, modifiers); };
    await page.until(`document.querySelector('#front')`, "the window");

    // Tab, then Control released: the tab shown before; again: back.
    await key("keyDown", "Control", 17, control);
    await tab();
    await rows(["one", "two*", "three"]);
    await key("keyUp", "Control", 17, 0);
    await page.until(`document.querySelector('#front').textContent === 'two' && !document.querySelector('.tab-switcher')`, "the tab before shown, the list closed");
    await key("keyDown", "Control", 17, control); await tab(); await key("keyUp", "Control", 17, 0);
    await page.until(`document.querySelector('#front').textContent === 'one'`, "the tab left, shown again");
    await key("keyDown", "Control", 17, control); await tab(); await key("keyUp", "Control", 17, 0);
    await page.until(`document.querySelector('#front').textContent === 'two'`, "back to the tab left"); 

    // Held: the list is by recency, each press goes further, Shift goes back up, Escape closes without going.
    await key("keyDown", "Control", 17, control); await tab(); await tab();
    await rows(["two", "one", "three*"]);
    const style = (label: string) => page.evaluate<string>(`getComputedStyle([...document.querySelectorAll('.tab-switcher-label')].find(node => node.textContent === ${JSON.stringify(label)})).fontStyle`);
    assert.deepEqual([await style("three"), await style("one")], ["italic", "normal"], "a preview has its title in italics, as in the strip");
    await tab(control | shift);
    await rows(["two", "one*", "three"]);
    await key("keyDown", "Escape", 27, control); await key("keyUp", "Escape", 27, control);
    await page.until(`!document.querySelector('.tab-switcher')`, "the list closed by Escape");
    await key("keyUp", "Control", 17, 0);
    assert.equal(await front(), "two", "Escape goes nowhere");

    await key("keyDown", "Control", 17, control); await tab(); await tab(); await key("keyUp", "Control", 17, 0);
    await page.until(`document.querySelector('#front').textContent === 'three'`, "the tab chosen when Ctrl is released");
    assert.deepEqual(page.problems, []);
  }, options);
});
