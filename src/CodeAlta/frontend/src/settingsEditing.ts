import type { McpServerEdit, McpServerEntry, McpServerValueName } from "#neoastra";
import type { MessageKey } from "./localization";

/** Where a setting is stored: the user's global configuration or the selected project's. */
export type SettingsScope = "Global" | "Project";
export type SettingsNotice = Readonly<{ key: MessageKey; intent: "success" | "warning" | "danger"; detail?: string | null }>;

/** The user-facing reason a settings read or write did not succeed; null when it did. */
export function settingsFailure(status: string, message?: string | null): SettingsNotice | null {
  switch (status) {
    case "ok": return null;
    case "unavailable": return { key: "Editing settings requires the desktop app.", intent: "warning" };
    case "stale_epoch": return { key: "CodeAlta restarted. Close and reopen Settings.", intent: "warning" };
    case "unknown_project": case "archived_project": case "project_unavailable":
      return { key: "The selected project is not available for editing.", intent: "warning" };
    case "read_failed": return { key: "The settings could not be read.", intent: "danger" };
    case "write_failed": return { key: "The change could not be written.", intent: "danger" };
    case "config_invalid": return { key: "The configuration file could not be parsed. Fix it in Configuration file first.", intent: "danger" };
    case "conflict": return { key: "The name is already used, or it changed on disk. Reload and try again.", intent: "warning" };
    case "not_found": return { key: "It no longer exists. Reload to see the current list.", intent: "warning" };
    case "invalid": return { key: "The values were not accepted.", intent: "danger", detail: message ?? null };
    case "read_only": return { key: "Built-in items cannot be changed.", intent: "warning" };
    case "too_large": return { key: "The text is too large.", intent: "danger" };
    case "policy_failed": return { key: "Saved, but the enabled state could not be updated.", intent: "warning" };
    default: return { key: "The operation did not complete.", intent: "danger", detail: message ?? null };
  }
}

/** One editable environment variable or HTTP header. `stored` marks a value the host keeps and never returns. */
export type NameValueRow = Readonly<{ name: string; value: string; stored: boolean }>;
export type McpServerForm = Readonly<{
  key: string; scope: SettingsScope; transport: "Stdio" | "Http"; enabled: boolean;
  command: string; arguments: string; workingDirectory: string; url: string;
  environment: readonly NameValueRow[]; headers: readonly NameValueRow[];
}>;

const rows = (values: readonly McpServerValueName[] | undefined): NameValueRow[] =>
  (values ?? []).map(value => ({ name: value.name, value: "", stored: value.hasValue }));

/** The edit form of an existing server, or a blank standard-input server for a new one. */
export function mcpServerForm(entry: McpServerEntry | null, scope: SettingsScope = "Global"): McpServerForm {
  if (!entry) return { key: "", scope, transport: "Stdio", enabled: true, command: "", arguments: "", workingDirectory: "", url: "", environment: [], headers: [] };
  return { key: entry.key, scope: entry.scope === "Project" ? "Project" : "Global", transport: entry.transport === "Http" ? "Http" : "Stdio", enabled: entry.enabled,
    command: entry.command ?? "", arguments: entry.arguments.join("\n"), workingDirectory: entry.workingDirectory ?? "", url: entry.url ?? "",
    environment: rows(entry.environment), headers: rows(entry.headers) };
}

export function mcpServerFormDirty(form: McpServerForm, baseline: McpServerForm): boolean {
  return JSON.stringify(form) !== JSON.stringify(baseline);
}

/** The first problem that prevents saving, or null. Keys follow the host's `[A-Za-z0-9._-]{1,128}` rule. */
export function validateMcpServerForm(form: McpServerForm, existing: readonly Pick<McpServerEntry, "key" | "scope">[],
  original: Pick<McpServerEntry, "key" | "scope"> | null): MessageKey | null {
  const key = form.key.trim();
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(key)) return "Use 1 to 128 letters, digits, dots, dashes or underscores for the name.";
  if (existing.some(entry => entry.key === key && entry.scope === form.scope && !(original && original.key === entry.key && original.scope === entry.scope)))
    return "A server with this name already exists in that scope.";
  if (form.transport === "Stdio" && !form.command.trim()) return "Enter the command that starts the server.";
  if (form.transport === "Http" && !/^https?:\/\/\S+$/i.test(form.url.trim()) && form.url.trim() !== "[redacted]") return "Enter the server URL (http or https).";
  for (const list of [form.environment, form.headers]) {
    const names = list.map(row => row.name.trim());
    if (names.some(name => name === "")) return "Every variable and header needs a name.";
    if (new Set(names).size !== names.length) return "Variable and header names must be unique.";
  }
  return null;
}

/** The wire edit: a blank value of a stored variable keeps the stored secret (sent as null). */
export function mcpServerEdit(form: McpServerForm): McpServerEdit {
  const values = (list: readonly NameValueRow[]) => list.map(row => ({ name: row.name.trim(), value: row.value === "" && row.stored ? null : row.value }));
  const stdio = form.transport === "Stdio";
  return { key: form.key.trim(), transport: form.transport, enabled: form.enabled,
    command: stdio ? form.command.trim() : null,
    arguments: stdio ? form.arguments.split(/\r?\n/).map(line => line.trim()).filter(line => line !== "") : null,
    workingDirectory: stdio && form.workingDirectory.trim() ? form.workingDirectory.trim() : null,
    url: stdio ? null : form.url.trim(),
    environment: stdio ? values(form.environment) : null, headers: stdio ? null : values(form.headers) };
}

/** Stable list identity of a scoped item. */
export const scopedKey = (scope: string, key: string) => `${scope}\u0000${key}`;
