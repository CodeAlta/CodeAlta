// What ends a dictation, and what happens to the prompt then. The user chooses, in the Speech settings,
// whether a dictation they stop is sent; a dictation that ends in any other way never sends anything.

/** Why a dictation ended. */
export type StopReason =
  /** The microphone button, or its command (F9): the user is done speaking. */
  | "button"
  /** Escape: the user stops, and keeps the prompt to themselves. */
  | "escape"
  /** A send was asked while it recorded: its last words are written first, and the composer sends. */
  | "send"
  /** It heard nothing for a long time. */
  | "silence"
  /** Another composer, or a test of the microphone, took the microphone. */
  | "taken"
  /** Its composer can no longer take text: hidden, closed, read-only, or Settings over it. */
  | "abandoned"
  /** The host, the microphone or the stream ended it. */
  | "ended";

/** The send modes of the settings: keep the prompt, or send it when the user stops the dictation. */
export const sendModes = ["manual", "on_stop"] as const;
export type SendMode = typeof sendModes[number];
/** The mode the host tells; one the page does not know keeps the prompt. */
export const sendMode = (value: string | null | undefined): SendMode => value === "on_stop" ? "on_stop" : "manual";

/** The words a dictation must have written to be sent by itself: fewer is a slip of the button, or a word to correct. */
export const wordsToSend = 3;
// Scripts written without spaces between words: Thai and Lao, Burmese, Khmer, kana, Han. Ranges of code points and
// no regular expression: a bundle that writes such a class as its characters breaks in a page read in another encoding.
const unspacedRanges: readonly (readonly [number, number])[] = [[0x0e00, 0x0eff], [0x1000, 0x109f], [0x1780, 0x17ff], [0x3040, 0x30ff], [0x3400, 0x4dbf], [0x4e00, 0x9fff], [0xf900, 0xfaff]];
function unspacedCharacters(text: string): number {
  let count = 0;
  for (const character of text) {
    const code = character.codePointAt(0)!;
    if (unspacedRanges.some(([first, last]) => code >= first && code <= last)) count++;
  }
  return count;
}
/** The characters of such a script that stand for those few words. */
export const charactersToSend = 6;

/** The words of a text, as the spaces between them count them. */
export function spokenWords(text: string): number {
  return text.trim().split(/\s+/u).filter(Boolean).length;
}

/** Whether a dictation wrote enough to be sent by itself: three words, or as much in a script that has no spaces. */
export function enoughToSend(text: string): boolean {
  return spokenWords(text) >= wordsToSend || unspacedCharacters(text) >= charactersToSend;
}

/**
 * Whether the prompt is sent when a dictation ends: only in the mode that sends, only when the user stopped it
 * with the microphone button or its command, and only when it wrote enough. Escape stops without sending, and
 * a stop the user did not ask for never sends.
 */
export function sendsOnStop(mode: string | null | undefined, reason: StopReason, written: string): boolean {
  return sendMode(mode) === "on_stop" && reason === "button" && enoughToSend(written);
}
