import assert from "node:assert/strict";
import test from "node:test";
import { createModelPickerPreferences, defaultModelPickerRows, matchingModels, maximumModelPickerRows, minimumModelPickerRows, modelAfterKey, modelPickerRows, modelPickerRowsKey } from "./modelPicker";

function memory(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  const written: [string, string][] = [];
  return { values, written, getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { written.push([key, value]); values.set(key, value); } };
}

const models = [
  { id: "gpt-6.1-sol", name: "GPT-6.1 Sol" }, { id: "gpt-6.1-mini", name: "GPT-6.1 Mini" },
  { id: "claude-opus-5-5", name: "Opus 5.5" }, { id: "claude-haiku-4-5", name: "Haiku 4.5" },
];

test("a search keeps the models whose name or id holds every word, in the order of the provider", () => {
  assert.equal(matchingModels(models, ""), models, "nothing searched lists everything");
  assert.equal(matchingModels(models, "   "), models);
  assert.deepEqual(matchingModels(models, "gpt").map(model => model.id), ["gpt-6.1-sol", "gpt-6.1-mini"]);
  assert.deepEqual(matchingModels(models, "MINI").map(model => model.id), ["gpt-6.1-mini"]);
  assert.deepEqual(matchingModels(models, "claude 4").map(model => model.id), ["claude-haiku-4-5"], "the words are found in the id as in the name");
  assert.deepEqual(matchingModels(models, "5 opus").map(model => model.id), ["claude-opus-5-5"], "in any order");
  assert.deepEqual(matchingModels(models, "gemini"), []);
});

test("the arrows move by one model and the page keys by what the list shows, without leaving it", () => {
  assert.equal(modelAfterKey("ArrowDown", 0, 4, 10), 1);
  assert.equal(modelAfterKey("ArrowDown", 3, 4, 10), 3);
  assert.equal(modelAfterKey("ArrowUp", 0, 4, 10), 0);
  assert.equal(modelAfterKey("ArrowUp", 2, 4, 10), 1);
  assert.equal(modelAfterKey("PageDown", 2, 40, 10), 12);
  assert.equal(modelAfterKey("PageDown", 35, 40, 10), 39);
  assert.equal(modelAfterKey("PageUp", 12, 40, 10), 2);
  assert.equal(modelAfterKey("PageUp", 3, 40, 10), 0);
  for (const key of ["Home", "End", "Enter", "a", "ArrowLeft"]) assert.equal(modelAfterKey(key, 1, 4, 10), null, key);
  assert.equal(modelAfterKey("ArrowDown", 0, 0, 10), null, "nothing moves in an empty list");
});

test("a number of models is a whole number between the two limits", () => {
  assert.deepEqual([minimumModelPickerRows, defaultModelPickerRows, maximumModelPickerRows], [4, 10, 30]);
  for (const value of [4, 10, 30, "4", " 12 ", "30"]) assert.equal(modelPickerRows(value), Number(value), JSON.stringify(value));
  for (const value of [3, 31, 0, -5, 10.5, Number.NaN, "", "ten", "10.5", "1e1", "0x10", null, undefined, true, {}]) assert.equal(modelPickerRows(value), null, JSON.stringify(value));
});

test("what was kept is read, and the default stands for anything else", () => {
  assert.equal(modelPickerRowsKey, "codealta.desktop.modelPicker.rows.v1");
  assert.equal(createModelPickerPreferences(memory()).get(), 10);
  assert.equal(createModelPickerPreferences(null).get(), 10);
  assert.equal(createModelPickerPreferences(memory({ [modelPickerRowsKey]: "16" })).get(), 16);
  for (const garbage of ["", "3", "31", "many", "12.5", "-8"]) assert.equal(createModelPickerPreferences(memory({ [modelPickerRowsKey]: garbage })).get(), 10, JSON.stringify(garbage));
  assert.equal(createModelPickerPreferences({ getItem: () => { throw new Error("denied"); }, setItem: () => { } }).get(), 10);
});

test("a choice is kept and told once to whoever listens; one the list does not take changes nothing", () => {
  const storage = memory();
  const store = createModelPickerPreferences(storage);
  let told = 0;
  const stop = store.subscribe(() => { told++; });
  store.set(15);
  assert.equal(store.get(), 15);
  assert.deepEqual(storage.written, [[modelPickerRowsKey, "15"]]);
  assert.equal(told, 1);
  store.set(15); store.set(3); store.set(31); store.set(12.5);
  assert.equal(store.get(), 15);
  assert.deepEqual(storage.written, [[modelPickerRowsKey, "15"]], "nothing is written again");
  assert.equal(told, 1);
  stop();
  store.set(6);
  assert.equal(told, 1, "one that stopped listening is told nothing more");
  assert.equal(createModelPickerPreferences(storage).get(), 6, "the next start of the window reads what was kept");
});

test("a storage that cannot be written keeps the choice for this run", () => {
  const full = createModelPickerPreferences({ getItem: () => "8", setItem: () => { throw new Error("quota"); } });
  assert.equal(full.get(), 8);
  full.set(20);
  assert.equal(full.get(), 20);
});
