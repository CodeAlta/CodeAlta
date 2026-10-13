import { useEffect, useRef, useState } from "react";
import { dismissToast, showToast } from "../appToaster";
import { translate, type Locale } from "../localization";
import { startMicrophone, type StartCapture } from "./capture";
import { installToast } from "./InstallNotice";
import { createSpeech, type Speech, type SpeechApi } from "./speech";
import { installing, installNotice, type InstallNotice } from "./speechStore";

const installKey = "speech-install", dictationKey = "speech-dictation";
// A download tells its progress many times a second: the notice is drawn a few times a second.
const pace = 250;

/**
 * Speech to prompt of the window, owned by `App`: it follows the host of the epoch, and keeps one notice of
 * the download of a model up to date, whatever page or tab is shown. Nothing it follows is state of `App`:
 * a level, a percentage or a word renders the control that shows it.
 */
export function useSpeechInstall(api: SpeechApi, epoch: string | null, locale: Locale, capture: StartCapture = startMicrophone): Speech {
  const language = useRef(locale); language.current = locale;
  const [speech] = useState(() => createSpeech({ api, capture,
    notify: message => showToast({ message: translate(language.current, message), intent: "danger", icon: "error", timeout: 10_000 }, dictationKey) }));
  useEffect(() => followInstall(speech, () => language.current), [speech]);
  useEffect(() => speech.connect(epoch), [speech, epoch]);
  return speech;
}

/** Shows the download as the state of `speech` tells it, until what it returns is called. */
export function followInstall(speech: Speech, locale: () => Locale, show = showToast, dismiss = dismissToast): () => void {
  let previous = speech.getState().install, reveals = speech.getState().reveals;
  // Closed by the user: the progress is not put back in front of them, until they ask for it or it ends.
  let closed = false;
  let last = 0, timer: ReturnType<typeof setTimeout> | undefined, waiting: InstallNotice | null = null;
  const draw = (notice: Exclude<InstallNotice, { kind: "dismiss" }>) => {
    last = Date.now();
    show(installToast(notice, locale(), () => speech.cancelInstall(), () => { closed = true; }), installKey);
  };
  const settle = () => { clearTimeout(timer); timer = undefined; waiting = null; };
  const stop = speech.subscribe(() => {
    const state = speech.getState(), install = state.install;
    if (install !== previous) {
      const before = previous;
      previous = install;
      // The host is gone: its download goes on there, and this window no longer follows it.
      const notice = install ? installNotice(before, install) : installing(before) ? { kind: "dismiss" } as const : null;
      if (notice?.kind === "progress") {
        if (!installing(before)) closed = false;
        if (closed) return;
        const wait = notice.phase === before?.state ? last + pace - Date.now() : 0;
        if (wait <= 0) { settle(); draw(notice); }
        else { waiting = notice; timer ??= setTimeout(() => { const next = waiting; settle(); if (next?.kind === "progress" && !closed) draw(next); }, wait); }
      } else if (notice) {
        settle();
        if (notice.kind === "dismiss") dismiss(installKey); else draw(notice);
      }
    }
    if (state.reveals !== reveals) {
      reveals = state.reveals;
      // Asked for again, by a click on a microphone or on Download while it runs.
      const notice = install && installing(install) ? installNotice(install, install) : null;
      if (notice?.kind === "progress") { closed = false; settle(); draw(notice); }
    }
  });
  return () => { stop(); settle(); };
}
