import { useContext, useEffect, useId, useMemo, useRef, useState, useSyncExternalStore, type RefObject } from "react";
import { flushSync } from "react-dom";
import { Button, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { modalDialogOpen } from "../modalDialogs";
import type { PromptInput } from "../PromptEditor";
import { settingsNavigation } from "../settingsNavigation";
import { useShellLanguage } from "../shellLanguage";
import { storageSize } from "../storage/storage";
import { microphoneAvailable } from "./capture";
import { LevelBar, MicrophoneSelect } from "./MicrophoneChoice";
import { shortMicrophoneName, type MicrophoneChoice } from "./microphones";
import { SpeechContext, type DictationSink, type Speech } from "./speech";
import { chosenModel, micView, modelBytes, modelLanguages, setupDownloads, type MicView } from "./speechStore";
import { sendMode } from "./stopping";

const labels: Readonly<Record<Exclude<MicView, "hidden">, MessageKey>> = {
  idle: "Dictate the prompt (F9)", starting: "Starting the dictation…", recording: "Stop dictation (F9)",
  finishing: "Finishing the dictation…", installing: "The speech model is being downloaded…",
};

/**
 * The microphone of a composer, just before Send: a click dictates into its prompt, another one stops. The
 * first click shows its setup beside it. It is absent where the window has no speech to prompt, where it is
 * turned off, and where the page has no microphone.
 */
export function MicButton(props: {
  /** What names the composer among those of the window: one of them dictates at a time. */
  owner: string;
  /** The prompt the words go into. */
  input: RefObject<PromptInput | null>;
  /** The prompt takes no text now (sent, read-only, edited in the large window): a dictation of it ends. */
  disabled?: boolean;
  /** The composer is shown; a dictation ends when its pane is hidden. */
  visible?: boolean;
  id?: string;
  /**
   * Sends the prompt as Enter does. Called after the last words of a dictation the user stopped are written,
   * when the settings say that a dictation is sent; a composer that gives none never sends by itself.
   */
  onSend?: () => void;
}) {
  const speech = useContext(SpeechContext);
  return speech ? <Microphone speech={speech} {...props} /> : null;
}

function Microphone({ speech, owner, input, disabled = false, visible = true, id, onSend }: Parameters<typeof MicButton>[0] & { speech: Speech }) {
  const { t } = useShellLanguage();
  const [microphone] = useState(microphoneAvailable);
  const view = useSyncExternalStore(speech.subscribe, () => micView(speech.getState(), owner, microphone));
  const asking = useSyncExternalStore(speech.subscribe, () => speech.getState().asking === owner);
  // The microphone a dictation of this composer hears, once the browser named it.
  const heard = useSyncExternalStore(speech.subscribe, () => { const dictation = speech.getState().dictation; return dictation.owner === owner ? dictation.microphone : null; });
  const button = useRef<HTMLButtonElement>(null);
  // What sends the prompt, as the composer has it now: a dictation that sends reads it after its last words are written.
  const send = useRef(onSend); send.current = onSend;
  const sink = useMemo<DictationSink>(() => ({
    // Written as a key is typed: the composer has the text before anything that follows reads its prompt.
    write: text => { flushSync(() => { input.current?.insertAtCaret(text); }); },
    interim: text => input.current?.setInterimText(text),
    focus: () => input.current?.focus(),
    send: () => send.current?.(),
  }), [input]);
  // What a click on the button does to the prompt when it stops a dictation.
  const sends = useSyncExternalStore(speech.subscribe, () => sendMode(speech.getState().listing?.send) === "on_stop");

  // A prompt that takes no text, a pane that is hidden, a composer that goes: what was being said is dropped.
  const usable = !disabled && visible;
  useEffect(() => { if (!usable) speech.abandon(owner); }, [speech, owner, usable]);
  useEffect(() => () => speech.abandon(owner), [speech, owner]);
  // While the button is there, a notice of the microphone can show the setup beside it.
  const shown = usable && view !== "hidden";
  useEffect(() => shown ? speech.attach(owner) : undefined, [speech, owner, shown]);

  // The ring of the button follows the voice. About ten levels a second: written to the element, never rendered.
  const recording = view === "recording";
  useEffect(() => {
    const element = button.current;
    if (!recording || !element) return;
    const stop = speech.subscribeLevel((level, of) => { if (of === owner) element.style.setProperty("--speech-level", level.toFixed(2)); });
    return () => { stop(); element.style.removeProperty("--speech-level"); };
  }, [speech, owner, recording]);

  // Escape stops the dictation of this composer, from its prompt or with the keyboard nowhere. It is taken before
  // the editor and the window see it, and only then: a dialog, a popover and another pane keep their Escape.
  const dictating = view === "starting" || view === "recording";
  useEffect(() => {
    if (!dictating) return;
    const key = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || event.repeat || event.isComposing || event.keyCode === 229
        || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey || modalDialogOpen()) return;
      const target = event.target instanceof Element ? event.target : null;
      if (target && target !== document.body && !button.current?.closest(".owned-session")?.contains(target)) return;
      event.preventDefault(); event.stopPropagation();
      speech.stop(owner);
    };
    window.addEventListener("keydown", key, true);
    return () => window.removeEventListener("keydown", key, true);
  }, [speech, owner, dictating]);

  if (view === "hidden") return null;
  // While it records the button says what a click does, and which microphone it hears.
  const name = heard ? shortMicrophoneName(heard) : null;
  const label = !recording ? t(labels[view])
    : sends && onSend ? name ? t("Stop dictation and send (F9) — {name}", { name }) : t("Stop dictation and send (F9)")
    : name ? t("Stop dictation (F9) — {name}", { name }) : t("Stop dictation (F9)");
  return <PopoverNext isOpen={asking} placement="top-end" popoverClassName="speech-setup-popover"
    content={<Setup speech={speech} owner={owner} sink={sink} />}
    onInteraction={next => { if (!next) speech.dismissAsk(owner); }}>
    <Button ref={button} id={id} className="speech-button" variant="minimal" data-speech={view} disabled={disabled}
      icon={view === "finishing" ? <ActivitySpinner size={16} /> : <AppIcon name="mic" size={16} />}
      aria-label={label} title={label} aria-pressed={recording}
      onClick={() => {
        // The click on the button of an open setup closes it, as a click elsewhere does.
        if (asking) speech.dismissAsk(owner);
        else speech.toggle(owner, sink);
      }} />
  </PopoverNext>;
}

/**
 * The setup of dictation, beside the button, until the user has said which microphone to use, and whenever
 * the model is missing: the microphone, with its level while this is open, and the model. Its answer keeps the
 * microphone and starts the dictation with the same press, or the download; nothing is kept or downloaded before.
 */
function Setup({ speech, owner, sink }: { speech: Speech; owner: string; sink: DictationSink }) {
  const { t, locale } = useShellLanguage();
  const id = useId();
  const confirm = useRef<HTMLButtonElement>(null);
  const [device, setDevice] = useState<MicrophoneChoice>(() => speech.getState().microphone.device);
  const model = useSyncExternalStore(speech.subscribe, () => chosenModel(speech.getState().listing));
  const downloads = useSyncExternalStore(speech.subscribe, () => setupDownloads(speech.getState()));
  const silent = useSyncExternalStore(speech.subscribe, () => { const monitor = speech.getState().monitor; return monitor?.key === owner && monitor.silent; });
  // The chosen microphone is open while this is: the bar shows at once whether it hears. Whatever closes this releases it.
  useEffect(() => { speech.startMonitor(owner, device); return () => speech.stopMonitor(owner); }, [speech, owner, device]);
  useEffect(() => speech.watchDevices(), [speech]);
  // The click that asked left the focus on the button: Enter then answers yes.
  useEffect(() => { const timer = setTimeout(() => confirm.current?.focus(), 0); return () => clearTimeout(timer); }, []);
  const languages = model ? modelLanguages(model) : null;
  const facts = model ? [modelBytes(model) > 0 ? storageSize(modelBytes(model), locale) : null, languages ? t(languages) : null].filter(Boolean).join(" · ") : "";
  return <div className="confirm-form speech-setup" role="dialog" aria-labelledby={`${id}-title`}
    onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); speech.dismissAsk(owner); } }}>
    <strong id={`${id}-title`}>{t("Set up dictation")}</strong>
    <label className="speech-setup-label" htmlFor={`${id}-microphone`}>{t("Microphone")}</label>
    <MicrophoneSelect speech={speech} id={`${id}-microphone`} fill value={device} onChange={setDevice} />
    <LevelBar speech={speech} source={owner} />
    {silent && <p className="speech-silent" role="status">{t("No sound comes from this microphone.")}</p>}
    {model && <>
      <span className="speech-setup-label">{t("Model")}</span>
      <span className="confirm-subject" title={model.name}>{model.name}</span>
      {facts && <p>{facts}</p>}
    </>}
    <div className="rename-actions">
      <Button className="speech-ask-other" variant="minimal" size="small" text={t("Choose another model")}
        onClick={() => { speech.dismissAsk(owner); settingsNavigation.open("speech"); }} />
      {/* The two answers stay together when the line is too short for the link beside them. */}
      <span className="speech-ask-answers">
        <Button variant="minimal" size="small" text={t("Cancel")} onClick={() => speech.dismissAsk(owner)} />
        {downloads
          ? <Button ref={confirm} intent="primary" size="small" text={t("Download")} data-speech-confirm="download" onClick={() => speech.confirmAndDownload(owner, device)} />
          : <Button ref={confirm} intent="primary" size="small" text={t("Start dictation")} data-speech-confirm="start" onClick={() => speech.confirmAndStart(owner, device, sink)} />}
      </span>
    </div>
  </div>;
}
