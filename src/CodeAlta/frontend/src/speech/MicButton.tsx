import { useContext, useEffect, useId, useMemo, useRef, useState, useSyncExternalStore, type RefObject } from "react";
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
import { SpeechContext, type DictationSink, type Speech } from "./speech";
import { chosenModel, micView, modelBytes, modelLanguages, type MicView } from "./speechStore";

const labels: Readonly<Record<Exclude<MicView, "hidden">, MessageKey>> = {
  idle: "Dictate the prompt (F9)", starting: "Starting the dictation…", recording: "Stop dictation (F9)",
  finishing: "Finishing the dictation…", installing: "The speech model is being downloaded…",
};

/**
 * The microphone of a composer, just before Send: a click dictates into its prompt, another one stops. It is
 * absent where the window has no speech to prompt, where it is turned off, and where the page has no microphone.
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
}) {
  const speech = useContext(SpeechContext);
  return speech ? <Microphone speech={speech} {...props} /> : null;
}

function Microphone({ speech, owner, input, disabled = false, visible = true, id }: Parameters<typeof MicButton>[0] & { speech: Speech }) {
  const { t } = useShellLanguage();
  const [microphone] = useState(microphoneAvailable);
  const view = useSyncExternalStore(speech.subscribe, () => micView(speech.getState(), owner, microphone));
  const asking = useSyncExternalStore(speech.subscribe, () => speech.getState().asking === owner);
  const button = useRef<HTMLButtonElement>(null);
  const sink = useMemo<DictationSink>(() => ({
    write: text => { input.current?.insertAtCaret(text); },
    interim: text => input.current?.setInterimText(text),
    focus: () => input.current?.focus(),
  }), [input]);

  // A prompt that takes no text, a pane that is hidden, a composer that goes: what was being said is dropped.
  const usable = !disabled && visible;
  useEffect(() => { if (!usable) speech.abandon(owner); }, [speech, owner, usable]);
  useEffect(() => () => speech.abandon(owner), [speech, owner]);

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
  const label = t(labels[view]);
  return <PopoverNext isOpen={asking} placement="top-end" popoverClassName="speech-ask-popover"
    content={<DownloadQuestion speech={speech} owner={owner} />}
    onInteraction={next => { if (!next) speech.dismissAsk(owner); }}>
    <Button ref={button} id={id} className="speech-button" variant="minimal" data-speech={view} disabled={disabled}
      icon={view === "finishing" ? <ActivitySpinner size={16} /> : <AppIcon name="mic" size={16} />}
      aria-label={label} title={label} aria-pressed={recording}
      onClick={() => {
        // The click on the button of an open question closes it, as a click elsewhere does.
        if (asking) speech.dismissAsk(owner);
        else speech.toggle(owner, sink);
      }} />
  </PopoverNext>;
}

/** The question of the first use, beside the button: nothing is downloaded before its answer. */
function DownloadQuestion({ speech, owner }: { speech: Speech; owner: string }) {
  const { t, locale } = useShellLanguage();
  const id = useId();
  const confirm = useRef<HTMLButtonElement>(null);
  const model = useSyncExternalStore(speech.subscribe, () => chosenModel(speech.getState().listing));
  // The click that asked left the focus on the button: Enter then answers yes.
  useEffect(() => { const timer = setTimeout(() => confirm.current?.focus(), 0); return () => clearTimeout(timer); }, []);
  const languages = model ? modelLanguages(model) : null;
  const facts = model ? [modelBytes(model) > 0 ? storageSize(modelBytes(model), locale) : null, languages ? t(languages) : null].filter(Boolean).join(" · ") : "";
  return <div className="confirm-form speech-ask" role="alertdialog" aria-labelledby={`${id}-title`} aria-describedby={facts ? `${id}-facts` : undefined}
    onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); speech.dismissAsk(owner); } }}>
    <strong id={`${id}-title`}>{t("Download the speech model?")}</strong>
    {model && <span className="confirm-subject" title={model.name}>{model.name}</span>}
    {facts && <p id={`${id}-facts`}>{facts}</p>}
    <div className="rename-actions">
      <Button className="speech-ask-other" variant="minimal" size="small" text={t("Choose another model")}
        onClick={() => { speech.dismissAsk(owner); settingsNavigation.open("speech"); }} />
      {/* The two answers stay together when the line is too short for the link beside them. */}
      <span className="speech-ask-answers">
        <Button variant="minimal" size="small" text={t("Cancel")} onClick={() => speech.dismissAsk(owner)} />
        <Button ref={confirm} intent="primary" size="small" text={t("Download")} data-speech-download
          onClick={() => { speech.dismissAsk(owner); void speech.install(null); }} />
      </span>
    </div>
  </div>;
}
