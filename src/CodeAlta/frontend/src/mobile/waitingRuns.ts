import { failureText, SignedOutError, type Api } from "./api";
import type { WaitingRun } from "./contract";
import { el, icon } from "./dom";

/** What a card says of a run that waits for the person. */
export function waitingLine(run: WaitingRun): string {
  const name = run.name ?? "A run of an automation";
  return run.machine || run.machineId ? `${name} on ${run.machine ?? run.machineId} waits for your approval.` : `${name} waits for your approval.`;
}

/**
 * The runs that wait for the person, above the sessions: of this computer, and of the machines of their servers, such as a
 * run of their team they are told of. Take over makes the run theirs: what it asks waits for them, and no timeout stops it.
 */
export function createWaitingRuns(options: Readonly<{
  document: Document;
  /** The place of the list, `#waiting`. */
  root: HTMLElement;
  api: Pick<Api, "waiting" | "takeOverRun">;
  /** The name of the computer, which a failure names. */
  host: string;
}>) {
  const { document, root } = options;
  let runs: readonly WaitingRun[] = [];
  const notes = new Map<string, string>(), sending = new Set<string>();

  function draw() {
    root.hidden = runs.length === 0;
    root.replaceChildren(...runs.map(run => {
      const card = el(document, "div", "waiting-card");
      card.dataset.run = run.runId;
      const line = el(document, "p", "waiting-line");
      line.append(icon(document, "shield"), el(document, "span", null, waitingLine(run)));
      const button = el(document, "button", "ghost", "Take over");
      button.type = "button";
      button.dataset.action = "takeover";
      button.disabled = sending.has(run.runId);
      button.addEventListener("click", () => void takeOver(run));
      card.append(line, button);
      const note = notes.get(run.runId);
      if (note) {
        const said = el(document, "p", "ask-note", note);
        said.setAttribute("role", "status");
        card.append(said);
      }
      return card;
    }));
  }

  async function takeOver(run: WaitingRun) {
    if (sending.has(run.runId)) return;
    sending.add(run.runId);
    notes.delete(run.runId);
    draw();
    try {
      const result = await options.api.takeOverRun(run);
      notes.set(run.runId, result.ok ? `You took the run over: answer it in its session${run.machine ? ` on ${run.machine}` : ""}.` : result.error ?? "The run was not taken over.");
    } catch (error) {
      if (error instanceof SignedOutError) return;
      notes.set(run.runId, `The run was not taken over: ${failureText(error, options.host)}`);
    } finally {
      sending.delete(run.runId);
    }
    draw();
  }

  return {
    /** Reads the runs that wait again; an older CodeAlta, or a failure, shows none. */
    async read() {
      try {
        const page = await options.api.waiting();
        runs = page.runs ?? [];
      } catch (error) {
        if (error instanceof SignedOutError) return;
        runs = [];
      }
      draw();
    },
  };
}
