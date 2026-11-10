import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { openReviewPage } from "../review/reviewBrowserHarness";
import { testLimit } from "../testTime";

const edge = browserExecutable;

// The buttons of the view of a file take the press of the mouse, and then give the keyboard to the view they show:
// the text after the preview, the page of the preview after the text.
test("the view of a file chosen with the mouse takes the keyboard, the text after the preview and the preview after the text", { skip: !edge, timeout: testLimit(180_000) }, async () => {
  const page = await openReviewPage(new URL("./editorKeyboard.mount.tsx", import.meta.url), "!!window.editorKeyboardFixture && !!document.querySelector('.editor-markdown')",
    new URL("../demo-api.ts", import.meta.url));
  const { until, clickOn, type, frames, errors } = page;
  try {
    const preview = "document.querySelector('.editor-markdown')";
    const where = "[document.activeElement?.tagName, document.activeElement?.className, document.activeElement?.getAttribute('aria-label')]";
    const inText = "!!document.activeElement?.closest('.monaco-editor')";
    // The file opens in its preview, which has the keyboard.
    await until(`document.activeElement === ${preview}`, where);

    await clickOn("document.querySelector('.editor-mode button[aria-label=\"Text\"]')");
    await until(`!${preview} && ${inText}`, where);
    // What is typed goes in the text.
    await frames();
    await type("Typed ");
    await until("document.querySelector('.file-editor-status')?.dataset.state === 'modified'", "document.querySelector('.file-editor-status')?.dataset.state");

    await clickOn("document.querySelector('.editor-mode button[aria-label=\"Preview\"]')");
    await until(`!!${preview} && document.activeElement === ${preview}`, where);
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
});
