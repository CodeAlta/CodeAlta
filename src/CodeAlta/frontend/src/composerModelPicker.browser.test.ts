import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../composerModelPicker.mount.tsx", fixture: "pickerFixture", rpcFixture: "../demo-api.ts", width: 1200, height: 900 } as const;
const listed = `[...document.querySelectorAll('.composer-model-picker [role="option"]')].map(option => option.textContent)`;
// A popover grows into place: its list is measured once it has its size.
const opened = `document.querySelector('.composer-model-picker')?.closest('.bp6-popover-enter-done, .bp6-popover-appear-done')`;
// The form of the chip too: a press aimed at its model button while it grows lands beside the button.
const form = `document.querySelector('#composer-model-test')?.closest('.bp6-popover-enter-done, .bp6-popover-appear-done')`;
const active =`document.querySelector('.composer-model-picker .bp6-active')?.closest('[data-model-id]')?.dataset.modelId`;

test("the model list of the composer searches, shows the chosen number of models and is driven from its search box", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.click('[aria-label="Session configuration"]');
    await page.until(form, "the form of the chip at its size");
    assert.equal(await page.evaluate(`document.querySelector('#composer-model-test').textContent`), "Model 25");
    await page.click('#composer-model-test');
    await page.until(`document.querySelectorAll('.composer-model-picker [role="option"]').length === 40`, "every model of the provider");
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search models'`, "the focus in the search box");

    // Ten models are shown, the list scrolls for the others, and it opens on the model in use.
    await page.until(opened, "the list at its size");
    const shape = await page.evaluate<{ list: number; row: number; scrolls: boolean; inView: boolean }>(`(() => {
      const list = document.querySelector('.composer-model-picker [role="listbox"]'), box = list.getBoundingClientRect();
      const row = list.querySelector('[role="option"]').getBoundingClientRect(), current = list.querySelector('[aria-selected="true"]').getBoundingClientRect();
      return { list: box.height, row: row.height, scrolls: list.scrollHeight > list.clientHeight, inView: current.top >= box.top - 1 && current.bottom <= box.bottom + 1 };
    })()`);
    assert.equal(Math.round(shape.list / shape.row), 10, JSON.stringify(shape));
    assert.ok(shape.scrolls && shape.inView, JSON.stringify(shape));
    assert.equal(await page.evaluate(active), "vendor/model-25");

    // The search narrows the list; the arrows and Enter choose without leaving the box.
    await page.type("model 3");
    await page.until(`${listed}.length === 13`, "the models that match");
    assert.equal(await page.evaluate(`${listed}[0].startsWith('Model 3')`), true);
    assert.equal(await page.evaluate(active), "vendor/model-3");
    await page.key("ArrowDown", 40); await page.key("ArrowDown", 40); await page.key("ArrowUp", 38);
    assert.equal(await page.evaluate(active), "vendor/model-13");
    assert.equal(await page.evaluate(`(() => { const option = document.querySelector('.composer-model-picker .bp6-active').closest('[role="option"]');
      return !!option.id && document.activeElement?.getAttribute('aria-activedescendant') === option.id; })()`), true, "the search box names the active option");
    await page.key("Enter", 13);
    await page.until(`!document.querySelector('.composer-model-picker')`, "the list closed by the choice");
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["vendor/model-13"]);
    assert.equal(await page.evaluate(`document.querySelector('#composer-model-test').textContent`), "Model 13");
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-form')`), true, "the form of the chip stays open");

    // A search that finds nothing says so, and Escape closes the list only.
    await page.click('#composer-model-test');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search models'`, "the list again");
    assert.equal(await page.evaluate(`document.activeElement.value`), "", "nothing is searched in a list that opens");
    await page.type("gemini");
    await page.until(`document.querySelector('.composer-model-picker [role="status"]')?.textContent === 'No models match this search.'`, "no match");
    await page.key("Enter", 13);
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.composer-model-picker')`, "the list closed by Escape");
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-form')`), true);
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["vendor/model-13"]);

    // A click chooses as well, and choosing the model in use changes nothing.
    await page.click('#composer-model-test');
    await page.until(`document.querySelector('.composer-model-picker [data-model-id="vendor/model-13"]')`, "the list a third time");
    await page.until(opened, "the list at its size");
    await page.click('.composer-model-picker [data-model-id="vendor/model-13"]');
    await page.until(`!document.querySelector('.composer-model-picker')`, "the list closed by the click");
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["vendor/model-13"]);
  }, options);
});

test("the setting of the model list changes how many models it shows, and a short list does not scroll", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const rows = `(() => { const list = document.querySelector('.composer-model-picker [role="listbox"]'); return Math.round(list.getBoundingClientRect().height / list.querySelector('[role="option"]').getBoundingClientRect().height); })()`;
    await page.click('#settings-model-picker-rows');
    await page.key("a", 65); // Not a digit: the field takes numbers only.
    await page.evaluate(`document.querySelector('#settings-model-picker-rows').select()`);
    await page.type("6");
    await page.until(`document.querySelector('#settings-model-picker-rows').value === '6'`, "six models");
    await page.click('[aria-label="Session configuration"]');
    await page.until(form, "the form of the chip at its size");
    await page.click('#composer-model-test');
    await page.until(`document.querySelectorAll('.composer-model-picker [role="option"]').length === 40`, "the list");
    await page.until(opened, "the list at its size");
    assert.equal(await page.evaluate(rows), 6);
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.composer-model-picker')`, "the list closed");

    // Three models: the list is as high as they are.
    await page.evaluate(`pickerFixture.listed = 3; pickerFixture.apply()`);
    await page.click('#composer-model-test');
    await page.until(`document.querySelectorAll('.composer-model-picker [role="option"]').length === 3`, "three models");
    assert.equal(await page.evaluate(`(() => { const list = document.querySelector('.composer-model-picker [role="listbox"]'); return list.scrollHeight <= list.clientHeight; })()`), true);
    assert.equal(await page.evaluate(`document.querySelector('#composer-model-test').textContent`), "vendor/model-25 · Unverified", "a saved model the provider does not list is named on the button");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.composer-model-picker')`, "the list closed");

    // While the models are read, the button waits and opens nothing.
    await page.evaluate(`pickerFixture.loading = true; pickerFixture.apply()`);
    await page.until(`document.querySelector('#composer-model-test').disabled`, "a waiting button");
    assert.equal(await page.evaluate(`document.querySelector('#composer-model-test').textContent`), "Loading…");
  }, options);
});
