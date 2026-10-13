import assert from "node:assert/strict";
import test from "node:test";
import { createSpeech, type DictationSink } from "./speech";
import { createFixtureHost, createFixtureMicrophone } from "./speechFixture";
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

async function window(options: Parameters<typeof createFixtureHost>[0] = { installed: ["parakeet-tdt-0.6b-v3"] }) {
  const host = createFixtureHost(options), microphone = createFixtureMicrophone();
  const notices: string[] = [];
  const speech = createSpeech({ api: host.api, capture: microphone.start, notify: message => notices.push(message) });
  const disconnect = speech.connect(host.epoch);
  await settle();
  return { host, microphone, speech, notices, disconnect, status: () => speech.getState().dictation.status };
}

test("a dictation: the words are written as they are finished, the others greyed, and nothing twice", async () => {
  const { host, microphone, speech, notices, status } = await window();
  const { state, sink } = prompt();
  const levels: [number, string | null][] = [];
  speech.subscribeLevel((level, owner) => levels.push([level, owner]));
  speech.toggle("a", sink);
  assert.deepEqual(speech.getState().dictation, { status: "starting", owner: "a", partial: "" });
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
  assert.deepEqual(speech.getState().dictation, { status: "starting", owner: "b", partial: "" });
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
  const speech = createSpeech({ api: host.api, capture: microphone.start, notify: () => { } });
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
  const speech = createSpeech({ api: host.api, capture: microphone.start, notify: () => { } });
  speech.connect("before");
  await settle();
  assert.equal(speech.getState().listing?.status, "stale_epoch");
  // A replayed end of a download from before the page was there.
  const replay = createFixtureHost();
  replay.state.install = { state: "done", modelId: "whisper-small", modelName: "Whisper small", receivedBytes: "40", totalBytes: "40", error: null };
  const other = createSpeech({ api: replay.api, capture: microphone.start, notify: () => { } });
  other.connect(replay.epoch);
  await settle();
  assert.deepEqual([other.getState().install?.state, replay.state.reads], ["done", 1]);
});
