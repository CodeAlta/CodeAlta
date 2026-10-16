import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";

// The images of a message in headless Edge, under the production content security policy and React StrictMode.

const options = { entry: "../timelineImageCopy.mount.tsx", fixture: "imageCopyFixture", width: 1000, height: 700 } as const;
const written = "imageCopyFixture.state.written";
const menuItems = `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.textContent.trim())`;

async function rightClick(page: Page, element: string) {
  const { x, y } = await page.evaluate<{ x: number; y: number }>(`(() => { const box = ${element}.getBoundingClientRect(); return { x: box.x + box.width / 2, y: box.y + box.height / 2 }; })()`);
  await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
  await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "right", buttons: 2, clickCount: 1 });
  await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "right", buttons: 0, clickCount: 1 });
}

test("an image of the timeline is copied from its context menu, and from the window that shows it", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.timeline-image-thumbnail img').length === 2`, "the two thumbnails");

    // A right-click on a thumbnail opens its menu; the image is copied as PNG.
    await rightClick(page, `document.querySelectorAll('.timeline-image-thumbnail')[0]`);
    await page.until(`document.querySelectorAll('.session-tab-popup [role="menuitem"]').length === 2`, "the menu of the image");
    assert.deepEqual(await page.evaluate(menuItems), ["Open image", "Copy image"]);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Copy image");
    await page.until(`${written}.length === 1`, "the copied image");
    assert.deepEqual(await page.evaluate(`${written}[0]`), { types: ["image/png"], type: "image/png", width: 320, height: 200 });
    await page.until(`document.querySelector('.timeline-image-copy-state[data-state="copied"]')`, "the copied mark of the thumbnail");
    assert.equal(await page.evaluate(`!document.querySelector('.session-tab-popup')`), true, "the menu closes");

    // The context menu key opens it too; a JPEG is drawn again as PNG.
    await page.evaluate(`document.querySelectorAll('.timeline-image-thumbnail')[1].focus()`);
    await page.key("ContextMenu", 93);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Copy image");
    await page.until(`${written}.length === 2`, "the copied JPEG");
    assert.deepEqual(await page.evaluate(`${written}[1]`), { types: ["image/png"], type: "image/png", width: 120, height: 90 });

    // "Open image" opens the window; its title bar copies the image, and so does Ctrl+C.
    await rightClick(page, `document.querySelectorAll('.timeline-image-thumbnail')[0]`);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Open image");
    await page.until(`document.querySelector('dialog.timeline-image-dialog[open] .timeline-image-view img')`, "the image window");
    await page.click(".timeline-image-copy");
    await page.until(`${written}.length === 3`, "the image copied from the title bar");
    await page.until(`document.querySelector('.timeline-image-copy').getAttribute('aria-label') === 'Image copied'`, "the copied state of the button");
    await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: "c", code: "KeyC", windowsVirtualKeyCode: 67, modifiers: 2 });
    await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "c", code: "KeyC", windowsVirtualKeyCode: 67, modifiers: 2 });
    await page.until(`${written}.length === 4`, "the image copied by Ctrl+C");

    // A right-click on the image opens its menu inside the window; Escape closes the menu, not the window.
    await rightClick(page, `document.querySelector('.timeline-image-view img')`);
    await page.until(`document.querySelector('dialog.timeline-image-dialog .session-tab-popup')`, "the menu inside the window");
    assert.deepEqual(await page.evaluate(menuItems), ["Copy image"]);
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed");
    assert.equal(await page.evaluate(`!!document.querySelector('dialog.timeline-image-dialog[open]')`), true, "the window stays open");
    await rightClick(page, `document.querySelector('.timeline-image-view img')`);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Copy image");
    await page.until(`${written}.length === 5`, "the image copied from the menu of the window");

    // A clipboard that refuses says so.
    await page.evaluate(`imageCopyFixture.state.refuse = true`);
    await page.click(".timeline-image-copy");
    await page.until(`document.querySelector('.timeline-image-copy').getAttribute('aria-label') === 'Copy failed'`, "the failed state of the button");
    assert.equal(await page.evaluate(`${written}.length`), 5);
  }, options);
});
