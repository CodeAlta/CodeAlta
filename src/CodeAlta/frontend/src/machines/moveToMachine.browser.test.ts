import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../machines/moveToMachine.mount.tsx", fixture: "moveToMachineFixture", rpcFixture: "../demo-api.ts", styles: ["../machines/machines.css"], width: 900,
  height: 700, reducedMotion: true } as const;
const checked = `[...document.querySelectorAll('.move-machine-targets input')].filter(input => input.checked).map(input => input.value)`;

test("the machine the person chooses stays chosen, and the machines are read once", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.move-machine-project')?.textContent.includes('/home/me/mac-id/markdig')`, "the preview of the first machine online");
    assert.deepEqual(await page.evaluate(checked), ["mac-id"]);
    await page.evaluate(`[...document.querySelectorAll('.move-machine-targets input')].find(input => input.value === 'pc-ai-id').click()`);
    await page.until(`document.querySelector('.move-machine-project')?.textContent.includes('/home/me/pc-ai-id/markdig')`, "the preview of the chosen machine");
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.deepEqual(await page.evaluate(checked), ["pc-ai-id"], "the choice does not snap back to the first machine");
    const calls = await page.evaluate(`[...window.moveToMachineFixture.calls]`) as string[];
    assert.ok(calls.filter(call => call === "list").length <= 2, `the machines are listed once (twice under StrictMode), not at each render: ${calls.join(", ")}`);
    assert.equal(calls.at(-1), "preview pc-ai-id");
    assert.deepEqual(page.problems, []);
  }, options);
});
