import assert from "node:assert/strict";
import test from "node:test";
import type { ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { ToastProps } from "@blueprintjs/core";
import { installText } from "./InstallNotice";
import { createSpeech } from "./speech";
import { createFixtureHost, createFixtureMicrophone } from "./speechFixture";
import { followInstall } from "./useSpeechInstall";

const settle = async () => { for (let turn = 0; turn < 6; turn++) await new Promise<void>(resolve => setTimeout(resolve, 0)); };
const wait = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));
// What a toast says, whether its message is a text or the notice with its bar.
const said = (toast: ToastProps) => typeof toast.message === "string" ? toast.message : renderToStaticMarkup(toast.message as ReactElement).replace(/<[^>]+>/g, "");

function follow(options: Parameters<typeof createFixtureHost>[0] = {}) {
  const host = createFixtureHost(options), microphone = createFixtureMicrophone();
  const speech = createSpeech({ api: host.api, capture: microphone.start, notify: () => { } });
  const shown: { toast: ToastProps; key?: string }[] = [], dismissed: string[] = [];
  let locale: "en" | "fr" = "en";
  const stop = followInstall(speech, () => locale, (toast, key) => { shown.push({ toast, key }); }, key => { dismissed.push(key); });
  return { host, speech, shown, dismissed, stop, language: (value: "en" | "fr") => { locale = value; }, last: () => shown.at(-1)!.toast };
}

test("the notice says what the download does, with its sizes once they are known", () => {
  const progress = { kind: "progress", phase: "downloading", model: "Parakeet TDT 0.6b v3", received: 222298112, total: 713031680 } as const;
  assert.equal(installText(progress, "en"), "Downloading Parakeet TDT 0.6b v3 — 212 MiB of 680 MiB");
  assert.equal(installText({ ...progress, total: 0 }, "en"), "Downloading Parakeet TDT 0.6b v3…");
  assert.equal(installText({ ...progress, phase: "installing" }, "en"), "Installing Parakeet TDT 0.6b v3…");
  // More received than expected is never shown as more than the whole.
  assert.equal(installText({ ...progress, received: 900000000 }, "en"), "Downloading Parakeet TDT 0.6b v3 — 680 MiB of 680 MiB");
  assert.equal(installText(progress, "fr"), "Téléchargement de Parakeet TDT 0.6b v3 — 212 MiB sur 680 MiB");
});

test("one notice follows a download from its start to its end, updated in place", async () => {
  const { host, speech, shown, dismissed, last } = follow();
  speech.connect(host.epoch);
  await settle();
  assert.equal(shown.length, 0, "nothing runs: nothing is shown");

  assert.equal(await speech.install(null), "ok");
  await settle();
  assert.equal(shown.length, 1);
  // Kept until the download ends, under one key, with the way to stop it.
  assert.deepEqual([shown[0].key, last().timeout, last().action?.text, said(last())], ["speech-install", 0, "Cancel", "Downloading Parakeet TDT 0.6b v3…"]);
  assert.match(renderToStaticMarkup(last().message as ReactElement), /bp6-progress-bar/);

  // Progress comes many times a second: the notice is drawn a few times, and ends on the latest.
  for (let received = 1; received <= 20; received++) host.tell({ receivedBytes: String(received * 1048576), totalBytes: "104857600" });
  await settle();
  await wait(320);
  assert.ok(shown.length <= 3, `${shown.length} notices for twenty steps`);
  assert.equal(said(last()), "Downloading Parakeet TDT 0.6b v3 — 20 MiB of 100 MiB");
  assert.ok(shown.every(entry => entry.key === "speech-install"));
  assert.match(renderToStaticMarkup(last().message as ReactElement), /width:\s*20%/);

  // Another phase is shown at once.
  const before = shown.length;
  host.tell({ state: "installing" });
  await settle();
  assert.equal(shown.length, before + 1);
  assert.equal(said(last()), "Installing Parakeet TDT 0.6b v3…");

  host.installed("parakeet-tdt-0.6b-v3");
  await settle();
  assert.deepEqual([said(last()), last().intent, last().timeout, last().action], ["Speech to prompt is ready", "success", 5000, undefined]);
  assert.deepEqual(dismissed, []);
  // The end is told once.
  const told = shown.length;
  host.tell({ state: "done" });
  await settle();
  assert.equal(shown.length, told);
});

test("Cancel stops the download, and its notice goes away", async () => {
  const { host, speech, shown, dismissed, last } = follow();
  speech.connect(host.epoch);
  await speech.install("whisper-small");
  await settle();
  assert.equal(said(last()), "Downloading Whisper small…");
  (last().action!.onClick as () => void)();
  await settle();
  assert.equal(host.state.cancels, 1);
  assert.deepEqual(dismissed, ["speech-install"]);
  assert.equal(shown.length, 1, "a canceled download says nothing more");
});

test("a failure is said in a sentence of its own, in the language of the window", async () => {
  const { host, speech, last, language } = follow();
  speech.connect(host.epoch);
  await speech.install(null);
  await settle();
  host.tell({ state: "failed", error: "busy_elsewhere" });
  await settle();
  assert.deepEqual([said(last()), last().intent], ["Another CodeAlta is downloading this speech model.", "danger"]);
  language("fr");
  await speech.install(null);
  await settle();
  assert.equal(said(last()), "Téléchargement de Parakeet TDT 0.6b v3…");
  host.tell({ state: "failed", error: "network" });
  await settle();
  assert.equal(said(last()), "Le modèle vocal n’a pas pu être téléchargé : vérifiez la connexion réseau.");
});

test("a notice the user closed stays closed, until they ask for it or the download ends", async () => {
  const { host, speech, shown, last } = follow();
  speech.connect(host.epoch);
  await speech.install(null);
  await settle();
  // The close button of the toast.
  last().onDismiss!(false);
  host.tell({ receivedBytes: "1048576", totalBytes: "104857600" });
  await settle();
  await wait(320);
  host.tell({ state: "installing" });
  await settle();
  assert.equal(shown.length, 1, "closed: the progress is not put back in front of the user");
  // Download asked again while it runs: the host says busy, and the notice comes back.
  assert.equal(await speech.install(null), "busy");
  await settle();
  assert.equal(shown.length, 2);
  assert.equal(said(last()), "Installing Parakeet TDT 0.6b v3…");
  // Closed again, its end is still told.
  last().onDismiss!(false);
  host.installed("parakeet-tdt-0.6b-v3");
  await settle();
  assert.equal(said(last()), "Speech to prompt is ready");
  // The next download is shown from its start.
  await speech.install("whisper-small");
  await settle();
  assert.equal(said(last()), "Downloading Whisper small…");
});

test("the end of a download from before the window was there is not announced, and a running one is shown", async () => {
  const ended = follow();
  ended.host.state.install = { state: "done", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "40", totalBytes: "40", error: null };
  ended.speech.connect(ended.host.epoch);
  await settle();
  assert.equal(ended.shown.length, 0);
  const failed = follow();
  failed.host.state.install = { state: "failed", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "0", totalBytes: "0", error: "network" };
  failed.speech.connect(failed.host.epoch);
  await settle();
  assert.equal(failed.shown.length, 0);

  const running = follow();
  running.host.state.install = { state: "downloading", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "104857600", totalBytes: "209715200", error: null };
  const disconnect = running.speech.connect(running.host.epoch);
  await settle();
  assert.equal(said(running.last()), "Downloading Whisper small — 100 MiB of 200 MiB");
  // The window stops following the host: the notice goes, the download does not.
  disconnect();
  await settle();
  assert.deepEqual([running.dismissed, running.host.state.cancels], [["speech-install"], 0]);
  // Once it no longer follows, nothing is shown.
  running.stop();
  running.speech.connect(running.host.epoch);
  await settle();
  assert.equal(running.shown.length, 1);
});
