import { useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { listMicrophones, microphoneChoice, type InputDevice, type MicrophoneChoice } from "./microphones";
import type { Speech } from "./speech";

const noDevices: readonly InputDevice[] = Object.freeze([]);

/**
 * The list of microphones, in the setup of a composer and in the settings: the one the system uses, named when
 * the browser tells it, then each device. The device that was chosen stays in the list when it is not connected.
 */
export function MicrophoneSelect({ speech, value, onChange, id, disabled = false, fill = false }: {
  speech: Speech; value: MicrophoneChoice; onChange: (choice: MicrophoneChoice) => void; id?: string; disabled?: boolean; fill?: boolean;
}) {
  const { t } = useShellLanguage();
  const devices = useSyncExternalStore(speech.subscribe, () => speech.getState().devices ?? noDevices);
  const list = useMemo(() => listMicrophones(devices, value), [devices, value]);
  return <HTMLSelect id={id} className="speech-microphone-select" fill={fill} value={list.chosen} disabled={disabled} aria-label={id ? undefined : t("Microphone")}
    onChange={event => onChange(microphoneChoice(devices, event.currentTarget.value, value))}>
    <option value="">{list.systemName ? t("System default — {name}", { name: list.systemName }) : t("System default")}</option>
    {list.entries.map(entry => <option key={entry.id} value={entry.id}>{entry.connected ? entry.name : t("{name} (not connected)", { name: entry.name })}</option>)}
  </HTMLSelect>;
}

/**
 * The level of the microphone that is open for who asked (`source`), as a bar: about ten levels a second,
 * written to the element and never rendered. It is empty while that microphone is not open.
 */
export function LevelBar({ speech, source }: { speech: Speech; source: string }) {
  const { t } = useShellLanguage();
  const bar = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    const element = bar.current;
    if (!element) return;
    const stop = speech.subscribeLevel((level, of) => { if (of === source) element.style.width = `${Math.round(level * 100)}%`; });
    return () => { stop(); element.style.width = "0%"; };
  }, [speech, source]);
  return <span className="speech-level" role="img" aria-label={t("Microphone level")}><span ref={bar} style={{ width: "0%" }} /></span>;
}
