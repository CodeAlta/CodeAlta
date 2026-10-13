import assert from "node:assert/strict";
import test from "node:test";
import type { SpeechListingResponse } from "#neoastra";
import { chosenModel, createSpeechStore, dictationFailure, initialSpeechState, installFailure, installNotice, installProgress, installView, micView,
  speechEnabled, withDictation, withInstall, type InstallView, type SpeechState } from "./speechStore";
import { fixtureModels } from "./speechFixture";
import { locales, messages, translate } from "../localization";

const listing = (change: Partial<SpeechListingResponse> = {}): SpeechListingResponse => ({ status: "ok", supported: true, enabled: true, model: "parakeet-tdt-0.6b-v3",
  modelId: "parakeet-tdt-0.6b-v3", models: fixtureModels, rootPath: "/cache/speech", send: "manual", error: null, ...change });
const install = (change: Partial<InstallView> = {}): InstallView => ({ state: "idle", modelId: null, modelName: null, received: 0, total: 0, error: null, ...change });
const state = (change: Partial<SpeechState> = {}): SpeechState => ({ ...initialSpeechState, listing: listing(), ...change });

test("the button is absent where there is nothing to dictate with", () => {
  assert.equal(micView(initialSpeechState, "a", true), "hidden", "not read yet");
  assert.equal(micView(state(), "a", true), "idle");
  assert.equal(micView(state(), "a", false), "hidden", "a page with no microphone");
  for (const change of [{ enabled: false }, { supported: false }, { status: "unavailable" }, { status: "stale_epoch" }, { status: "read_failed" }])
    assert.equal(micView(state({ listing: listing(change) }), "a", true), "hidden", JSON.stringify(change));
  assert.equal(speechEnabled(state()), true);
  assert.equal(speechEnabled(state({ listing: null })), false);
});

test("the button of a composer shows its own dictation, and the others stay as they are", () => {
  for (const status of ["starting", "recording", "finishing"] as const) {
    const dictating = state({ dictation: { status, owner: "a", partial: "", microphone: null } });
    assert.equal(micView(dictating, "a", true), status);
    assert.equal(micView(dictating, "b", true), "idle");
  }
  // The download of the chosen model, and only of it.
  const downloading = state({ install: install({ state: "downloading", modelId: "parakeet-tdt-0.6b-v3" }) });
  assert.equal(micView(downloading, "a", true), "installing");
  assert.equal(micView(state({ install: install({ state: "installing", modelId: "parakeet-tdt-0.6b-v3" }) }), "a", true), "installing");
  assert.equal(micView(state({ install: install({ state: "downloading", modelId: "whisper-small" }) }), "a", true), "idle");
  for (const ended of ["done", "failed", "canceled", "idle"] as const)
    assert.equal(micView(state({ install: install({ state: ended, modelId: "parakeet-tdt-0.6b-v3" }) }), "a", true), "idle", ended);
});

test("what the host says of a download is read as numbers, and an unknown state as no download", () => {
  assert.deepEqual(installView({ state: "downloading", modelId: "m", modelName: "Model", receivedBytes: "222298112", totalBytes: "713031680", error: null }),
    { state: "downloading", modelId: "m", modelName: "Model", received: 222298112, total: 713031680, error: null });
  assert.equal(installView({ state: "exploding", modelId: null, modelName: null, receivedBytes: "x", totalBytes: "-4", error: null }).state, "idle");
  assert.deepEqual([installView({ state: "idle", modelId: null, modelName: null, receivedBytes: "x", totalBytes: "-4", error: null })].map(value => [value.received, value.total]), [[0, 0]]);
  assert.equal(installProgress(install({ state: "downloading", received: 50, total: 200 })), .25);
  assert.equal(installProgress(install({ state: "downloading", received: 500, total: 200 })), 1);
  assert.equal(installProgress(install({ state: "downloading", received: 50, total: 0 })), null, "the size is not known yet");
  assert.equal(installProgress(install({ state: "installing", received: 200, total: 200 })), null, "installing has no percentage");
  assert.equal(installProgress(null), null);
});

test("the notice of a download: progress while it runs, its end told once, and never the end of an earlier one", () => {
  const running = install({ state: "downloading", modelId: "m", modelName: "Parakeet", received: 10, total: 100 });
  // The first state is a replay of what the host holds.
  assert.equal(installNotice(null, install({ state: "done" })), null);
  assert.equal(installNotice(null, install({ state: "failed", error: "network" })), null);
  assert.equal(installNotice(null, install({ state: "canceled" })), null);
  assert.equal(installNotice(null, install()), null);
  assert.deepEqual(installNotice(null, running), { kind: "progress", phase: "downloading", model: "Parakeet", received: 10, total: 100 });
  // While it runs the notice is kept up to date.
  assert.deepEqual(installNotice(running, { ...running, received: 60 }), { kind: "progress", phase: "downloading", model: "Parakeet", received: 60, total: 100 });
  assert.deepEqual(installNotice(running, { ...running, state: "installing" }), { kind: "progress", phase: "installing", model: "Parakeet", received: 10, total: 100 });
  assert.equal(installNotice(null, install({ state: "downloading", modelId: "owner--repo" }))?.kind === "progress" && (installNotice(null, install({ state: "downloading", modelId: "owner--repo" })) as { model: string }).model, "owner--repo");
  // An end the page sees happen.
  assert.deepEqual(installNotice(running, install({ state: "done" })), { kind: "ready" });
  assert.deepEqual(installNotice(install({ state: "installing" }), install({ state: "done" })), { kind: "ready" });
  assert.deepEqual(installNotice(running, install({ state: "canceled" })), { kind: "dismiss" });
  assert.deepEqual(installNotice(running, install()), { kind: "dismiss" });
  assert.deepEqual(installNotice(running, install({ state: "failed", error: "busy_elsewhere" })), { kind: "failed", message: "Another CodeAlta is downloading this speech model." });
  // A download that failed before the page saw it run is still an end it sees.
  assert.deepEqual(installNotice(install(), install({ state: "failed", error: "disk" })), { kind: "failed", message: "The speech model could not be written to the disk." });
  // Told again, an end says nothing more.
  assert.equal(installNotice(install({ state: "done" }), install({ state: "done" })), null);
  assert.equal(installNotice(install({ state: "failed", error: "network" }), install({ state: "failed", error: "network" })), null);
  assert.equal(installNotice(install({ state: "done" }), install()), null);
});

test("each failure has a sentence of its own, in every language", () => {
  const codes = ["network", "not_found", "checksum", "unsupported_model", "busy_elsewhere", "disk", "unsupported_platform", "load_failed"];
  const keys = [...codes.map(installFailure), installFailure("something_new"), installFailure(null),
    ...["microphone", "load_failed", "start", "engine_failed", "timeout", null].map(dictationFailure)];
  assert.equal(new Set(codes.map(installFailure)).size, codes.length);
  assert.equal(installFailure("something_new"), "The speech model could not be installed.");
  assert.equal(dictationFailure("microphone"), "The microphone is not available. Check the privacy settings of the system.");
  assert.equal(dictationFailure("timeout"), dictationFailure("engine_failed"));
  for (const key of keys) {
    assert.ok(key in messages, key);
    for (const locale of locales) if (locale !== "en") assert.notEqual(translate(locale, key), key, `${locale}: ${key}`);
  }
});

test("the chosen model is the one the settings name", () => {
  assert.equal(chosenModel(listing())?.name, "Parakeet TDT 0.6b v3");
  assert.equal(chosenModel(listing({ modelId: "whisper-small" }))?.name, "Whisper small");
  assert.equal(chosenModel(listing({ modelId: "gone" })), null);
  assert.equal(chosenModel(null), null);
});

test("the store tells its listeners of a change, and of nothing else", () => {
  const store = createSpeechStore();
  let told = 0;
  const stop = store.subscribe(() => told++);
  store.update(value => value);
  assert.equal(told, 0);
  store.update(value => withDictation(value, { status: "starting", owner: "a" }));
  assert.equal(told, 1);
  // The same facts are the same state: a selector that reads them renders nothing.
  const before = store.getState();
  store.update(value => withDictation(value, { status: "starting", owner: "a", partial: "" }));
  assert.equal(store.getState(), before);
  assert.equal(told, 1);
  store.update(value => withInstall(value, install({ state: "downloading", received: 5, total: 10 })));
  store.update(value => withInstall(value, install({ state: "downloading", received: 5, total: 10 })));
  assert.equal(told, 2);
  store.update(value => withInstall(value, install({ state: "downloading", received: 6, total: 10 })));
  assert.equal(told, 3);
  stop();
  store.update(value => withDictation(value, { partial: "hello" }));
  assert.equal(told, 3);
  assert.equal(store.getState().dictation.partial, "hello");
});
