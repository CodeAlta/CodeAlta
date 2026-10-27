import { useContext, useEffect, useRef, useState } from "react";
import { ActivitySpinner } from "./ActivitySpinner";
import { useShellLanguage } from "./shellLanguage";
import { MessageReaderContext, type WholeMessage } from "./wholeMessage";

/** Where the whole text of a shortened message is: not asked for, being read, read, or not readable. */
export type WholeMessageState = Readonly<{ status: "idle" } | { status: "loading" }> | WholeMessage;

const idle: WholeMessageState = Object.freeze({ status: "idle" });

/**
 * Reads the whole text of a message of the timeline when asked: the state follows the offset of its row, and a read
 * still going when the row changes or leaves is abandoned.
 * @param offset The journal offset of the row; null for a row the journal does not have (a live or an echoed message).
 */
export function useWholeMessage(offset: string | null): Readonly<{ state: WholeMessageState; load: (() => void) | null }> {
  const read = useContext(MessageReaderContext);
  const [state, setState] = useState<Readonly<{ offset: string | null; value: WholeMessageState }>>({ offset, value: idle });
  const reading = useRef<AbortController | null>(null);
  useEffect(() => () => { reading.current?.abort(); reading.current = null; }, [offset]);
  const load = read && offset ? () => {
    reading.current?.abort();
    const controller = new AbortController();
    reading.current = controller;
    setState({ offset, value: { status: "loading" } });
    void read(offset, controller.signal).then(value => { if (!controller.signal.aborted) setState({ offset, value }); });
  } : null;
  return { state: state.offset === offset ? state.value : idle, load };
}

/** The line under a message the timeline shows the start of: it says so, and reads the rest when asked. */
export function WholeMessageNote({ state, load }: { state: WholeMessageState; load: (() => void) | null }) {
  const { t, locale } = useShellLanguage();
  if (state.status === "ok") {
    return state.complete ? null : <p className="message-shortened">{t("Only the first {characters} characters of this message are shown.",
      { characters: state.text.length.toLocaleString(locale) })}</p>;
  }
  return <p className="message-shortened">
    <span>{t("The rest of this message is too long to show here.")}</span>
    {load && <button type="button" className="quiet-button message-shortened-load" disabled={state.status === "loading"}
      onClick={event => { event.stopPropagation(); load(); }}>
      {state.status === "loading" && <ActivitySpinner size={10} />}{t("Show the whole message")}</button>}
    {state.status === "failed" && <span role="alert" className="message-shortened-failed">{t("The message could not be read.")}</span>}
  </p>;
}
