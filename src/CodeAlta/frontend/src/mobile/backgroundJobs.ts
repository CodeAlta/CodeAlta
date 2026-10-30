import { failureText, SignedOutError, type Api } from "./api";
import type { BackgroundJob } from "./contract";
import { el } from "./dom";
import { createSheet } from "./sheet";
import { timelineIcon } from "./timelineIcons";
import { activityDuration } from "../timelineActivity/activityText";

const recentMilliseconds = 10 * 60_000;

/** The same running/recent window as the desktop, regardless of whether the agent's turn still runs. */
export function listedJobs(jobs: readonly BackgroundJob[], now: number): readonly BackgroundJob[] {
  return jobs.filter(job => job.state === "running" || !!job.endedAt && now - Date.parse(job.endedAt) < recentMilliseconds);
}

/** The outcome and duration of a command, as the desktop's list says them. */
export function jobFacts(job: BackgroundJob, now: number): string {
  const state = { running: "Running", succeeded: "Succeeded", failed: "Failed", timed_out: "Timed out", cancelled: "Stopped" }[job.state];
  const elapsed = activityDuration(Math.max(0, (job.endedAt ? Date.parse(job.endedAt) : now) - Date.parse(job.startedAt)));
  return [state, job.exitCode == null ? null : `Exit code ${job.exitCode}`, elapsed].filter(Boolean).join(" · ");
}

/** Background jobs above the phone's prompt: a count, a running/recent sheet, and the live output of a job. */
export function createBackgroundJobs({ window, root, api, changed }: {
  window: Window & typeof globalThis; root: HTMLElement; api: Pick<Api, "jobs" | "job" | "stopJob">; changed: () => void;
}) {
  const document = window.document;
  const make = <K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string) => el(document, tag, cls, text);
  const trigger = make("button", "background-status");
  trigger.type = "button";
  trigger.setAttribute("aria-haspopup", "dialog");
  root.append(trigger);
  const overlay = make("div", "sheet"), panel = make("div", "sheet-panel background-panel");
  overlay.hidden = true;
  panel.setAttribute("role", "dialog"); panel.setAttribute("aria-modal", "true"); panel.setAttribute("aria-labelledby", "background-title");
  const head = make("div", "sheet-head"), back = make("button", "sheet-done", "Back"), title = make("h2", undefined, "Background tasks"), done = make("button", "sheet-done", "Done");
  title.id = "background-title"; back.type = done.type = "button";
  head.append(back, title, done);
  const note = make("p", "muted"), list = make("div", "background-list"), output = make("div", "background-output");
  note.setAttribute("role", "status");
  const command = make("pre"), folder = make("p", "muted"), facts = make("p", "muted"), cut = make("p", "muted", "Showing the last 200 lines of retained output."), copy = make("button", "sheet-done", "Copy output"), text = make("pre"), stopOutput = make("button", "ghost", "Stop job");
  copy.type = stopOutput.type = "button";
  text.tabIndex = 0; text.setAttribute("aria-label", "Job output");
  output.append(facts, command, folder, stopOutput, copy, cut, text);
  panel.append(head, note, list, output); overlay.append(panel); document.body.append(overlay);
  let session: string | null = null, generation = 0, timer = 0, reading = false;
  let jobs: readonly BackgroundJob[] = [], shown: BackgroundJob | null = null, version = "", stopping = new Set<string>();
  const sheet = createSheet(document, overlay, () => { shown = null; version = ""; });
  const name = (job: BackgroundJob) => job.title || job.command;
  const errorText = (error: unknown) => failureText(error, window.location.host);

  function showOutput(job: BackgroundJob) {
    shown = job;
    title.textContent = name(job);
    back.hidden = false; list.hidden = true; output.hidden = false;
    command.textContent = job.command; folder.textContent = job.folder;
    facts.textContent = jobFacts(job, Date.now());
    stopOutput.hidden = job.state !== "running"; stopOutput.disabled = stopping.has(job.id);
  }
  function draw() {
    const listed = listedJobs(jobs, Date.now()), running = listed.filter(job => job.state === "running").length;
    root.hidden = listed.length === 0;
    const label = `${running || listed.length} ${running ? "in the background" : "ended"}`;
    if (trigger.textContent !== label) {
      const mark = make("span", "background-mark");
      mark.append(timelineIcon(document, "cog"));
      if (running) mark.append(timelineIcon(document, "spinner", "background-spin"));
      trigger.replaceChildren(mark, document.createTextNode(label));
    }
    trigger.setAttribute("aria-label", running ? `${running} background job${running === 1 ? "" : "s"} running` : "No background jobs running");
    if (!sheet.shown) return;
    if (shown) { showOutput(listed.find(job => job.id === shown!.id) ?? shown); return; }
    back.hidden = true; list.hidden = false; output.hidden = true; title.textContent = "Background tasks";
    const next = JSON.stringify(listed.map(job => [job.id, job.state, job.title, job.command, job.exitCode, stopping.has(job.id)]));
    if (next !== version) {
      const focused = document.activeElement as HTMLElement | null;
      const focusKey = focused && list.contains(focused) ? focused.dataset.jobAction : null;
      version = next;
      list.replaceChildren();
      for (const group of ["Running", "Recent"]) {
        const members = listed.filter(job => (job.state === "running") === (group === "Running"));
        if (!members.length) continue;
        const section = make("section"), heading = make("h3", undefined, group), ul = make("ul");
        for (const job of members) {
          const row = make("li"), open = make("button", "background-open"), label = make("span"), meta = make("small");
          row.dataset.state = job.state; open.type = "button";
          open.dataset.jobAction = `open:${job.id}`;
          meta.dataset.job = job.id; label.append(make("strong", undefined, name(job)), meta);
          open.append(timelineIcon(document, "terminal"), label, timelineIcon(document, "chevron"));
          open.setAttribute("aria-label", `Show the output of this job · ${name(job)}`);
          open.addEventListener("click", () => {
            note.textContent = ""; text.textContent = "Loading…"; cut.hidden = true;
            showOutput(job); back.focus(); void refresh();
          });
          row.append(open);
          if (job.state === "running") {
            const stop = make("button", "icon-btn"); stop.type = "button";
            stop.dataset.jobAction = `stop:${job.id}`;
            stop.setAttribute("aria-label", `Stop job · ${name(job)}`); stop.append(timelineIcon(document, "stop"));
            stop.disabled = stopping.has(job.id); stop.addEventListener("click", () => void stopJob(job.id)); row.append(stop);
          }
          ul.append(row);
        }
        section.append(heading, ul); list.append(section);
      }
      if (!listed.length) list.append(make("p", "muted", "No background jobs."));
      if (focusKey) {
        const controls = Array.from(list.querySelectorAll<HTMLButtonElement>("button"));
        (controls.find(button => button.dataset.jobAction === focusKey && !button.disabled)
          ?? controls.find(button => button.dataset.jobAction === focusKey.replace("stop:", "open:")) ?? done).focus();
      }
    }
    for (const meta of Array.from(list.querySelectorAll<HTMLElement>("small[data-job]"))) {
      const job = listed.find(job => job.id === meta.dataset.job);
      if (job) meta.textContent = jobFacts(job, Date.now());
    }
  }
  async function stopJob(jobId: string) {
    const id = session, revision = generation;
    if (!id || stopping.has(jobId)) return;
    stopping.add(jobId); draw(); note.textContent = "";
    try {
      const result = await api.stopJob(id, jobId);
      if (session === id && generation === revision && result.error) note.textContent = result.error;
    } catch (error) { if (generation === revision && !(error instanceof SignedOutError)) note.textContent = errorText(error); }
    finally { if (generation === revision) { stopping.delete(jobId); version = ""; draw(); void refresh(); } }
  }
  async function refresh() {
    if (!session || reading || document.hidden) return;
    const id = session, revision = generation;
    reading = true;
    window.clearTimeout(timer);
    try {
      const page = await api.jobs(id);
      if (generation !== revision) return;
      if (page.error) note.textContent = page.error;
      else { jobs = page.jobs; note.textContent = ""; }
      draw(); changed();
      const selected = shown?.id;
      if (sheet.shown && selected) {
        const result = await api.job(id, selected);
        if (generation !== revision || shown?.id !== selected || !sheet.shown) return;
        if (result.error) note.textContent = result.error;
        else {
          showOutput(result.job);
          const follow = text.scrollHeight - text.scrollTop - text.clientHeight < 40;
          if (text.textContent !== result.text) { text.textContent = result.text || "No output yet."; if (follow) text.scrollTop = text.scrollHeight; }
          cut.hidden = !result.truncated;
        }
      }
    } catch (error) { if (generation === revision && !(error instanceof SignedOutError)) note.textContent = `Could not read background jobs: ${errorText(error)}`; }
    finally {
      if (generation === revision) { reading = false; timer = window.setTimeout(() => void refresh(), 3000); }
    }
  }
  trigger.addEventListener("click", () => { shown = null; version = ""; trigger.focus(); sheet.open(done); draw(); void refresh(); });
  done.addEventListener("click", () => sheet.close());
  back.addEventListener("click", () => { shown = null; version = ""; draw(); list.querySelector<HTMLButtonElement>("button")?.focus(); });
  stopOutput.addEventListener("click", () => { if (shown) void stopJob(shown.id); });
  copy.addEventListener("click", () => { void window.navigator.clipboard.writeText(text.textContent ?? "").then(() => { note.textContent = "Output copied."; }, () => { note.textContent = "Could not copy the output."; }); });
  window.setInterval(() => { if (session && !document.hidden) { const hidden = root.hidden; draw(); if (hidden !== root.hidden) changed(); } }, 1000);
  return {
    refresh,
    /** Switches ownership and discards responses of the previous session. No reads run on the sessions list. */
    setSession(id: string | null) {
      if (session === id) return;
      sheet.close(); generation++; session = id; jobs = []; shown = null; version = ""; reading = false; stopping = new Set();
      window.clearTimeout(timer); root.hidden = true; note.textContent = "";
      if (id) void refresh();
    },
  };
}
