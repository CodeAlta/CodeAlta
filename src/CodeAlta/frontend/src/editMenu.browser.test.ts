import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

// The menu of a right-click in the text fields of the page, in headless Edge: the window shows no menu of its own.

const options = { entry: "../editMenu.mount.tsx", fixture: "textFieldFixture", width: 900, height: 700 } as const;
type Entry = { text: string; disabled: boolean; active: boolean };
const menu = (page: Page) => page.evaluate<Entry[] | null>("textFieldFixture.menu()");
const shown = (page: Page) => page.until("textFieldFixture.menu()", "the menu");
const enabled = async (page: Page) => (await menu(page))?.filter(entry => !entry.disabled).map(entry => entry.text);
const choose = (page: Page, text: string) => page.clickText(".edit-menu .bp6-fill", text);
// Shift+F10 itself, which the page reads: macOS asks for no menu with it.
const shiftF10 = async (page: Page) => {
  for (const type of ["keyDown", "keyUp"]) await page.command("Input.dispatchKeyEvent", { type, key: "F10", code: "F10", windowsVirtualKeyCode: 121, modifiers: 8 });
};

test("a right-click in a text field cuts and pastes there, and the field keeps the focus", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    // The pointer's own right-click, which focuses the field.
    const { x, y } = await page.evaluate<{ x: number; y: number }>(`textFieldFixture.point("editable")`);
    for (const type of ["mousePressed", "mouseReleased"]) await page.command("Input.dispatchMouseEvent", { type, x, y, button: "right", buttons: 2, clickCount: 1 });
    await shown(page);
    assert.deepEqual((await menu(page))?.map(entry => entry.text), ["Cut", "Copy", "Paste", "Select all"]);
    assert.equal(await page.evaluate("textFieldFixture.focused()"), "editable", "the menu leaves the focus in the field");
    await page.key("Escape", 27);

    await page.evaluate(`textFieldFixture.select("editable", 6, 11)`);
    await page.evaluate(`textFieldFixture.open("editable")`);
    await shown(page);
    assert.deepEqual(await enabled(page), ["Cut", "Copy", "Paste", "Select all"]);
    await choose(page, "Cut");
    await page.until(`textFieldFixture.state.value === "hello "`, "the cut text gone from the field");
    assert.equal(await page.evaluate("textFieldFixture.state.clipboard"), "world");
    assert.equal(await menu(page), null, "the menu closes");

    await page.evaluate(`textFieldFixture.select("editable", 0, 0)`);
    await page.evaluate(`textFieldFixture.open("editable")`);
    await shown(page);
    assert.deepEqual(await enabled(page), ["Paste", "Select all"], "nothing selected: nothing to cut or copy");
    await choose(page, "Paste");
    await page.until(`textFieldFixture.state.value === "worldhello "`, "the pasted text in the field, through its change");
    assert.equal(await page.evaluate("textFieldFixture.focused()"), "editable");
    assert.equal(await page.evaluate("textFieldFixture.state.blurs"), 0, "a field that saves when it loses the focus does not lose it");
  }, options);
});

test("the attributes of a field decide what its menu does", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    const entries = async (id: string, start: number, end: number) => {
      await page.evaluate(`textFieldFixture.select(${JSON.stringify(id)}, ${start}, ${end})`);
      await page.evaluate(`textFieldFixture.open(${JSON.stringify(id)})`);
      await shown(page);
      const result = await enabled(page);
      await page.key("Escape", 27);
      return result;
    };
    assert.deepEqual(await entries("readonly", 0, 4), ["Copy", "Select all"], "read-only");
    assert.deepEqual(await entries("aria", 0, 4), ["Copy", "Select all"], "aria-readonly");
    assert.deepEqual(await entries("password", 0, 6), ["Paste", "Select all"], "password");
    assert.deepEqual(await entries("area", 0, 4), ["Cut", "Copy", "Paste", "Select all"], "text area");

    for (const id of ["disabled", "fieldset"]) {
      assert.equal(await page.evaluate(`textFieldFixture.open(${JSON.stringify(id)})`), true, `${id}: the press is left alone`);
      assert.equal(await menu(page), null, `${id}: no menu`);
    }
    await page.evaluate(`textFieldFixture.open("owned")`);
    assert.equal(await page.evaluate("textFieldFixture.state.own"), 1);
    assert.equal(await menu(page), null, "a control with a menu of its own keeps it");
  }, options);
});

test("the keys choose in the menu of the keyboard, and Escape closes the menu only", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`textFieldFixture.select("area", 0, 0)`);
    await shiftF10(page);
    await shown(page);
    assert.deepEqual((await menu(page))?.filter(entry => entry.active).map(entry => entry.text), ["Paste"], "the first entry that can be chosen");
    await page.key("ArrowDown", 40);
    assert.deepEqual((await menu(page))?.filter(entry => entry.active).map(entry => entry.text), ["Select all"]);
    await page.key("Enter", 13);
    assert.equal(await page.evaluate(`textFieldFixture.selection("area")`), "line one\nline two");
    assert.equal(await menu(page), null);

    await shiftF10(page);
    await shown(page);
    await page.key("Escape", 27);
    assert.equal(await menu(page), null);
    assert.equal(await page.evaluate("textFieldFixture.state.escapes"), 0, "the dialog around the field does not hear the Escape that closed the menu");
    assert.equal(await page.evaluate("textFieldFixture.focused()"), "area");
  }, options);
});

test("the menu of a field in a modal dialog shows above the dialog and takes the press", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate("textFieldFixture.showModal()");
    await page.evaluate(`textFieldFixture.select("inDialog", 0, 2)`);
    await page.evaluate(`textFieldFixture.open("inDialog")`);
    await shown(page);
    assert.equal(await page.evaluate("textFieldFixture.menuOnTop()"), true, "the menu is not hidden under the dialog");
    await choose(page, "Copy");
    await page.until(`textFieldFixture.state.clipboard === "in"`, "the copy from the menu over the dialog");
  }, options);
});

test("a right-click on text selected in the page copies it", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.evaluate("textFieldFixture.selectText()");
    const { x, y } = await page.evaluate<{ x: number; y: number }>(`textFieldFixture.point("text")`);
    for (const type of ["mousePressed", "mouseReleased"]) await page.command("Input.dispatchMouseEvent", { type, x, y, button: "right", buttons: 2, clickCount: 1 });
    await shown(page);
    assert.deepEqual((await menu(page))?.map(entry => entry.text), ["Copy"]);
    await choose(page, "Copy");
    await page.until(`textFieldFixture.state.copied === "Some text of the page."`, "the copy of the selection");
  }, options);
});
