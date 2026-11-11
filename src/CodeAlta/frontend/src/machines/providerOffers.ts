import type { MachineProviderOfferView, MachineProviderProgramView } from "#neoastra";
import type { MessageKey } from "../localization";

/** A group of the picker of providers: the agents that run on the machine first, then the subscriptions, the APIs and the model servers. */
export interface OfferGroup { id: string; label: MessageKey; offers: MachineProviderOfferView[] }

const groupOrder: readonly { id: string; label: MessageKey }[] = [
  { id: "agent", label: "Agents that run on the machine" },
  { id: "subscription", label: "Subscriptions" },
  { id: "key", label: "APIs with a key" },
  { id: "local", label: "Model servers" },
];

/** The offers by group, in the order of the picker; a group without offers is left out, and an offer of an unknown group goes with the APIs. */
export function offerGroups(offers: readonly MachineProviderOfferView[]): OfferGroup[] {
  const known = new Set(groupOrder.map(group => group.id));
  return groupOrder.map(group => ({ ...group, offers: offers.filter(offer => (known.has(offer.group) ? offer.group : "key") === group.id) }))
    .filter(group => group.offers.length > 0);
}

/** Whether an offer has every word typed in its name, its type, its description or the name of its catalog. */
export function offerMatches(offer: MachineProviderOfferView, filter: string): boolean {
  const words = filter.toLowerCase().split(/\s+/u).filter(Boolean);
  const text = [offer.name, offer.type, offer.description, offer.catalog].filter(Boolean).join(" ").toLowerCase();
  return words.every(word => text.includes(word));
}

/** What a provider of an offer needs to run, in words. */
export function offerNeed(offer: Pick<MachineProviderOfferView, "credential" | "group">): MessageKey {
  switch (offer.credential) {
    case "key": return "Takes an API key";
    case "none": return offer.group === "local" ? "Runs on a model server of the machine" : "Needs nothing";
    case "cli": return "Signs in with its own account";
    default: return "Signs in with an account";
  }
}

/** What an offer installs or runs on the machine, in words; null when it runs no program, or cannot run there. */
export function offerProgram(offer: Pick<MachineProviderOfferView, "program">): { key: MessageKey; values?: Record<string, string> } | null {
  const program = offer.program;
  if (!program) return null;
  switch (program.state) {
    case "installed": return program.version ? { key: "Version {version} is installed", values: { version: program.version } } : { key: "Installed by the machine" };
    case "found": return { key: "Found on the machine" };
    case "missing": return program.version ? { key: "Installs version {version}", values: { version: program.version } } : { key: "Installs it" };
    case "runner": return { key: "Runs with {runner}", values: { runner: program.detail || "npx" } };
    default: return null;
  }
}

/** The program a provider of a machine runs, in words, as its row says it; null for a provider that runs none. */
export function programLabel(program: MachineProviderProgramView | null | undefined): { key: MessageKey; values?: Record<string, string> } | null {
  if (!program) return null;
  switch (program.state) {
    case "installed": return program.version ? { key: "Version {version}, installed by the machine", values: { version: program.version } } : { key: "Installed by the machine" };
    case "found": return program.detail ? { key: "Runs {path}", values: { path: program.detail } } : { key: "Found on the machine" };
    case "missing": return { key: "Not installed on the machine" };
    case "runner": return { key: "Runs with {runner}", values: { runner: program.detail || "npx" } };
    default: return program.detail ? { key: "It cannot run there: {detail}", values: { detail: program.detail } } : { key: "It cannot run there." };
  }
}

/** What the row of a provider offers for its program: install it, update it, or nothing. */
export function programAction(program: MachineProviderProgramView | null | undefined): { kind: "install" } | { kind: "update"; version: string } | null {
  if (!program) return null;
  if (program.state === "missing") return program.update ? { kind: "update", version: program.update } : { kind: "install" };
  return program.update ? { kind: "update", version: program.update } : null;
}
