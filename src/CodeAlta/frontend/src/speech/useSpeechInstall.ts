import { useEffect, useRef, useState } from "react";
import type { ToastProps } from "@blueprintjs/core";
import { dismissToast, showToast } from "../appToaster";
import { translate, type Locale } from "../localization";
import { settingsNavigation } from "../settingsNavigation";
import { systemMicrophones } from "./capture";
import { installToast } from "./InstallNotice";
import { createSpeech, type Speech, type SpeechApi, type SpeechDependencies } from "./speech";
import { installing, installNotice, type InstallNotice, type SpeechNotice } from "./speechStore";

const installKey = "speech-install";
// A download tells its progress many times a second: the notice is drawn a few times a second.
const pace = 250;

/**
 * Speech to prompt of the window, owned by `App`: it follows the host of the epoch, and keeps one notice of
 * the download of a model up to date, whatever page or tab is shown. Nothing it follows is state of `App`:
 * a level, a percentage or a word renders the control that shows it.
 */
export function useSpeechInstall(api: SpeechApi, epoch: string | null, locale: Locale,
  options: Partial<Omit<SpeechDependencies, "api" | "notify">> = {}): Speech {
  const language = useRef(locale); language.current = locale;
  const made = useRef<Speech | null>(null);
  const [speech] = useState(() => createSpeech({ api, microphones: systemMicrophones, ...options,
    notify: notice => showToast(noticeToast(notice, language.current, () => {
      // The setup of the composer that dictated, beside its button; the settings when that composer is no longer there.
      if (!made.current?.proposeSetup(notice.setup)) settingsNavigation.open("speech");
    }), `speech:${notice.message}`) }));
  made.current = speech;
  useEffect(() => followInstall(speech, () => language.current), [speech]);
  useEffect(() => speech.connect(epoch), [speech, epoch]);
  return speech;
}

/** The toast of what the page tells of a dictation or of its microphone; one that offers to choose a microphone stays longer. */
export function noticeToast(notice: SpeechNotice, locale: Locale, onSetup: () => void): ToastProps {
  return { message: translate(locale, notice.message, notice.parameters), intent: notice.intent, icon: notice.intent === "danger" ? "error" : "warning-sign",
    timeout: notice.setup ? 20_000 : 10_000,
    ...(notice.setup ? { action: { text: translate(locale, "Choose a microphone"), onClick: onSetup } } : {}) };
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
