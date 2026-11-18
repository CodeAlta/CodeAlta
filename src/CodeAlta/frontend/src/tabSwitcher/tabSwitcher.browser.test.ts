import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

// Ctrl+Tab in headless Edge: the keys of the window over the production switcher and its styles; a preview has its title in italics. A quick press goes to
// the tab shown before and the next one goes back; while Ctrl is held a list shows the tabs, and Escape shows none.

const options = { entry: "../tabSwitcher/tabSwitcher.mount.tsx", fixture: "tabSwitcherFixture", rpcFixture: "../demo-api.ts", width: 900, height: 500 } as const;
const control = 2, shift = 8;
const handled = (page: Page) => page.evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))`);
// Before the first key: the window is there, and the page has the focus. A headless page takes the focus when it wants
// (a `focusin` or a `blur` of the window, some time after its first key), and a focus that moves cancels the switcher,
// as it must: the list of a test then never opened.
async function ready(page: Page) {
  await page.command("Emulation.setFocusEmulationEnabled", { enabled: true });
  await page.until(`document.querySelector('#front')`, "the window");
}

function keys(page: Page) {
  const key = (type: "keyDown" | "keyUp", name: string, virtualKey: number, modifiers: number, code = name) =>
    page.command("Input.dispatchKeyEvent", { type, key: name, code, windowsVirtualKeyCode: virtualKey, modifiers });
  const tab = async (modifiers = control) => { await key("keyDown", "Tab", 9, modifiers); await key("keyUp", "Tab", 9, modifiers); };
  return { key, tab };
}

test("Ctrl+Tab goes back and forth between the last two tabs, and a list shows the others while Ctrl is held", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const { key, tab } = keys(page);
    const front = () => page.evaluate<string>(`document.querySelector('#front').textContent`);
    const list = `[...document.querySelectorAll('.tab-switcher-row')].map(row => row.textContent + (row.getAttribute('aria-selected') === 'true' ? '*' : '')).join()`;
    // The list is drawn by React after the key: wait for it to say what is expected.
    const rows = async (expected: string[]) => { await page.until(`(${list}) === ${JSON.stringify(expected.join())}`, `the list ${expected.join()}`); };
    await ready(page);

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
    assert.equal(await page.evaluate(`document.querySelector('.sr-only[role="status"]').textContent`), "three", "the highlighted tab is announced without moving focus");
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

test("stepping through the list does not render the window, and wrapping to the current tab does not select it again", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const { key, tab } = keys(page);
    await ready(page);
    const renders = `document.querySelector('#front').dataset.renders`;
    const before = await page.evaluate(renders);
    await key("keyDown", "Control", 17, control);
    for (let index = 0; index < 3; index++) await tab();
    await page.until(`document.querySelector('[role="option"][aria-selected="true"]')?.textContent === 'one'`, "the highlight wrapping to the current tab");
    assert.equal(await page.evaluate(renders), before, "only the switcher re-renders");
    await key("keyUp", "Control", 17, 0);
    await page.until(`!document.querySelector('.tab-switcher')`, "the switcher closed");
    assert.equal(await page.evaluate(`document.querySelector('#front').dataset.selections`), "0");
  }, options);
});

test("only releasing the last Control key applies the highlighted tab", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const { key, tab } = keys(page);
    await ready(page);
    await key("keyDown", "Control", 17, control, "ControlLeft");
    await tab();
    await key("keyDown", "Control", 17, control, "ControlRight");
    await key("keyUp", "Control", 17, control, "ControlLeft");
    // The list is drawn by React after the key, not within a frame or two on a loaded machine: wait for it, then for the
    // release of one Control to have been handled, before saying that it applied nothing.
    await page.until(`document.querySelector('[role="option"][aria-selected="true"]')?.textContent === 'two'`, "the list open on the highlighted tab");
    await handled(page);
    assert.equal(await page.evaluate(`document.querySelector('[role="option"][aria-selected="true"]')?.textContent`), "two");
    assert.equal(await page.evaluate(`document.querySelector('#front').textContent`), "one");
    await key("keyUp", "Control", 17, 0, "ControlRight");
    await page.until(`document.querySelector('#front').textContent === 'two' && !document.querySelector('.tab-switcher')`, "the chosen tab shown");
  }, options);
});

test("leaving the switcher by pointer, another shortcut or a focus change cancels its pending selection", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const { key, tab } = keys(page);
    await ready(page);
    for (const leave of [
      async () => { await key("keyDown", "PageDown", 34, control); await key("keyUp", "PageDown", 34, control); },
      () => page.evaluate("document.querySelector('#other').focus()"),
      () => page.click("#other"),
      () => page.evaluate("window.dispatchEvent(new Event('blur'))"),
    ]) {
      await key("keyDown", "Control", 17, control);
      await tab();
      await page.until(`!!document.querySelector('.tab-switcher')`, "the switcher open");
      await leave();
      await handled(page);
      assert.equal(await page.evaluate(`!!document.querySelector('.tab-switcher')`), false);
      await key("keyUp", "Control", 17, 0);
      await handled(page);
      assert.equal(await page.evaluate(`document.querySelector('#front').dataset.selections`), "0");
    }
  }, options);
});
