import { useEffect, useRef } from "react";
import { showToast } from "../appToaster";
import type { MessageKey } from "../localization";
import { settingsNavigation } from "../settingsNavigation";
import { modelServersApi, type ModelServersApi, type ModelServersWatchEvent } from "./api";
import { modelName } from "./modelServers";
import type { DownloadInfo, HostTask, ModelServersMachine } from "./types";

type Translate = (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string;

/** The toast of the end of a download or of an install or update of llama.cpp, or null for what needs none (a cancelled download). */
export function modelServerToast(event: ModelServersWatchEvent, machine: string, t: Translate): Readonly<{ key: string; message: string; intent: "success" | "danger" }> | null {
  if (!event.json) return null;
  if (event.kind === "download") {
    const download = JSON.parse(event.json) as DownloadInfo;
    const name = modelName(download.model);
    if (download.state === "done") return { key: `model-download:${event.machine}:${download.id}`, intent: "success", message: t("{model} is on {machine}.", { model: name, machine }) };
    if (download.state === "failed") return { key: `model-download:${event.machine}:${download.id}`, intent: "danger",
      message: t("{model} could not be downloaded to {machine}: {reason}", { model: name, machine, reason: download.error ?? "" }) };
    return null;
  }
  if (event.kind === "task") {
    const task = JSON.parse(event.json) as HostTask;
    if (task.state === "done") return { key: `model-task:${event.machine}:${task.id}`, intent: "success", message: t("{machine}: {task}. {message}", { machine, task: task.title, message: task.message ?? "" }).trim() };
    if (task.state === "failed") return { key: `model-task:${event.machine}:${task.id}`, intent: "danger", message: t("{machine}: {task} failed. {message}", { machine, task: task.title, message: task.message ?? "" }).trim() };
  }
  return null;
}

/**
 * Tells the person, wherever they are in the window, when a model is on a machine, when its download failed, and when an
 * install or an update of llama.cpp ended: a toast whose button opens Settings > Fleet > Model servers.
 */
export function useModelServerToasts(epoch: string | null, t: Translate, given?: ModelServersApi): void {
  const translate = useRef(t);
  translate.current = t;
  useEffect(() => {
    const api = given ?? (epoch ? modelServersApi(epoch) : null);
    if (!api) return;
    const controller = new AbortController();
    let machines: readonly ModelServersMachine[] = [];
    // Listing the machines makes the host follow those online: the end of a download there comes as an event too.
    const list = () => void api.machines(controller.signal).then(value => { machines = value; }, () => { /* Read again at the next turn. */ });
    list();
    const timer = window.setInterval(list, 60_000);
    controller.signal.addEventListener("abort", () => window.clearInterval(timer));
    const nameOf = async (id: string) => {
      if (!machines.some(machine => machine.id === id)) machines = await api.machines(controller.signal).catch(() => machines);
      return machines.find(machine => machine.id === id)?.name ?? id;
    };
    void api.watch(controller.signal, event => {
      if (event.kind === "changed") return;
      void nameOf(event.machine).then(name => {
        if (controller.signal.aborted) return;
        const toast = modelServerToast(event, name, translate.current);
        if (!toast) return;
        showToast({ message: toast.message, intent: toast.intent, icon: toast.intent === "success" ? "tick" : "error", timeout: toast.intent === "success" ? 10_000 : 20_000,
          action: { text: translate.current("Model servers"), onClick: () => settingsNavigation.open("modelServers") } }, toast.key);
      });
    }).catch(() => { /* A window without model servers shows no toast of them. */ });
    return () => controller.abort();
  }, [epoch, given]);
}
