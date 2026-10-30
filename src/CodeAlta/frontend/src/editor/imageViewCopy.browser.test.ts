import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";

// A picture of the project editor in headless Edge, under the production content security policy and React StrictMode.

const options = { entry: "../editor/imageViewCopy.mount.tsx", fixture: "imageViewFixture", width: 900, height: 700 } as const;
const written = "imageViewFixture.state.written";
const menuItems = `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.textContent.trim())`;

async function rightClick(page: Page, element: string) {
  const { x, y } = await page.evaluate<{ x: number; y: number }>(`(() => { const box = ${element}.getBoundingClientRect(); return { x: box.x + box.width / 2, y: box.y + box.height / 2 }; })()`);
  await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
  await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "right", buttons: 2, clickCount: 1 });
  await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "right", buttons: 0, clickCount: 1 });
}

test("a picture of the project editor is copied from its context menu, its tools and Ctrl+C", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fixture-picture img')?.naturalWidth > 0`, "the picture");

    // A right-click on the picture opens its menu; an icon is drawn again as PNG.
    await rightClick(page, `document.querySelector('.fixture-picture img')`);
    await page.until(`document.querySelectorAll('.session-tab-popup [role="menuitem"]').length === 1`, "the menu of the picture");
    assert.deepEqual(await page.evaluate(menuItems), ["Copy image"]);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Copy image");
    await page.until(`${written}.length === 1`, "the copied picture");
    assert.deepEqual(await page.evaluate(`${written}[0]`), { types: ["image/png"], type: "image/png", width: 64, height: 48 });
    await page.until(`document.querySelector('.fixture-picture .editor-image-copy').getAttribute('aria-label') === 'Image copied'`, "the copied state of the button");

    // The button of the tools copies it, and so does Ctrl+C in the focused view.
    await page.click(".fixture-picture .editor-image-copy");
    await page.until(`${written}.length === 2`, "the picture copied by the button");
    await page.evaluate(`document.querySelector('.fixture-picture .editor-image-view').focus()`);
    await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: "c", code: "KeyC", windowsVirtualKeyCode: 67, modifiers: 2 });
    await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "c", code: "KeyC", windowsVirtualKeyCode: 67, modifiers: 2 });
    await page.until(`${written}.length === 3`, "the picture copied by Ctrl+C");

    // A drawing offers no copy.
    assert.equal(await page.evaluate(`!document.querySelector('.fixture-drawing .editor-image-copy')`), true);
    await rightClick(page, `document.querySelector('.fixture-drawing .editor-image-view')`);
    assert.equal(await page.evaluate(`!document.querySelector('.session-tab-popup')`), true);
  }, options);
});
