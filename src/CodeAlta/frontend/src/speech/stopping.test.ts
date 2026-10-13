import assert from "node:assert/strict";
import test from "node:test";
import { charactersToSend, enoughToSend, idleStopDelay, sendMode, sendModes, sendsOnStop, silentTooLong, spokenWords, wordsToSend, type StopReason } from "./stopping";

test("the words of a dictation are counted by the spaces between them", () => {
  assert.equal(spokenWords(""), 0);
  assert.equal(spokenWords("   \n "), 0);
  assert.equal(spokenWords("Hello"), 1);
  assert.equal(spokenWords("Fix the parser."), 3);
  assert.equal(spokenWords("  Fix\tthe\n\nparser,  please.  "), 4);
  assert.equal(spokenWords("l'état c'est moi"), 3);
});

test("a dictation is sent by itself when it wrote three words, or as much in a script without spaces", () => {
  assert.equal(wordsToSend, 3);
  assert.equal(enoughToSend(""), false);
  assert.equal(enoughToSend("Yes."), false);
  assert.equal(enoughToSend("Not yet"), false);
  assert.equal(enoughToSend("Fix the parser."), true);
  // One long word is one word.
  assert.equal(enoughToSend("internationalization"), false);
  // Japanese and Chinese have no spaces: a few characters are a few words.
  assert.equal(charactersToSend, 6);
  assert.equal(enoughToSend("はい"), false);
  assert.equal(enoughToSend("パーサーを直して"), true);
  assert.equal(enoughToSend("修复解析器的错误"), true);
  assert.equal(enoughToSend("修复"), false);
  // Mixed: the words count, or the characters do.
  assert.equal(enoughToSend("README を 直す"), true);
});

test("only a dictation the user stopped with the button is sent, and only in the mode that sends", () => {
  const text = "Fix the parser and run the tests.";
  const reasons: StopReason[] = ["button", "escape", "send", "silence", "taken", "abandoned", "ended"];
  // Keep the prompt: nothing is ever sent.
  for (const reason of reasons) assert.equal(sendsOnStop("manual", reason, text), false, reason);
  // Send the prompt: the button and its command (F9) only. Escape is the way to stop without sending.
  assert.deepEqual(reasons.filter(reason => sendsOnStop("on_stop", reason, text)), ["button"]);
  // Too little to send: the text stays.
  assert.equal(sendsOnStop("on_stop", "button", "Fix it"), false);
  assert.equal(sendsOnStop("on_stop", "button", ""), false);
  // A mode the page does not know, or none yet, keeps the prompt.
  for (const mode of [undefined, null, "", "always", "ON_STOP"]) assert.equal(sendsOnStop(mode, "button", text), false, String(mode));
  assert.deepEqual([...sendModes], ["manual", "on_stop"]);
  assert.deepEqual([sendMode("on_stop"), sendMode("manual"), sendMode("else"), sendMode(null), sendMode(undefined)], ["on_stop", "manual", "manual", "manual", "manual"]);
});

test("a dictation that has heard nothing since its last sound for the whole delay stops by itself", () => {
  assert.equal(idleStopDelay, 30_000);
  assert.equal(silentTooLong(0), false);
  assert.equal(silentTooLong(29_999), false);
  assert.equal(silentTooLong(30_000), true);
  assert.equal(silentTooLong(3_600_000), true);
  // The delay is a parameter: the silence is counted from the last sound, whatever the length of the dictation.
  assert.equal(silentTooLong(4_999, 5_000), false);
  assert.equal(silentTooLong(5_000, 5_000), true);
  // A stop for silence never sends, in the mode that sends either.
  assert.equal(sendsOnStop("on_stop", "silence", "Enough words to be sent by the user."), false);
});
