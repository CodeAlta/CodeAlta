import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";

// What a script presented (`present()`) on the cards of its run, in headless Edge under React StrictMode, in both themes,
// in a 1600×1000 and a 900×650 window: the block in the timeline as the parts come, a part read whole, the card of the
// result of a run of an agent (the parts, its status, Open run, its value folded as plain text), the card of a run the user
// ran from the library (its value as Markdown), and the tab of the run.

const options = { entry: "../workflows/presented.mount.tsx", fixture: "presentedFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000,
  styles: ["../timelineActivity/timelineActivity.css", "../scriptCard/scriptCard.css", "../workflows/workflows.css", "../automations/automations.css",
    "../../node_modules/@xyflow/react/dist/style.css"] } as const;

const pictures = join(process.env.TEMP ?? tmpdir(), "ca-codemode", "ui");
async function shot(page: Page, name: string) {
  await mkdir(pictures, { recursive: true });
  const picture = await page.command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
  await writeFile(join(pictures, `${name}.png`), Buffer.from(picture.data, "base64"));
}
const texts = (selector: string) => `[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => e.textContent.trim())`;
// Whether nothing of the page scrolls sideways: every element of a selector fits its own width and the window's.
const fits = (selector: string) => `[...document.querySelectorAll(${JSON.stringify(selector)})].every(e => e.scrollWidth <= e.clientWidth + 1
  && e.getBoundingClientRect().right <= document.documentElement.clientWidth + 1)`;

for (const light of [false, true]) {
  const theme = light ? "light" : "dark";
  test(`the block of a run shows its parts as they come, and reads a shortened one whole (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`presentedFixture.theme(${light}); presentedFixture.renderBlock(1180, 2)`);
      await page.until(`document.querySelectorAll('.workflow-block .script-presented-part').length === 1`, "the first part");
      assert.deepEqual(await page.evaluate(texts(".script-presented-part h2")), ["Reading"]);
      assert.match(await page.evaluate<string>(`document.querySelector('.workflow-pill').textContent`), /^Running/u);
      // The next records come: the next part shows under the first, in the step that presented it.
      await page.evaluate(`presentedFixture.renderBlock(1180, 6)`);
      await page.until(`document.querySelectorAll('.workflow-block .script-presented-part').length === 2`, "the second part");
      assert.deepEqual(await page.evaluate(texts(".script-presented-part h2")), ["Reading", "Findings"]);
      assert.equal(await page.evaluate(`document.querySelectorAll('.script-presented-part')[1].dataset.step`), "review");
      assert.equal(await page.evaluate(`document.querySelectorAll('.script-presented table tbody tr').length`), 2, "Markdown, a table included");
      assert.ok(await page.evaluate<boolean>(fits(".workflow-block, .script-presented-part")), "nothing scrolls sideways");
      await page.clickText(".message-shortened-load", "Show the whole message");
      await page.until(`document.querySelectorAll('.script-presented-part li').length === 12`, "the part read whole");
      assert.equal(await page.evaluate(`presentedFixture.state.reads`), 1);
      await shot(page, `presented-block-${theme}`);

      await page.resize(900, 650);
      await page.evaluate(`presentedFixture.renderBlock(860, 6)`);
      await page.until(`document.querySelectorAll('.workflow-block .script-presented-part').length === 2`, "the parts, narrow");
      assert.ok(await page.evaluate<boolean>(fits(".workflow-block, .script-presented-part")), "nothing scrolls sideways at 860 px");
      await shot(page, `presented-block-860-${theme}`);
    }, options);
  });

  test(`the result of a run of an agent folds its value as plain text; a run of the user keeps it as Markdown (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`presentedFixture.theme(${light}); presentedFixture.renderResult(1180, false)`);
      await page.until(`!!document.querySelector('.script-result-facts')`, "the card of the result");
      assert.deepEqual(await page.evaluate(texts(".message-user .script-presented-part h2")), ["Reading", "Findings"], "what the run presented, from its records");
      assert.equal(await page.evaluate(`document.querySelector('.script-result-outcome').textContent`), "Script completed · 4.0 s · 2 calls");
      assert.deepEqual(await page.evaluate(texts(".script-result-open")), ["Open run"]);
      // The value is folded, and no Markdown: its text is plain, in the font of code.
      assert.equal(await page.evaluate(`document.querySelector('.script-result-value').open`), false);
      assert.equal(await page.evaluate(`document.querySelector('.message-user .message-body > .markdown-content')`), null, "no body");
      await page.click(".script-result-value > summary");
      await page.until(`document.querySelector('.script-result-value').open`, "the value open");
      const shown = await page.evaluate<string>(`document.querySelector('.script-result-text').textContent`);
      assert.match(shown, /^\{\n {2}"issues": 2,/u);
      assert.match(shown, /\/\/ <summary> \*not\* Markdown/u, "as written");
      assert.match(await page.evaluate<string>(`getComputedStyle(document.querySelector('.script-result-text')).fontFamily`), /mono|consol|cascadia|courier/iu);
      await page.click(".script-result-open");
      await page.until(`presentedFixture.state.runs.length === 1`, "the tab of the run asked for");
      await shot(page, `presented-result-${theme}`);

      await page.resize(900, 650);
      await page.evaluate(`presentedFixture.renderResult(860, false)`);
      await page.until(`!!document.querySelector('.script-result-facts')`, "the card, narrow");
      await page.click(".script-result-value > summary");
      assert.ok(await page.evaluate<boolean>(fits(".message-user .message-body, .script-presented-part")), "nothing scrolls sideways at 860 px");
      await shot(page, `presented-result-860-${theme}`);

      await page.resize(1600, 1000);
      await page.evaluate(`presentedFixture.renderResult(1180, true)`);
      await page.until(`!!document.querySelector('.message-user .markdown-content h2')`, "the value of a run of the user");
      assert.deepEqual(await page.evaluate(texts(".message-user .markdown-content h2")), ["Reading", "Findings", "Review of PR 231"]);
      assert.equal(await page.evaluate(`document.querySelector('.script-result-value')`), null, "not folded");
      await shot(page, `presented-result-user-${theme}`);
    }, options);
  });
}

test("the tab of a run shows what it presented, and its value as plain text or, for a run of the user, as Markdown", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`presentedFixture.theme(false); presentedFixture.renderTab(false)`);
    await page.until(`document.querySelectorAll('.workflow-run-section h3').length >= 4`, "the tab of the run");
    assert.deepEqual(await page.evaluate(texts(".workflow-run-section h3")), ["Timeline", "Presented", "Result", "Script"]);
    assert.equal(await page.evaluate(`document.querySelectorAll('.workflow-run-section .script-presented-part li').length`), 12, "the parts whole");
    assert.match(await page.evaluate<string>(`document.querySelector('.workflow-run-value').textContent`), /^\{\n {2}"issues": 2,/u);
    await shot(page, "presented-tab");
    await page.evaluate(`presentedFixture.clear(); presentedFixture.renderTab(true)`);
    await page.until(`!!document.querySelector('.workflow-run-result .markdown-content h2')`, "the value of a run of the user");
    assert.equal(await page.evaluate(`document.querySelector('.workflow-run-result h2').textContent`), "Review of PR 231");
    assert.equal(await page.evaluate(`document.querySelector('.workflow-run-value')`), null);
  }, options);
});
