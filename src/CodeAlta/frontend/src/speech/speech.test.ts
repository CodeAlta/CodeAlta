import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createSpeech, type DictationSink } from "./speech";
import { createFixtureHost, createFixtureMicrophone, createFixturePreferences, fixtureDevices } from "./speechFixture";
import { setupDownloads, type SpeechNotice } from "./speechStore";
import { spokenInsertion } from "./spokenText";

// Lets what waits on promises and streams run.
const settle = async () => { for (let turn = 0; turn < 6; turn++) await new Promise<void>(resolve => setTimeout(resolve, 0)); };

// A prompt as the editor keeps it: pieces go in at its end, the greyed text is beside it.
function prompt() {
  const state = { text: "", interim: "", focused: 0, writes: [] as string[] };
  const sink: DictationSink = {
    write(piece) { state.writes.push(piece); state.text += spokenInsertion(state.text, piece); },
    interim(text) { state.interim = text; },
    focus() { state.focused++; },
  };
  return { state, sink };
}

// Every window of a test is left at the end of the file: a dictation that still records holds nothing back.
const windows: (() => void)[] = [];
after(() => { for (const leave of windows) leave(); });

// A window whose user already said which microphone to use, unless `preference` says otherwise.
async function window(options: Parameters<typeof createFixtureHost>[0] = { installed: ["parakeet-tdt-0.6b-v3"] },
  more: { preference?: string | null; silenceAfter?: number; warnAfter?: number } = {}) {
  const host = createFixtureHost(options), microphone = createFixtureMicrophone();
  const preferences = "preference" in more ? createFixturePreferences(more.preference) : createFixturePreferences();
  const notices: string[] = [], noticed: SpeechNotice[] = [];
  const speech = createSpeech({ api: host.api, microphones: microphone.microphones, preferences, silenceAfter: more.silenceAfter, warnAfter: more.warnAfter,
    notify: notice => { notices.push(notice.message); noticed.push(notice); } });
  const disconnect = speech.connect(host.epoch);
  windows.push(disconnect);
  await settle();
  return { host, microphone, speech, notices, noticed, preferences, disconnect, status: () => speech.getState().dictation.status };
}

test("a dictation: the words are written as they are finished, the others greyed, and nothing twice", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { state, sink } = prompt();
  const levels: [number, string | null][] = [];
  speech.subscribeLevel((level, owner) => levels.push([level, owner]));
  speech.toggle("a", sink);
  assert.deepEqual(speech.getState().dictation, { status: "starting", owner: "a", partial: "", microphone: null });
  await settle();
  assert.equal(status(), "recording");
  assert.equal(microphone.open(), true);
  assert.equal(state.focused, 1, "the caret shows where the words go");

  // A tenth of a second at a time, numbered from zero, without waiting for the host between them.
  microphone.speak({ base64: "AAAA", level: .4 }); microphone.speak({ base64: "BBBB", level: .8 }); microphone.speak({ base64: "CCCC" });
  assert.deepEqual(host.state.appends.map(request => [request.id, request.sequence, request.base64, request.expectedEpoch]),
    [["dictation-1", 0, "AAAA", "fixture"], ["dictation-1", 1, "BBBB", "fixture"], ["dictation-1", 2, "CCCC", "fixture"]]);
  assert.deepEqual(levels.slice(0, 2), [[.4, "a"], [.8, "a"]]);

  // The model loads: what is said meanwhile is kept by the host, and nothing is written.
  host.hear("", "", { state: "loading" });
  await settle();
  assert.deepEqual([state.text, state.interim, status()], ["", "", "recording"]);
  host.hear("", "hello wor");
  await settle();
  assert.deepEqual([state.text, state.interim, speech.getState().dictation.partial], ["", "hello wor", "hello wor"]);
  host.hear("Hello world.", "");
  await settle();
  assert.deepEqual([state.text, state.interim], ["Hello world.", ""]);
  // The same text told again writes nothing.
  host.hear("Hello world.", "how are");
  await settle();
  assert.deepEqual([state.text, state.interim], ["Hello world.", "how are"]);
  host.hear("Hello world. How are you?", "");
  await settle();
  assert.equal(state.text, "Hello world. How are you?");

  // Stopped: the host gives the whole text once more, and the stream ends with it too.
  host.hear("Hello world. How are you?", "fine than");
  await settle();
  host.state.dictations[0].finalText = "Hello world. How are you? Fine, thanks.";
  speech.toggle("a", sink);
  assert.equal(status(), "finishing");
  assert.equal(microphone.open(), false, "the microphone closes at once");
  microphone.speak();
  assert.equal(host.state.appends.length, 3, "no audio after the stop");
  await settle();
  assert.deepEqual(host.state.stops.map(request => [request.id, request.cancel]), [["dictation-1", false]]);
  assert.equal(state.text, "Hello world. How are you? Fine, thanks.");
  assert.deepEqual(state.writes, ["Hello world.", " How are you?", " Fine, thanks."]);
  assert.deepEqual([status(), speech.getState().dictation.owner, state.interim], ["idle", null, ""]);
  assert.equal(state.focused, 2, "the keyboard is back in the prompt");
  assert.deepEqual(levels.at(-1), [0, "a"]);
  assert.deepEqual(notices, []);
});

test("the first use asks before anything is downloaded, and a running download is only shown again", async () => {
  const { host, microphone, speech, notices, status } = await window({});
  const { state, sink } = prompt();
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), speech.getState().asking, microphone.state.captures.length, host.state.installs.length], ["idle", "a", 0, 0]);
  assert.equal(state.focused, 0, "the question keeps the focus");
  // Cancel closes the question and leaves everything as it is.
  speech.dismissAsk("b");
  assert.equal(speech.getState().asking, "a");
  speech.dismissAsk("a");
  assert.equal(speech.getState().asking, null);
  assert.equal(host.state.installs.length, 0);

  // Download: the host owns it, and the page follows it.
  assert.equal(await speech.install(null), "ok");
  await settle();
  assert.deepEqual(host.state.installs, [null]);
  assert.equal(speech.getState().install?.state, "downloading");
  host.state.startStatus = "installing";
  const reveals = speech.getState().reveals;
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), speech.getState().reveals, speech.getState().asking, microphone.state.captures.length], ["idle", reveals + 1, null, 0]);

  // Once it is there, the same click dictates.
  host.state.startStatus = null;
  const reads = host.state.reads;
  host.installed("parakeet-tdt-0.6b-v3");
  await settle();
  assert.ok(host.state.reads > reads, "the list of models is read again");
  assert.equal(speech.getState().listing?.models[0].installed, true);
  speech.toggle("a", sink);
  await settle();
  assert.equal(status(), "recording");
  assert.deepEqual(notices, []);
});

test("a dictation that cannot start says why once, and leaves no microphone open", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { sink } = prompt();
  // The host could not read its settings.
  host.state.startStatus = "read_failed";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), notices.splice(0), microphone.state.captures.length], ["idle", ["The dictation could not start."], 0]);
  // Turned off elsewhere meanwhile: the buttons follow what the host says now, without a message.
  host.state.startStatus = "disabled"; host.state.enabled = false;
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), notices.length, speech.getState().listing?.enabled], ["idle", 0, false]);
  host.state.startStatus = null; host.state.enabled = true;
  await speech.refresh();

  // The system refuses the microphone: the dictation the host started is given back.
  microphone.state.opening = "microphone";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual(notices.splice(0), ["The microphone is not available. Check the privacy settings of the system."]);
  assert.deepEqual([status(), microphone.open()], ["idle", false]);
  assert.deepEqual(host.state.stops.map(request => [request.id, request.cancel]), [["dictation-1", true]]);
  // The audio of the page fails.
  microphone.state.opening = "failed";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual(notices.splice(0), ["The dictation could not start."]);
  assert.deepEqual([status(), microphone.open(), host.state.stops.length], ["idle", false, 2]);
  assert.equal(host.state.appends.length, 0);
});

test("stopped before the host or the microphone answered, a dictation holds nothing", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { state, sink } = prompt();
  // The second click comes before the answer of the host.
  host.state.hold = true;
  speech.toggle("a", sink);
  speech.toggle("a", sink);
  assert.equal(status(), "finishing");
  host.release(); await settle();
  host.release(); await settle();
  assert.deepEqual(host.state.stops.map(request => [request.id, request.cancel]), [["dictation-1", true]], "the dictation the host started is given back");
  assert.deepEqual([status(), microphone.state.captures.length, state.focused], ["idle", 0, 0]);
  host.state.hold = false;

  // The second click comes while the system still asks for the microphone.
  microphone.state.opening = "wait";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), microphone.open()], ["starting", true]);
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), microphone.open()], ["idle", false]);
  microphone.ready(); microphone.speak();
  assert.deepEqual([status(), host.state.appends.length], ["idle", 0]);
  assert.deepEqual(notices, []);
});

test("one dictation for the window: another composer starts, and the first keeps what it heard", async () => {
  const { host, microphone, speech, status } = await window();
  const first = prompt(), second = prompt();
  speech.toggle("a", first.sink);
  await settle();
  host.hear("One.", "two");
  await settle();
  host.state.dictations[0].finalText = "One. Two.";
  speech.toggle("b", second.sink);
  assert.deepEqual(speech.getState().dictation, { status: "starting", owner: "b", partial: "", microphone: null });
  await settle();
  assert.deepEqual(host.state.stops.map(request => [request.id, request.cancel]), [["dictation-1", false]]);
  assert.deepEqual([first.state.text, first.state.interim], ["One. Two.", ""]);
  assert.equal(first.state.focused, 1, "the keyboard is not taken back from the second prompt");
  assert.deepEqual([status(), speech.getState().dictation.owner, microphone.state.captures.filter(capture => !capture.stopped).length], ["recording", "b", 1]);
  host.hear("Three.", "");
  microphone.speak();
  await settle();
  assert.deepEqual([second.state.text, first.state.text], ["Three.", "One. Two."]);
  assert.deepEqual(host.state.appends.map(request => [request.id, request.sequence]), [["dictation-2", 0]]);
  // Stopping the composer that does not dictate changes nothing.
  speech.stop("a");
  assert.equal(status(), "recording");
  speech.stop("b");
  await settle();
  assert.equal(status(), "idle");
});

test("a composer that can no longer take text ends its dictation, and nothing more is written", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { state, sink } = prompt();
  speech.toggle("a", sink);
  await settle();
  host.hear("Kept.", "being said");
  await settle();
  host.state.dictations[0].finalText = "Kept. Being said when it was sent.";
  // Sent, hidden, read-only or gone: what was being spoken is dropped.
  speech.abandon("a");
  assert.deepEqual([microphone.open(), state.interim], [false, ""]);
  await settle();
  assert.deepEqual(host.state.stops.map(request => [request.id, request.cancel]), [["dictation-1", true]]);
  assert.deepEqual([state.text, status(), state.focused], ["Kept.", "idle", 1]);
  speech.abandon("a");
  assert.equal(host.state.stops.length, 1);

  // Abandoned while its stop was on the way: the last words are not written into a prompt that left.
  host.state.hold = true;
  speech.toggle("a", sink);
  host.release(); await settle();
  host.hear("Again.", "");
  await settle();
  host.state.dictations[1].finalText = "Again. Too late.";
  speech.stop("a");
  speech.abandon("a");
  host.release(); await settle();
  assert.deepEqual([state.text, status()], ["Kept. Again.", "idle"]);
  assert.deepEqual(notices, []);
});

test("a dictation the host ends or refuses stops sending, and says what failed", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { state, sink } = prompt();
  // The host no longer knows the dictation: the chunk it refuses is the last one sent.
  speech.toggle("a", sink);
  await settle();
  host.state.appendStatus = "unknown";
  microphone.speak();
  await settle();
  microphone.speak();
  assert.equal(host.state.appends.length, 1);
  assert.deepEqual([status(), microphone.open()], ["idle", false]);
  host.state.appendStatus = "ok";

  // The engine fails while it listens.
  speech.toggle("a", sink);
  await settle();
  host.hear("Before it failed.", "and", { state: "failed", error: "engine_failed" });
  await settle();
  assert.deepEqual(notices.splice(0), ["The dictation stopped: the speech engine failed."]);
  assert.deepEqual([state.text, state.interim, status(), microphone.open()], ["Before it failed.", "", "idle", false]);

  // The model does not load: the stop tells it, once.
  speech.toggle("a", sink);
  await settle();
  host.state.stopReply = { status: "failed", text: "", error: "load_failed" };
  speech.stop("a");
  await settle();
  assert.deepEqual(notices.splice(0), ["The speech model could not be loaded."]);
  assert.equal(status(), "idle");
  host.state.stopReply = null;

  // The stream of the host ends without a word (the host went away).
  speech.toggle("a", sink);
  await settle();
  host.state.dictations.at(-1)!.stream.close();
  await settle();
  assert.deepEqual([status(), microphone.open(), notices.length], ["idle", false, 0]);
});

test("the window follows one host: connecting twice leaves one stream, and leaving ends a dictation", async () => {
  const host = createFixtureHost({ installed: ["parakeet-tdt-0.6b-v3"] }), microphone = createFixtureMicrophone();
  const speech = createSpeech({ api: host.api, microphones: microphone.microphones, preferences: createFixturePreferences(), notify: () => { } });
  // An effect runs twice in development.
  speech.connect(host.epoch)();
  const disconnect = speech.connect(host.epoch);
  await settle();
  assert.equal(host.state.watches.filter(stream => !stream.closed).length, 1);
  assert.equal(speech.getState().listing?.status, "ok");
  assert.equal(speech.getState().install?.state, "idle");

  // The end of a download the page did not see is not a reason to read the models again.
  const reads = host.state.reads;
  host.tell({ state: "downloading", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "10", totalBytes: "40" });
  await settle();
  assert.deepEqual([speech.getState().install?.received, speech.getState().install?.total, host.state.reads], [10, 40, reads]);
  host.tell({ state: "failed", error: "network" });
  await settle();
  assert.equal(host.state.reads, reads + 1);

  const { state, sink } = prompt();
  speech.toggle("a", sink);
  await settle();
  host.hear("Said.", "and more");
  await settle();
  disconnect();
  assert.deepEqual([microphone.open(), speech.getState().dictation.status, speech.getState().listing], [false, "idle", null]);
  await settle();
  assert.deepEqual(host.state.stops.map(request => request.cancel), [true]);
  assert.deepEqual([state.text, state.interim], ["Said.", ""]);
  assert.equal(host.state.watches.filter(stream => !stream.closed).length, 0);
  // With no host there is nothing to start.
  speech.toggle("a", sink);
  await settle();
  assert.equal(speech.getState().dictation.status, "idle");
  assert.equal(await speech.install(null), "unavailable");
});

test("a window of another epoch is told so, and shows no button", async () => {
  const host = createFixtureHost({ epoch: "now" }), microphone = createFixtureMicrophone();
  const speech = createSpeech({ api: host.api, microphones: microphone.microphones, preferences: createFixturePreferences(), notify: () => { } });
  speech.connect("before");
  await settle();
  assert.equal(speech.getState().listing?.status, "stale_epoch");
  // A replayed end of a download from before the page was there.
  const replay = createFixtureHost();
  replay.state.install = { state: "done", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "40", totalBytes: "40", error: null };
  const other = createSpeech({ api: replay.api, microphones: microphone.microphones, preferences: createFixturePreferences(), notify: () => { } });
  other.connect(replay.epoch);
  await settle();
  assert.deepEqual([other.getState().install?.state, replay.state.reads], ["done", 1]);
});

const yeti = { id: "id-yeti", label: "Microphone (Yeti Stereo Microphone) (b58e:9e84)" };
const wait = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));

test("the first press proposes the setup: nothing is recorded or kept before its answer", async () => {
  const { host, microphone, speech, preferences, status } = await window(undefined, { preference: null });
  const { state, sink } = prompt();
  assert.deepEqual(speech.getState().microphone, { device: null, confirmed: false });
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().asking, status(), host.state.starts, microphone.state.captures.length], ["a", "idle", 0, 0]);
  // Cancel keeps nothing: the next press proposes again.
  speech.dismissAsk("a");
  assert.deepEqual([speech.getState().asking, preferences.state.writes], [null, 0]);
  speech.toggle("a", sink);
  assert.equal(speech.getState().asking, "a");

  // The answer keeps the microphone and starts the dictation on it, with the same press.
  speech.confirmAndStart("a", yeti, sink);
  await settle();
  assert.deepEqual([speech.getState().asking, status(), microphone.heard(), host.state.starts], [null, "recording", "id-yeti", 1]);
  assert.deepEqual(JSON.parse(preferences.state.value!), { device: yeti, confirmed: true });
  assert.equal(speech.getState().dictation.microphone, yeti.label, "the button can name the microphone it hears");
  speech.stop("a");
  await settle();
  // Later presses record directly, on the same device.
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().asking, status(), microphone.heard()], [null, "recording", "id-yeti"]);
  assert.equal(state.focused > 0, true);
});

test("the setup offers to download a model that is missing, and keeps the microphone with that answer too", async () => {
  const { host, microphone, speech, preferences } = await window({}, { preference: null });
  const { sink } = prompt();
  speech.toggle("a", sink);
  assert.deepEqual([speech.getState().asking, setupDownloads(speech.getState())], ["a", true]);
  speech.confirmAndDownload("a", yeti);
  await settle();
  assert.deepEqual([speech.getState().asking, host.state.installs, microphone.open()], [null, [null], false]);
  assert.deepEqual(JSON.parse(preferences.state.value!), { device: yeti, confirmed: true });
  // While the download runs there is nothing to download again; once it is there, the setup would start.
  assert.equal(setupDownloads(speech.getState()), false);
  host.installed("parakeet-tdt-0.6b-v3");
  await settle();
  assert.equal(setupDownloads(speech.getState()), false);
  // The microphone was confirmed: the next press dictates.
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().asking, speech.getState().dictation.status, microphone.heard()], [null, "recording", "id-yeti"]);
});

test("a model the host says is missing shows the same setup to a user who already chose a microphone", async () => {
  const { host, speech } = await window({ installed: ["parakeet-tdt-0.6b-v3"] });
  const { sink } = prompt();
  // The model is listed as installed, and its engine is not: the host knows.
  host.state.startStatus = "model_missing";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().asking, speech.getState().missing, setupDownloads(speech.getState())], ["a", true, true]);
  speech.confirmAndDownload("a", null);
  await settle();
  host.state.startStatus = null;
  host.installed("parakeet-tdt-0.6b-v3");
  await settle();
  assert.deepEqual([speech.getState().missing, setupDownloads(speech.getState())], [false, false]);
});

test("a microphone open for its level tells it, says when it gives nothing, and takes that back at the first sound", async () => {
  const { microphone, speech, notices } = await window(undefined, { preference: null, silenceAfter: 300 });
  const levels: [number, string | null][] = [];
  speech.subscribeLevel((level, owner) => levels.push([level, owner]));
  speech.startMonitor("a", yeti);
  assert.deepEqual(speech.getState().monitor, { key: "a", open: false, silent: false, label: null });
  await settle();
  assert.deepEqual([microphone.heard(), speech.getState().monitor], ["id-yeti", { key: "a", open: true, silent: false, label: yeti.label }]);
  microphone.silence(); microphone.silence();
  assert.deepEqual(levels, [[0, "a"], [0, "a"]]);
  await wait(400);
  assert.equal(speech.getState().monitor?.silent, true, "about three seconds flat");
  // A live hint, not a verdict.
  microphone.speak({ level: .7 });
  assert.deepEqual([speech.getState().monitor?.silent, levels.at(-1)], [false, [.7, "a"]]);
  microphone.silence();
  await wait(400);
  assert.equal(speech.getState().monitor?.silent, false);
  // Another device: the first is released, the level starts again.
  speech.startMonitor("a", null);
  await settle();
  assert.deepEqual([microphone.state.captures.filter(capture => !capture.stopped).length, microphone.heard()], [1, ""]);
  speech.stopMonitor("b");
  assert.equal(microphone.open(), true, "only who asked stops it");
  speech.stopMonitor("a");
  assert.deepEqual([microphone.open(), speech.getState().monitor, levels.at(-1)], [false, null, [0, "a"]]);
  // Nothing of a level is kept as a choice, and nothing is said.
  assert.deepEqual([speech.getState().microphone, notices], [{ device: null, confirmed: false }, []]);
  // A microphone that gave sound before the wait was over is never called silent.
  speech.startMonitor("a", null);
  await settle();
  microphone.speak();
  await wait(400);
  assert.equal(speech.getState().monitor?.silent, false);
  speech.dismissAsk("a");
  assert.equal(microphone.open(), false, "closing the setup releases the microphone");
});

test("a level and a dictation never hold the microphone together", async () => {
  const { host, microphone, speech, status } = await window();
  const { state, sink } = prompt();
  speech.startMonitor("settings", null);
  await settle();
  speech.toggle("a", sink);
  assert.equal(speech.getState().monitor, null, "the dictation takes the microphone from the test");
  await settle();
  assert.deepEqual([status(), microphone.state.captures.filter(capture => !capture.stopped).length], ["recording", 1]);
  host.hear("Said before the test.", "");
  await settle();
  // The test takes it back: the dictation ends and keeps what it heard.
  speech.startMonitor("settings", null);
  await settle();
  assert.deepEqual([status(), state.text, host.state.stops.map(request => request.cancel), speech.getState().monitor?.key], ["idle", "Said before the test.", [false], "settings"]);
  assert.equal(microphone.state.captures.filter(capture => !capture.stopped).length, 1);
  // A microphone that cannot be opened for its level is said, and nothing stays open.
  microphone.state.opening = "microphone";
  speech.startMonitor("settings", null);
  await settle();
  assert.deepEqual([speech.getState().monitor, microphone.open()], [null, false]);
});

test("a dictation that hears nothing at all says so once, after a longer wait, and goes on", async () => {
  const { microphone, speech, noticed, status } = await window(undefined, { warnAfter: 300 });
  const { sink } = prompt();
  speech.attach("a");
  speech.toggle("a", sink);
  await settle();
  microphone.silence(); microphone.silence();
  await wait(400);
  assert.deepEqual(noticed, [{ message: "No sound comes from the microphone ({name}). Choose another one in the Speech settings.",
    parameters: { name: "NVIDIA Broadcast" }, intent: "warning", setup: "a" }]);
  assert.equal(status(), "recording", "the dictation is not stopped");
  microphone.silence();
  await wait(400);
  assert.equal(noticed.length, 1, "once for a dictation");
  // Its action shows the setup of that composer, which takes the microphone: the dictation ends.
  assert.equal(speech.proposeSetup("a"), true);
  await settle();
  assert.deepEqual([speech.getState().asking, status()], ["a", "idle"]);
  // A composer that is no longer there has no setup to show: the settings are opened instead.
  assert.equal(speech.proposeSetup("gone"), false);
  assert.equal(speech.proposeSetup(undefined), false);
  speech.dismissAsk("a");

  // Any sound before the wait is over, and nothing is said: a microphone with noise removal is silent until somebody speaks.
  speech.toggle("a", sink);
  await settle();
  microphone.silence(); microphone.speak(); microphone.silence();
  await wait(400);
  assert.equal(noticed.length, 1);
  speech.stop("a");
  await settle();
  // A dictation that ends before the wait says nothing either.
  speech.toggle("a", sink);
  await settle();
  speech.stop("a");
  await wait(400);
  assert.equal(noticed.length, 1);
});

test("a chosen microphone that is not connected gives way to the one of the system, said once", async () => {
  const { microphone, speech, notices, preferences } = await window(undefined, { preference: JSON.stringify({ device: yeti, confirmed: true }) });
  const { sink } = prompt();
  microphone.state.devices = fixtureDevices.filter(device => device.id !== "id-yeti");
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().dictation.status, speech.getState().dictation.microphone], ["recording", "Default - Microphone (NVIDIA Broadcast)"]);
  assert.deepEqual(notices.splice(0), ["The chosen microphone is not connected. The microphone of the system is used."]);
  speech.stop("a");
  await settle();
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual(notices, [], "said once for that device");
  speech.stop("a");
  await settle();
  // The choice is kept: the device may come back.
  assert.deepEqual(JSON.parse(preferences.state.value!).device, yeti);
  assert.deepEqual(speech.getState().devices?.map(device => device.id), ["default", "communications", "id-broadcast"]);

  // It comes back under another id (a new run of the WebView): found by its label, and kept under the new id.
  microphone.plug(fixtureDevices.map(device => device.id === "id-yeti" ? { ...device, id: "id-yeti-2" } : device));
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([speech.getState().dictation.microphone, notices], [yeti.label, []]);
  assert.deepEqual(speech.getState().microphone.device, { id: "id-yeti-2", label: yeti.label });
  speech.stop("a");
  await settle();
  speech.toggle("a", sink);
  await settle();
  assert.equal(microphone.heard(), "id-yeti-2", "opened under its new id at once");
});

test("the devices are read once for two askers, again when one comes or goes, and a choice in the settings needs no other question", async () => {
  const { microphone, speech, preferences } = await window(undefined, { preference: null });
  microphone.state.named = false;
  // A browser names its devices only to a page that was given a microphone: without a probe nothing is listed.
  await speech.listDevices(false);
  assert.equal(speech.getState().devices, null);
  await Promise.all([speech.listDevices(true), speech.listDevices(true)]);
  assert.deepEqual(microphone.state.lists, [false, true], "an effect that runs twice reads once");
  assert.equal(speech.getState().devices?.length, 4);
  // Watched while a page shows them; two pages are one watch.
  const first = speech.watchDevices(), second = speech.watchDevices();
  assert.equal(microphone.watching(), 1);
  microphone.plug(fixtureDevices.slice(0, 3));
  await settle();
  assert.equal(speech.getState().devices?.length, 3);
  first(); first();
  assert.equal(microphone.watching(), 1);
  second();
  assert.equal(microphone.watching(), 0);
  // The settings: choosing there is saying which microphone to use.
  speech.setMicrophone(yeti);
  assert.deepEqual(speech.getState().microphone, { device: yeti, confirmed: true });
  assert.deepEqual(JSON.parse(preferences.state.value!), { device: yeti, confirmed: true });
  const writes = preferences.state.writes;
  speech.setMicrophone({ ...yeti });
  assert.equal(preferences.state.writes, writes, "the same choice is not written again");
  speech.setMicrophone(null);
  assert.deepEqual(speech.getState().microphone, { device: null, confirmed: true });
});

test("the choice of a microphone belongs to the page: it stays when the window follows another host", async () => {
  const { host, speech, disconnect } = await window(undefined, { preference: JSON.stringify({ device: yeti, confirmed: true }) });
  await speech.listDevices(true);
  disconnect();
  assert.deepEqual([speech.getState().listing, speech.getState().microphone.device, speech.getState().devices?.length], [null, yeti, 4]);
  speech.connect(host.epoch)();
  assert.deepEqual(speech.getState().microphone, { device: yeti, confirmed: true });
});

// A prompt whose composer sends: what it held each time it was asked to.
function sendingPrompt() {
  const { state, sink } = prompt();
  const sent: string[] = [];
  return { state, sent, sink: { ...sink, send: () => { sent.push(state.text); } } satisfies DictationSink };
}

test("in the mode that sends, the button stops, writes the last words and sends once; Escape stops without sending", async () => {
  const { host, microphone, speech, status } = await window({ installed: ["parakeet-tdt-0.6b-v3"], send: "on_stop" });
  const { state, sent, sink } = sendingPrompt();
  speech.toggle("a", sink);
  await settle();
  microphone.speak();
  host.hear("Fix the parser.", "and run");
  await settle();
  assert.deepEqual(sent, [], "nothing is sent while it records");
  // The last words come with the stop: they are in the prompt before it is sent.
  host.state.dictations[0].finalText = "Fix the parser. And run the tests.";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([status(), state.text, sent], ["idle", "Fix the parser. And run the tests.", ["Fix the parser. And run the tests."]]);

  // Escape: the words are written, and the prompt is kept.
  speech.toggle("a", sink);
  await settle();
  host.state.dictations[1].finalText = "Something I want to read again first.";
  speech.stop("a");
  await settle();
  assert.deepEqual([status(), state.text.endsWith("read again first."), sent.length], ["idle", true, 1]);

  // Two words are a slip of the button, or a word to correct: they stay, unsent.
  speech.toggle("a", sink);
  await settle();
  host.state.dictations[2].finalText = "Not yet";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([state.text.endsWith("Not yet"), sent.length], [true, 1]);
});

test("a dictation the user did not stop never sends, whatever the mode", async () => {
  const { host, microphone, speech, status } = await window({ installed: ["parakeet-tdt-0.6b-v3"], send: "on_stop" });
  const first = sendingPrompt(), second = sendingPrompt();
  const words = "Enough words to be sent by themselves.";
  const start = async (target = first) => { speech.toggle(target === first ? "a" : "b", target.sink); await settle(); host.state.dictations.at(-1)!.finalText = words; host.hear(words, ""); await settle(); };
  // Another composer takes the dictation.
  await start();
  speech.toggle("b", second.sink);
  await settle();
  assert.deepEqual([first.state.text, first.sent, status()], [words, [], "recording"]);
  // Its composer can no longer take text: hidden, closed, read-only, Settings over it.
  speech.abandon("b");
  await settle();
  // A test of the microphone takes it.
  await start();
  speech.startMonitor("settings", null);
  await settle();
  speech.stopMonitor("settings");
  // The host ends it, and the microphone fails.
  await start();
  host.state.dictations.at(-1)!.stream.close();
  await settle();
  await start();
  microphone.fail("microphone");
  await settle();
  // The stop itself fails.
  await start();
  host.state.stopReply = { status: "failed", text: words, error: "engine_failed" };
  speech.toggle("a", first.sink);
  await settle();
  host.state.stopReply = null;
  // The window stops following the host.
  await start();
  speech.connect(null);
  await settle();
  assert.deepEqual([first.sent, second.sent, status()], [[], [], "idle"]);
});

test("in the mode that keeps the prompt nothing is sent, and a composer that does not send is never asked to", async () => {
  const { host, speech } = await window();
  const { state, sent, sink } = sendingPrompt();
  speech.toggle("a", sink);
  await settle();
  host.state.dictations[0].finalText = "Fix the parser and run the tests.";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual([state.text, sent], ["Fix the parser and run the tests.", []]);
  // The mode changes in the settings: the next stop follows it.
  host.state.send = "on_stop";
  await speech.refresh();
  const plain = prompt();
  speech.toggle("b", plain.sink);
  await settle();
  host.state.dictations[1].finalText = "A composer with no send of its own.";
  speech.toggle("b", plain.sink);
  await settle();
  assert.equal(plain.state.text, "A composer with no send of its own.");
  speech.toggle("a", sink);
  await settle();
  host.state.dictations[2].finalText = "Now it is sent.";
  speech.toggle("a", sink);
  await settle();
  assert.deepEqual(sent, ["Fix the parser and run the tests. Now it is sent."]);
});
