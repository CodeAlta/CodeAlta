import assert from "node:assert/strict";
import test from "node:test";
import { defaultMicrophone, deviceMissing, dictationSilenceDelay, listMicrophones, microphoneChoice, microphoneName, noSound, readMicrophone, resolveMicrophone,
  saveMicrophone, shortMicrophoneName, silenceDelay, silencePeak, type InputDevice, type MicrophonePreference } from "./microphones";
import { fixtureDevices } from "./speechFixture";

const yeti = { id: "id-yeti", label: "Microphone (Yeti Stereo Microphone) (b58e:9e84)" };
const broadcast = { id: "id-broadcast", label: "Microphone (NVIDIA Broadcast)" };

test("the choice of a microphone is kept with its id and its label, and read back", () => {
  let stored: string | null = null;
  const write = (value: string) => { stored = value; };
  for (const preference of [{ device: yeti, confirmed: true }, { device: null, confirmed: true }, { device: yeti, confirmed: false }, defaultMicrophone] satisfies MicrophonePreference[]) {
    assert.equal(saveMicrophone(write, preference), true);
    assert.deepEqual(readMicrophone(() => stored), preference);
  }
  // Nothing kept yet: the microphone of the system, and the first press asks.
  assert.deepEqual(readMicrophone(() => null), { device: null, confirmed: false });
  // A page that cannot write keeps the choice for its own time only.
  assert.equal(saveMicrophone(() => { throw new Error("quota"); }, { device: yeti, confirmed: true }), false);
});

test("what is not a preference reads as nothing chosen, and never as a device that does not exist", () => {
  const read = (value: unknown) => readMicrophone(() => typeof value === "string" ? value : JSON.stringify(value));
  for (const bad of ["", "not json", "[]", "7", "null", '"default"', { device: "id-yeti" }, { device: { id: 7, label: "x" }, confirmed: "yes" },
    { device: { id: "", label: "x" } }, { device: { id: "x".repeat(600), label: "x" } }, { device: { id: "x", label: 3 } }, { device: [] }])
    assert.deepEqual(read(bad), defaultMicrophone, JSON.stringify(bad));
  // The entries Chromium makes up are no device of their own.
  assert.deepEqual(read({ device: { id: "default", label: "Default - Microphone" }, confirmed: true }), { device: null, confirmed: true });
  assert.deepEqual(read({ device: { id: "communications", label: "x" }, confirmed: true }), { device: null, confirmed: true });
  // Only `true` confirms; a device without a label is still the device.
  assert.deepEqual(read({ device: { id: "a", label: "" }, confirmed: 1 }), { device: { id: "a", label: "" }, confirmed: false });
  assert.deepEqual(readMicrophone(() => { throw new Error("denied"); }), defaultMicrophone);
});

test("a device is named without the prefix of a pseudo-entry and without its USB id", () => {
  assert.equal(microphoneName("Default - Microphone (NVIDIA Broadcast)"), "Microphone (NVIDIA Broadcast)");
  assert.equal(microphoneName("Communications - Microphone (Yeti Stereo Microphone) (b58e:9e84)"), "Microphone (Yeti Stereo Microphone)");
  assert.equal(microphoneName("Microphone (Yeti Stereo Microphone) (B58E:9E84)"), "Microphone (Yeti Stereo Microphone)");
  assert.equal(microphoneName("MacBook Pro Microphone (Built-in)"), "MacBook Pro Microphone (Built-in)");
  assert.equal(microphoneName(""), "");
  // In a tooltip, the device and not its kind.
  assert.equal(shortMicrophoneName("Microphone (Yeti Stereo Microphone) (b58e:9e84)"), "Yeti Stereo Microphone");
  assert.equal(shortMicrophoneName("Default - Microphone (NVIDIA Broadcast)"), "NVIDIA Broadcast");
  assert.equal(shortMicrophoneName("Mikrofon (Realtek(R) Audio)"), "Realtek(R) Audio");
  assert.equal(shortMicrophoneName("Microphone Array (Realtek(R) Audio)"), "Microphone Array (Realtek(R) Audio)");
  assert.equal(shortMicrophoneName("Built-in Microphone"), "Built-in Microphone");
});

test("the list has the microphone of the system, named, then each real device once", () => {
  const list = listMicrophones(fixtureDevices, null);
  assert.equal(list.systemName, "Microphone (NVIDIA Broadcast)");
  assert.deepEqual(list.entries, [{ id: "id-broadcast", name: "Microphone (NVIDIA Broadcast)", connected: true }, { id: "id-yeti", name: "Microphone (Yeti Stereo Microphone)", connected: true }]);
  assert.equal(list.chosen, "", "nothing chosen is the microphone of the system");
  assert.equal(listMicrophones(fixtureDevices, yeti).chosen, "id-yeti");
  // A browser that names no device of the system (WebKit), and one that names none at all before it was given a microphone.
  assert.equal(listMicrophones([yeti, broadcast], null).systemName, null);
  assert.deepEqual(listMicrophones([{ id: "", label: "" }], null), { systemName: null, entries: [], chosen: "" });
  assert.deepEqual(listMicrophones([], null), { systemName: null, entries: [], chosen: "" });
  // Two devices of the same model are told apart; one with no name still has a line.
  const twins: InputDevice[] = [{ id: "a", label: "USB Microphone" }, { id: "b", label: "USB Microphone" }, { id: "c", label: "" }];
  assert.deepEqual(listMicrophones(twins, null).entries.map(entry => entry.name), ["USB Microphone #1", "USB Microphone #2", "#3"]);
});

test("the chosen device stays in the list when it is not connected, and is found again by its label", () => {
  const unplugged = fixtureDevices.filter(device => device.id !== "id-yeti");
  const list = listMicrophones(unplugged, yeti);
  assert.deepEqual(list.entries.at(-1), { id: "id-yeti", name: "Microphone (Yeti Stereo Microphone)", connected: false });
  assert.equal(list.chosen, "id-yeti");
  assert.equal(resolveMicrophone(unplugged, yeti), null);
  // The id of a device changes between two runs of the WebView: the label finds it.
  const renumbered = fixtureDevices.map(device => device.id === "id-yeti" ? { ...device, id: "id-yeti-2" } : device);
  assert.deepEqual(resolveMicrophone(renumbered, yeti), { id: "id-yeti-2", label: yeti.label });
  const again = listMicrophones(renumbered, yeti);
  assert.equal(again.chosen, "id-yeti-2");
  assert.equal(again.entries.every(entry => entry.connected), true);
  // The id wins over the label, and a pseudo-entry is never what a choice opens.
  assert.equal(resolveMicrophone([...fixtureDevices, { id: "other", label: yeti.label }], yeti)?.id, "id-yeti");
  assert.equal(resolveMicrophone(fixtureDevices, { id: "gone", label: "Communications - Microphone (Yeti Stereo Microphone) (b58e:9e84)" }), null);
  assert.equal(resolveMicrophone(fixtureDevices, null), null);
  // A choice without a label is found by its id only.
  assert.equal(resolveMicrophone([{ id: "a", label: "" }], { id: "gone", label: "" }), null);
});

test("an entry of the list is the choice of its device, as the browser lists it now", () => {
  assert.equal(microphoneChoice(fixtureDevices, "", yeti), null);
  assert.deepEqual(microphoneChoice(fixtureDevices, "id-broadcast", yeti), broadcast);
  // The entry of a device that is not connected is the choice that is kept.
  const unplugged = fixtureDevices.filter(device => device.id !== "id-yeti");
  assert.equal(microphoneChoice(unplugged, "id-yeti", yeti), yeti);
  assert.equal(microphoneChoice(unplugged, "id-nowhere", yeti), null);
  assert.equal(microphoneChoice(fixtureDevices, "default", null), null);
});

test("a capture that asked for a device that is not there opens the microphone of the system; any other refusal is the microphone that is not available", () => {
  assert.equal(deviceMissing("OverconstrainedError", true), true);
  assert.equal(deviceMissing("NotFoundError", true), true);
  // Asked for no device in particular: there is no microphone at all.
  assert.equal(deviceMissing("NotFoundError", false), false);
  assert.equal(deviceMissing("OverconstrainedError", false), false);
  for (const refusal of ["NotAllowedError", "NotReadableError", "SecurityError", "AbortError", ""]) assert.equal(deviceMissing(refusal, true), false, refusal);
});

test("a microphone gives no sound when the wait is over and nothing above digital silence was heard", () => {
  assert.equal(silenceDelay, 3_000);
  assert.equal(dictationSilenceDelay, 8_000);
  const zeros = [0, 0, 0, 0];
  // Not before the wait is over, whatever was heard.
  assert.equal(noSound(zeros, 2_999), false);
  assert.equal(noSound(zeros, 3_000), true);
  assert.equal(noSound(zeros, 7_999, dictationSilenceDelay), false);
  assert.equal(noSound(zeros, 8_000, dictationSilenceDelay), true);
  // A capture that gave no chunk at all gives no sound either.
  assert.equal(noSound([], 3_000), true);
  // Any sound, however late and however small above digital silence, is a microphone that works: the quiet of a room is one.
  assert.equal(noSound([0, 0, .1], 60_000), false);
  assert.equal(noSound([0, 3.05e-5, 0], 3_000), false, "one step of a 16-bit sample");
  assert.equal(noSound([silencePeak], 3_000), true);
  assert.equal(noSound([silencePeak * 1.01], 3_000), false);
  assert.equal(noSound([Number.NaN], 3_000), true);
});
