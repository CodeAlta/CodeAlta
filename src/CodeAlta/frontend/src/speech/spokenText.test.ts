import assert from "node:assert/strict";
import test from "node:test";
import { spokenInsertion, unwritten } from "./spokenText";

test("a snapshot adds what was not written yet, and nothing twice", () => {
  let written = 0;
  const pieces: string[] = [];
  // The stream and the answer of the stop both tell the whole text: each is read against the same count.
  for (const finalText of ["", "Hello world.", "Hello world.", "Hello world. How are you?", "Hello world. How are you?"]) {
    const piece = unwritten(finalText, written);
    if (piece) pieces.push(piece);
    written = Math.max(written, finalText.length);
  }
  assert.deepEqual(pieces, ["Hello world.", " How are you?"]);
  assert.equal(unwritten("abc", 3), "");
  assert.equal(unwritten("abc", 7), "");
  assert.equal(unwritten("abc", -2), "abc");
});

test("a spoken piece is separated from the word before it, and from nothing else", () => {
  // The start of the prompt, of a line, and a space that is already there.
  assert.equal(spokenInsertion("", "Hello world."), "Hello world.");
  assert.equal(spokenInsertion("first line\n", "second"), "second");
  assert.equal(spokenInsertion("Fix ", "the parser"), "the parser");
  assert.equal(spokenInsertion("Fix\t", "the parser"), "the parser");
  // After a word or the end of a sentence.
  assert.equal(spokenInsertion("Fix", "the parser"), " the parser");
  assert.equal(spokenInsertion("Hello world.", "How are you?"), " How are you?");
  // The space that joins the utterances in the text of the host is not written twice.
  assert.equal(spokenInsertion("Hello world.", " How are you?"), " How are you?");
  assert.equal(spokenInsertion("Hello world. ", " How are you? "), "How are you?");
  // Punctuation goes against the word.
  for (const mark of [".", ",", ";", ":", "!", "?", "…", ")", "]", "}", "%"]) assert.equal(spokenInsertion("word", `${mark} next`), `${mark} next`, mark);
  // And a word against an opening bracket, a quote or a slash.
  for (const mark of ["(", "[", "{", "«", "“", "/"]) assert.equal(spokenInsertion(`see ${mark}`, "the file"), "the file", mark);
  // Only the last characters before the caret are needed.
  assert.equal(spokenInsertion("d", "next"), " next");
  // Nothing spoken writes nothing.
  assert.equal(spokenInsertion("word", ""), "");
  assert.equal(spokenInsertion("word", "  \n "), "");
  // Other scripts are words like any other.
  assert.equal(spokenInsertion("こんにちは", "世界"), " 世界");
});
