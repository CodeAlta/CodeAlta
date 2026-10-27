import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../applicationLogs.mount.tsx", fixture: "logsFixture", rpcFixture: "../demo-api.ts", width: 1300, height: 900 } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const optionTexts = (selector: string) => `[...document.querySelector(${JSON.stringify(selector)}).options].map(option => option.textContent)`;
const shown = `[...document.querySelectorAll('.logs-view .log-line .log-text')].map(line => line.textContent)`;

test("the log viewer filters by feature, with the rows of each one counted, and by level and words with it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.logs-view .log-line').length === 4 && document.querySelector('.logs-feature-filter select')?.options.length === 5`, "the rows and the features");
    assert.deepEqual(await page.evaluate(optionTexts(".logs-feature-filter select")),
      ["All features", "System (1)", "Providers (1)", "Issue trackers (2)", "Git (0)"]);

    await page.evaluate(choose(".logs-feature-filter select", "issues"));
    await page.until(`document.querySelectorAll('.logs-view .log-line').length === 2`, "the rows of the issue trackers");
    assert.deepEqual(await page.evaluate(shown), ["GET https://dev.azure.com/org/proj/_apis/wit/wiql answered 203",
      "Azure DevOps org/proj/app: listing the issues shows: Azure DevOps asks to sign in."]);
    assert.equal(await page.evaluate(`document.querySelector('.logs-view .log-logger').title`), "Issue trackers");

    await page.clickText(".logs-toolbar .bp6-button", "Warnings");
    await page.until(`document.querySelectorAll('.logs-view .log-line').length === 1`, "the warnings of the issue trackers");
    await page.clickText(".logs-toolbar .bp6-button", "All");
    await page.evaluate(choose(".logs-feature-filter select", "git"));
    await page.until(`document.querySelector('.logs-empty')?.textContent === 'No message matches the filter.'`, "nothing for Git");
    assert.match(await page.evaluate(`document.querySelector('.logs-summary').textContent`) as string, /^0 of 4 messages/);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the level of a feature is chosen in the log levels and saved at once, the others keeping the level of every feature", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.logs-view .log-line').length === 4`, "the rows");
    await page.clickText(".logs-toolbar .bp6-button", "Log levels");
    await page.until(`document.querySelector('#logs-level-issues')`, "the levels");
    assert.equal(await page.evaluate(`document.querySelector('#logs-level-all').value`), "info");
    assert.deepEqual(await page.evaluate(optionTexts("#logs-level-issues")), ["Default (Info)", "Trace", "Debug", "Info", "Warning", "Error"]);
    assert.equal(await page.evaluate(`document.querySelector('#logs-level-issues').value`), "");

    await page.evaluate(choose("#logs-level-issues", "debug"));
    await page.until(`logsFixture.saved.length === 1 && document.querySelector('#logs-level-issues').value === 'debug' && !document.querySelector('#logs-level-issues').disabled`, "the save of Debug");
    assert.deepEqual(await page.evaluate(`logsFixture.saved[0]`), { expectedEpoch: "epoch", level: "info", features: [{ id: "issues", level: "debug" }] });

    await page.evaluate(choose("#logs-level-all", "warn"));
    await page.until(`logsFixture.saved.length === 2 && document.querySelector('#logs-level-all').value === 'warn' && !document.querySelector('#logs-level-all').disabled`, "the save of every feature");
    assert.deepEqual(await page.evaluate(`logsFixture.saved[1]`), { expectedEpoch: "epoch", level: "warn", features: [{ id: "issues", level: "debug" }] });
    assert.equal(await page.evaluate(`document.querySelector('#logs-level-git').options[0].textContent`), "Default (Warning)");

    await page.evaluate(choose("#logs-level-issues", ""));
    await page.until(`logsFixture.saved.length === 3`, "the issue trackers back to every feature");
    assert.deepEqual(await page.evaluate(`logsFixture.saved[2]`), { expectedEpoch: "epoch", level: "warn", features: [] });
    assert.deepEqual(page.problems, []);
  }, options);
});
