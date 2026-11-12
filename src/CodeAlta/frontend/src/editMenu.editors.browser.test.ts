import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build, type Plugin } from "esbuild";
import type { MenuEntry } from "./editMenu.probe";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

// The editors of Monaco under the edit menu of the page, in headless Edge: they have no menu of their own, and the one of
// the text fields, with the same entries. Monaco's own showed others, and in an editor whose widgets are fixed to the
// window (the prompt, a file) it had no surface and black entries.

const editorWorker: Plugin = { name: "editor-worker", setup(bundle) {
  bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
  bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
    const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
      bundle: true, platform: "browser", format: "iife", write: false });
    return { loader: "js", contents: `export default class extends Worker { constructor() {
      const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
      super(url); URL.revokeObjectURL(url);
    } }` };
  });
} };
const options = { entry: "../editMenu.editors.mount.tsx", fixture: "editorsFixture", width: 900, height: 600, plugins: [editorWorker] } as const;

const press = async (page: Page, { x, y }: { x: number; y: number }, button: "left" | "right") => {
  for (const type of ["mousePressed", "mouseReleased"]) await page.command("Input.dispatchMouseEvent", { type, x, y, button, buttons: button === "right" ? 2 : 1, clickCount: 1 });
};
const rightClick = async (page: Page, editor: string) => press(page, await page.evaluate<{ x: number; y: number }>(`editorsFixture.point(${JSON.stringify(editor)})`), "right");
const choose = async (page: Page, text: string) => press(page, await page.evaluate<{ x: number; y: number }>(`editorsFixture.entry(${JSON.stringify(text)})`), "left");
// The entries of the open menu, a disabled one followed by "-", the one the keys chose by "*".
const entries = async (page: Page) => {
  await page.until("editorsFixture.menu()", "the menu");
  return (await page.evaluate<MenuEntry[]>("editorsFixture.menu()")).map(entry => `${entry.text}${entry.disabled ? "-" : ""}${entry.active ? "*" : ""}`);
};

test("a right-click in the prompt, a file or a read-only editor opens the menu of the text fields, readable in both themes", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.until("editorsFixture.ready()", "the editors");
    for (const theme of ["dark", "light"] as const) {
      await page.evaluate(`editorsFixture.theme(${JSON.stringify(theme)})`);
      for (const [editor, expected] of [["prompt", ["Cut-", "Copy-", "Paste", "Select all"]], ["file", ["Cut-", "Copy-", "Paste", "Select all"]],
        ["readonly", ["Cut-", "Copy-", "Paste-", "Select all"]]] as const) {
        // A right-click of the mouse, the browser's own input.
        await rightClick(page, editor);
        const where = `${editor}, ${theme}`;
        assert.deepEqual(await entries(page), expected, where);
        // An opaque surface of the theme, under entries that read on it (WCAG AA: 4.5).
        const surface = await page.evaluate<{ opacity: number; contrast: number; background: number }>("editorsFixture.surface()");
        assert.equal(surface.opacity, 1, where);
        assert.ok(theme === "dark" ? surface.background < 0.2 : surface.background > 0.6, `${where}: ${JSON.stringify(surface)}`);
        assert.ok(surface.contrast >= 4.5, `${where}: ${JSON.stringify(surface)}`);
        await page.key("Escape", 27);
        await page.until("editorsFixture.menu() === null", `${where}: the menu closed`);
      }
    }
  }, options);
});

test("the entries of the menu edit the prompt as its keys do, and Shift+F10 opens it", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  await withCanvas(async page => {
    await page.until("editorsFixture.ready()", "the editors");
    await rightClick(page, "prompt");
    await entries(page);
    await choose(page, "Select all");
    await rightClick(page, "prompt");
    assert.deepEqual(await entries(page), ["Cut", "Copy", "Paste", "Select all"], "the selection is kept under the pointer");
    await choose(page, "Cut");
    await page.until(`editorsFixture.state.prompt === ""`, "the cut text gone from the prompt");
    assert.equal(await page.evaluate("editorsFixture.state.clipboard"), "a prompt");
    await rightClick(page, "prompt");
    await entries(page);
    await choose(page, "Paste");
    await page.until(`editorsFixture.state.prompt === "a prompt"`, "the clipboard pasted in the prompt");

    // Shift+F10 in the prompt, which has the keyboard after the paste: the same menu, its first enabled entry chosen.
    for (const type of ["keyDown", "keyUp"]) await page.command("Input.dispatchKeyEvent", { type, key: "F10", code: "F10", windowsVirtualKeyCode: 121, modifiers: 8 });
    assert.deepEqual(await entries(page), ["Cut-", "Copy-", "Paste*", "Select all"], "the menu of the keyboard");
    await page.key("Escape", 27);
  }, options);
});
