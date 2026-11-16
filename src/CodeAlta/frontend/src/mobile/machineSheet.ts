import type { Machine } from "./contract";
import { allMachines, machineLimit, machineStatus, waitingWords } from "./machines";
import { el, icon } from "./dom";
import { createSheet } from "./sheet";

/** The label of the choice that shows every machine. */
export const allMachinesLabel = "All machines";

/**
 * The sheet of the machines: **All machines**, this computer and each machine it reaches, with how it is reached (direct or relayed),
 * when it was last seen when it is offline, and how many of its sessions wait for the person. A tap chooses one; a machine that does not
 * let this computer in, or that needs an update of CodeAlta to give its sessions, is listed, says so and cannot be chosen. The sheet is made here, outside the page, and added to it.
 */
export function createMachineSheet(options: Readonly<{
  document: Document;
  machines: () => readonly Machine[];
  /** `all`, or the id of the machine the list shows. */
  selected: () => string;
  now: () => number;
  choose: (machineId: string) => void;
}>) {
  const { document } = options;
  const sheet = el(document, "div", "sheet");
  sheet.id = "machine-sheet";
  sheet.hidden = true;
  const panel = el(document, "div", "sheet-panel");
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-modal", "true");
  panel.setAttribute("aria-labelledby", "machine-sheet-title");
  const title = el(document, "h2", null, "Machines");
  title.id = "machine-sheet-title";
  const list = el(document, "ul");
  list.id = "machine-list";
  panel.append(title, list);
  sheet.append(panel);
  document.body.append(sheet);
  const shown = createSheet(document, sheet);

  function row(id: string, name: string, status: string, waiting: string, state: string, disabled: boolean): HTMLLIElement {
    const on = options.selected() === id;
    const item = el(document, "li");
    const button = el(document, "button", on ? "on" : null);
    button.type = "button";
    button.dataset.machine = id;
    button.dataset.state = state;
    button.disabled = disabled;
    button.setAttribute("aria-pressed", String(on));
    const head = el(document, "span", "machine-row-name");
    head.append(icon(document, "computer"), el(document, "span", null, name));
    button.append(head);
    const small = el(document, "small", "machine-row-status", [status, waiting].filter(Boolean).join(" · "));
    button.append(small);
    button.addEventListener("click", () => { shown.close(); options.choose(id); });
    item.append(button);
    return item;
  }

  function render() {
    const machines = options.machines(), now = options.now();
    const waiting = machines.reduce((sum, machine) => sum + machine.waiting, 0);
    const reachable = machines.filter(machine => machine.state === "online").length;
    list.replaceChildren(
      row(allMachines, allMachinesLabel, `${machines.length} machines · ${reachable} online`, waitingWords(waiting), "all", false),
      ...machines.map(machine => row(machine.id, machine.this ? `${machine.name} (this computer)` : machine.name, [machineStatus(machine, now), machineLimit(machine)].filter(Boolean).join(" · "), waitingWords(machine.waiting), machine.state,
        // A machine that does not let this computer in, or whose CodeAlta is too old to give its sessions, is listed and cannot be chosen.
        !machine.this && (machine.state === "unauthorized" || !machine.features.includes("sessions.read")))));
  }

  return {
    open() { render(); shown.open(list.querySelector<HTMLElement>("button.on")); },
    close: shown.close,
    /** Draws the sheet again while it is open (the machines changed). */
    refresh() { if (shown.shown) render(); },
    get shown() { return shown.shown; },
  };
}
