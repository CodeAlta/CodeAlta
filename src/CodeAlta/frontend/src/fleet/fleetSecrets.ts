import type { FleetSecretsOverview, FleetSecretView } from "#neoastra";
import type { MessageKey } from "../localization";

/** A scope a value can be set in, as the form offers it: its kind, its id on this computer, and how it is named. */
export type FleetScopeOption = Readonly<{ value: string; kind: "person" | "space" | "project" | "environment"; id: string | null; label: MessageKey; name: string | null }>;

/** The scopes of a server a value can be set in: all the work of the person, then each space, project and environment. */
export function scopeOptions(overview: FleetSecretsOverview): FleetScopeOption[] {
  return [
    { value: "person", kind: "person", id: null, label: "All my work", name: null },
    ...overview.spaces.map(space => ({ value: `space:${space.id}`, kind: "space" as const, id: space.id, label: "Space: {name}" as MessageKey, name: space.name })),
    ...overview.projects.map(project => ({ value: `project:${project.id}`, kind: "project" as const, id: project.id, label: "Project: {name}" as MessageKey, name: project.name })),
    ...overview.environments.filter(environment => environment.verified).map(environment => ({ value: `environment:${environment.id}`, kind: "environment" as const,
      id: environment.id, label: "Environment: {name}" as MessageKey, name: environment.name })),
  ];
}

/** How the scope of a value is named in its row. */
export function scopeLabel(secret: FleetSecretView): { key: MessageKey; name: string | null } {
  switch (secret.scopeKind) {
    case "person": return { key: "All my work", name: null };
    case "space": return { key: "Space: {name}", name: secret.scopeLabel };
    case "project": return { key: "Project: {name}", name: secret.scopeLabel };
    default: return { key: "Environment: {name}", name: secret.scopeLabel };
  }
}

/** The machines that hold a value now, by name; the person's other computers are not machines. */
export function holderNames(secret: FleetSecretView): string[] {
  return secret.holders.filter(holder => holder.machine).map(holder => holder.name);
}

/** Whether the value needs a rotation: a machine revoked since held its current version. */
export function needsRotation(secret: FleetSecretView): boolean {
  return secret.flagged.length > 0;
}

/** Whether a text can name an environment: letters, digits, `-` and `_`, at most 64. */
export function isEnvironmentName(name: string): boolean {
  return /^[A-Za-z0-9_-]{1,64}$/.test(name);
}

/** The labels typed in a field: separated by commas or spaces, each once, in the order typed. */
export function parseLabels(text: string): string[] {
  return [...new Set(text.split(/[\s,]+/).map(label => label.trim()).filter(label => label.length > 0))];
}

/** The approvals this computer's person may give now, the oldest first, so that the first run asked is approved first. */
export function waitingApprovals(overview: FleetSecretsOverview) {
  return overview.approvals.filter(approval => approval.status === "pending" && approval.mayApprove)
    .sort((left, right) => left.requestedAt.localeCompare(right.requestedAt));
}
