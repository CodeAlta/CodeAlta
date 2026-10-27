import { createContext } from "react";
import type { MessageTextRequest, MessageTextResponse } from "#neoastra";

/**
 * The whole text of a message whose history row has only its start: the page gives each row a share of its size.
 * `complete` is false when the text is longer than what the window reads of it ({@link maximumWholeMessageCharacters}).
 */
export type WholeMessage = Readonly<{ status: "ok"; text: string; complete: boolean } | { status: "failed" }>;

/** Reads the whole text of a message of the timeline by the journal offset of its row. */
export type MessageReader = (offset: string, signal: AbortSignal) => Promise<WholeMessage>;

/** Reads the messages of the session a timeline shows; null where nothing reads them (a detached fixture). */
export const MessageReaderContext = createContext<MessageReader | null>(null);

/** The most characters of a message the window reads and renders: beyond it, the rest stays in the journal. */
export const maximumWholeMessageCharacters = 4 * 1024 * 1024;

/** Creates the reader of the messages of a session, over `toolCalls.readMessage`, which serves a text in parts. */
export function createMessageReader(read: (request: MessageTextRequest, options: { signal: AbortSignal }) => Promise<MessageTextResponse>,
  epoch: string, sessionId: string): MessageReader {
  return async (offset, signal) => {
    let text = "";
    try {
      for (;;) {
        const reply = await read({ expectedEpoch: epoch, sessionId, offset, position: text.length }, { signal });
        if (reply.status !== "ok" || !reply.text || reply.text.text.length === 0 && reply.text.more) return { status: "failed" };
        text += reply.text.text;
        if (!reply.text.more) return { status: "ok", text, complete: true };
        if (text.length >= maximumWholeMessageCharacters) return { status: "ok", text, complete: false };
      }
    } catch {
      return { status: "failed" };
    }
  };
}
