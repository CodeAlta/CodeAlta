import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { openReviewPage } from "../review/reviewBrowserHarness";
import { testLimit } from "../testTime";

test("only a terminal can be renamed to a blank title", { skip: !browserExecutable, timeout: testLimit(90_000) }, async t => {
  const form = `document.querySelector(".rename-form")`;
  const input = `${form}?.querySelector("input")`;
  const submit = `${form}.querySelector('button[type="submit"]')`;
  const requests = `JSON.parse(document.querySelector("#rename-requests").textContent)`;
  const rowTitle = `document.querySelector(".terminal-row bdi")?.textContent`;
  const page = await openReviewPage(new URL("./terminalRename.mount.tsx", import.meta.url), rowTitle);
  try {
    await page.command("Emulation.setFocusEmulationEnabled", { enabled: true });
    const click = (selector: string) => page.evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const clear = async (trigger: string, title: string) => {
      await click(trigger);
      await page.until(`${input} && document.activeElement === ${input} && ${input}.selectionEnd - ${input}.selectionStart === ${title.length}`);
      assert.equal(await page.evaluate(`${input}.value`), title);
      for (const type of ["keyDown", "keyUp"]) {
        await page.command("Input.dispatchKeyEvent", { type, key: "Backspace", code: "Backspace", windowsVirtualKeyCode: 8 });
      }
      await page.until(`${input}?.value === ""`);
    };

    for (const kind of ["session", "project"]) {
      await t.test(`${kind} rename rejects empty and whitespace-only drafts`, async () => {
        await clear(`#edit-${kind}`, "Existing name");
        assert.equal(await page.evaluate(`${submit}.disabled`), true);
        await page.type("  ");
        await page.until(`${input}?.value === "  "`);
        assert.equal(await page.evaluate(`${submit}.disabled`), true);
        // Even when the form is submitted without its disabled button, no rename is sent.
        await page.evaluate(`${form}.requestSubmit()`);
        assert.deepEqual(await page.value(requests), []);
        await click(".rename-actions button:first-child");
        await page.until(`${form} === null`);
      });
    }

    await t.test("clearing a terminal title sends an empty title and restores the terminal's own title", async () => {
      await clear('button[aria-label="Rename Build shell"]', "Build shell");
      await page.evaluate(`${submit}.click()`);
      assert.deepEqual(await page.value(requests), [{ id: "terminal", title: "" }], "the host is asked to reset the terminal title");
      await page.until(`${rowTitle} === "/work/build" && ${form} === null`);
    });
    assert.deepEqual(page.errors, []);
  } finally {
    await page.close();
  }
});
