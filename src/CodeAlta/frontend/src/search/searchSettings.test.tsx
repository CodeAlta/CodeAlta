import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { LocalSearchMeaning, LocalSearchSettings } from "#neoastra";
import { MeaningCard } from "./SearchSettings";

const noop = () => {};
const settings: LocalSearchSettings = { enabled: true, sessions: true, semantic: true, model: "embeddinggemma-2", excludedProjects: [], excludedSpaces: [], parallelism: 3, maximumParallelism: 7 };
const meaning: LocalSearchMeaning = { supported: true, enabled: true, state: "indexing", modelId: "embeddinggemma-2", embedded: 400, total: 2000, secondsLeft: 60,
  chunksPerSecondTenths: 280, error: null, download: { state: "done", modelId: null, receivedKilobytes: 0, totalKilobytes: 0, error: null }, runtimeInstalled: true,
  models: [{ id: "embeddinggemma-2", name: "EmbeddingGemma 2", summary: "code and prose", author: "Google", license: "Apache-2.0", kilobytes: 321_000, installed: true }],
  offer: false, sessions: 40 };
const card = (shown: LocalSearchMeaning) => renderToStaticMarkup(<MeaningCard meaning={shown} settings={settings} busy={false} onOpen={noop} onTurnOff={noop}
  onModel={noop} onParallelism={noop} onPause={noop} onCancel={noop} onDelete={noop} onRetry={noop} />);

test("the batches at once are chosen from one to the most of this computer, the default named, while search by meaning is on", () => {
  const html = card(meaning);
  const select = html.match(/<select[^>]*id="settings-search-parallelism"[^>]*>(.*?)<\/select>/)?.[1];
  assert.ok(select, "the choice is there during the indexing");
  const options = [...select.matchAll(/<option value="(\d+)"( selected="")?>([^<]*)<\/option>/g)];
  assert.deepEqual(options.map(option => option[1]), ["1", "2", "3", "4", "5", "6", "7"]);
  assert.equal(options.find(option => option[2])?.[1], "3", "the setting is selected");
  assert.equal(options[1][3], "2 (default)");
  assert.ok(html.includes(">Batches at once<"));
  assert.ok(!card({ ...meaning, enabled: false, state: "off" }).includes("settings-search-parallelism"), "not while search by meaning is off");
});
