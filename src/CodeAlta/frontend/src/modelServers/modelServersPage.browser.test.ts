import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../modelServers/modelServersPage.mount.tsx", fixture: "modelServersFixture", rpcFixture: "../demo-api.ts",
  styles: ["../fleet/console/fleetConsole.css", "../machines/machines.css", "../modelServers/modelServers.css"], width: 1300, height: 900, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
type Call = Readonly<{ machine: string; action: string; [key: string]: unknown }>;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.modelServersFixture.calls]`) as string[]).map(call => JSON.parse(call) as Call);
const clickRow = (table: string, text: string) => `(() => { const row = [...document.querySelectorAll('table[aria-label=${JSON.stringify(table)}] tbody tr[data-row-key]')]
  .find(item => item.textContent.includes(${JSON.stringify(text)})); row.querySelector('td[data-column="machine"]').click(); return true; })()`;

test("the model servers act on several rows at once, copy a server to another machine, and update the machines to one build", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('table[aria-label="Model servers"] tbody tr[data-row-key]').length === 2`, "a server on each machine");

    // Checking a row offers what can be done to the rows checked.
    await page.evaluate(`document.querySelector('input[aria-label="Select Qwen3 1.7B"]').click()`);
    await page.until(`document.querySelector('.model-servers-selection')?.textContent.includes('1 selected')`, "the selection bar");
    await page.clickText(".model-servers-selection .bp6-button", "Stop");
    await page.until(`window.modelServersFixture.calls.some(call => call.includes('"verb":"stop"'))`, "the stop");
    assert.deepEqual((await calls(page)).filter(call => call.action === "controlServer").map(call => [call.machine, call.id, call.verb]), [["local", "qwen3", "stop"]]);
    await page.until(`!document.querySelector('.model-servers-selection')`, "the selection cleared once done");

    // Copy to: the server of pc-ai goes to this computer, on its build (CUDA instead of ROCm), its GPUs and its first free port.
    await page.evaluate(clickRow("Model servers", "pc-ai"));
    await page.clickText(".model-servers-details .bp6-button", "Copy to…");
    await page.until(`document.querySelector('.bp6-dialog')?.textContent.includes('Copy gemma 4 E2B it to another machine')`, "the copy dialog");
    assert.match(await page.evaluate<string>(`document.querySelector('.model-servers-copy-facts').textContent`), /CUDA.*instead of ROCm.*1 to download \(3\.2 GB\)/u);
    await page.clickText(".bp6-dialog-footer .bp6-button", "Copy");
    await page.until(`window.modelServersFixture.calls.some(call => call.includes('"setServer"'))`, "the copy");
    const copied = (await calls(page)).find(call => call.action === "setServer")!;
    const definition = copied.server as Readonly<{ id: string; port: number; backend: string; devices: string[]; models: Readonly<{ model: string }>[] }>;
    assert.deepEqual([copied.machine, definition.id, definition.port, definition.backend, definition.devices, definition.models.map(model => model.model)],
      ["local", "", 0, "cuda", [], ["unsloth/gemma-4-E2B-it-GGUF:UD-Q4_K_XL"]]);

    // Models: two of pc-ai downloaded to this computer at once; the fit this computer measured shows on its model.
    await page.clickText("[role=tab]", "Models");
    await page.until(`document.querySelectorAll('table[aria-label="Models"] tbody tr[data-row-key]').length === 3`, "the models");
    await page.evaluate(`[...document.querySelectorAll('table[aria-label="Models"] input[aria-label$="· pc-ai"]')].forEach(input => input.click())`);
    await page.until(`document.querySelector('.model-servers-selection')?.textContent.includes('2 selected')`, "two models selected");
    await page.clickText(".model-servers-selection .bp6-button", "Download to…");
    await page.until(`document.querySelector('.bp6-dialog')?.textContent.includes('Download 2 models to a machine')`, "the download dialog");
    await page.clickText(".bp6-dialog-footer .bp6-button", "Download");
    await page.until(`window.modelServersFixture.calls.filter(call => call.includes('"action":"download"')).length === 2`, "two downloads");
    assert.deepEqual((await calls(page)).filter(call => call.action === "download").map(call => [call.machine, call.model]),
      [["local", "unsloth/gemma-4-E2B-it-GGUF:UD-Q4_K_XL"], ["local", "unsloth/Qwen3.6-27B-GGUF:UD-Q4_K_XL"]]);
    await page.evaluate(clickRow("Models", "this-pc"));
    await page.until(`document.querySelector('.model-servers-details')?.textContent.includes('Up to 40,960 tokens of context')`, "the measured fit");
    assert.match(await page.evaluate<string>(`document.querySelector('.model-servers-devices').textContent`), /CUDA0.*model 1\.1 GB, context 4\.7 GB, buffers 369 MB/u);

    // llama.cpp: both machines to one build, one after the other.
    await page.clickText("[role=tab]", "llama.cpp");
    await page.clickText(".bp6-tab-list .bp6-button", "Update all now…");
    await page.until(`document.querySelector('.bp6-dialog')?.textContent.includes('Update llama.cpp now')`, "the update dialog");
    await page.evaluate(type(".bp6-dialog input[list=model-update-builds]", "12110"));
    await page.until(`document.querySelector('.bp6-dialog [role=alert]')?.textContent.includes('b12110')`, "a build that is not one is refused");
    await page.evaluate(type(".bp6-dialog input[list=model-update-builds]", "b12110"));
    await page.clickText(".bp6-dialog-footer .bp6-button", "Update");
    await page.until(`window.modelServersFixture.calls.filter(call => call.includes('"updateNow"')).length === 2`, "both machines updated in turn");
    assert.deepEqual((await calls(page)).filter(call => call.action === "updateNow").map(call => [call.machine, call.build]), [["local", "b12110"], ["pcai", "b12110"]]);
    await page.until(`document.body.textContent.includes('All the machines are updated.')`, "the notice");

    // The machines checked update in the order of the table, sorted by machine: pc-ai first, though the page lists this computer first.
    await page.until(`document.querySelector('table[aria-label="llama.cpp"] tbody tr[data-row-key]')?.getAttribute('data-row-key') === 'pcai'`, "pc-ai first");
    await page.evaluate(`[...document.querySelectorAll('table[aria-label="llama.cpp"] input[aria-label^="Select"]')].forEach(input => input.click())`);
    await page.clickText(".model-servers-selection .bp6-button", "Update now…");
    await page.until(`document.querySelector('.bp6-dialog .muted-text')?.textContent === 'pc-ai, this-pc'`, "the machines in the order of the table");
    await page.clickText(".bp6-dialog-footer .bp6-button", "Update");
    await page.until(`window.modelServersFixture.calls.filter(call => call.includes('"updateNow"')).length === 4`, "both again");
    assert.deepEqual((await calls(page)).filter(call => call.action === "updateNow").slice(2).map(call => call.machine), ["pcai", "local"]);

    // The policy offers Lemonade's ROCm builds.
    await page.clickText(".bp6-tab-list .bp6-button", "Policy of all…");
    await page.until(`document.querySelector('.bp6-dialog')?.textContent.includes("Use Lemonade's ROCm builds")`, "the policy dialog");
    await page.evaluate(`[...document.querySelectorAll('.bp6-dialog label.bp6-control')].find(label => label.textContent.includes('Lemonade')).querySelector('input').click()`);
    await page.clickText(".bp6-dialog-footer .bp6-button", "Save");
    await page.until(`window.modelServersFixture.calls.filter(call => call.includes('"setSettings"')).length === 2`, "the policy saved on both");
    assert.ok((await calls(page)).filter(call => call.action === "setSettings")
      .every(call => (call.settings as Readonly<{ updates: Readonly<{ allowLemonadeRocm: boolean }> }>).updates.allowLemonadeRocm));
    assert.deepEqual(page.problems, []);
  }, options);
});
