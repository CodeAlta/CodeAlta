import type { GlobalConfigPluginProviderType, GlobalConfigPluginSettingValue, GlobalConfigProvider } from "#neoastra";

/**
 * The values of the settings of a provider of a plugin as the form edits them, by key: a text as it is, a list one
 * item per line, names and values one `NAME=value` per line, a switch `true` or `false`.
 */
export type PluginFieldValues = Readonly<Record<string, string>>;

/** The provider type of a plugin that a type names, or null for a built-in type. */
export function pluginType(types: readonly GlobalConfigPluginProviderType[] | null | undefined, type: string): GlobalConfigPluginProviderType | null {
  return types?.find(candidate => candidate.type === type) ?? null;
}

/** The values of the settings of a provider as the form edits them; empty for a new provider. */
export function pluginFieldValues(provider: GlobalConfigProvider | null): PluginFieldValues {
  const values: Record<string, string> = {};
  for (const value of provider?.plugin?.values ?? []) values[value.key] = fieldText(value);
  return values;
}

function fieldText(value: GlobalConfigPluginSettingValue): string {
  if (value.list) return value.list.join("\n");
  if (value.map) return value.map.map(entry => `${entry.name}=${entry.value}`).join("\n");
  if (value.flag !== undefined && value.flag !== null) return value.flag ? "true" : "false";
  return value.text ?? "";
}

/** The values of the settings of a type to save, from the form: blank values are left out, read-only ones are not sent. */
export function pluginSettingValues(type: GlobalConfigPluginProviderType, values: PluginFieldValues): GlobalConfigPluginSettingValue[] {
  return type.settings.filter(setting => !setting.readOnly).map(setting => {
    const text = values[setting.key] ?? "";
    switch (setting.kind) {
      case "list": return { key: setting.key, list: lines(text) };
      case "map": return { key: setting.key, map: lines(text).map(line => {
        const equals = line.indexOf("=");
        return equals < 0 ? { name: line.trim(), value: "" } : { name: line.slice(0, equals).trim(), value: line.slice(equals + 1) };
      }).filter(entry => entry.name.length > 0) };
      case "boolean": return { key: setting.key, flag: text === "" ? null : text === "true" };
      default: return { key: setting.key, text: text.trim() || null };
    }
  });
}

/** The first problem of the settings of a type that prevents saving, or null. */
export function pluginFieldsProblem(type: GlobalConfigPluginProviderType, values: PluginFieldValues): "A line of the environment has no name before =." | null {
  for (const setting of type.settings) {
    if (setting.kind !== "map" || setting.readOnly) continue;
    if (lines(values[setting.key] ?? "").some(line => line.trimStart().startsWith("="))) return "A line of the environment has no name before =.";
  }
  return null;
}

/** Whether two sets of values are the same, for the settings of a type. */
export function samePluginFieldValues(left: PluginFieldValues, right: PluginFieldValues): boolean {
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  for (const key of keys) if ((left[key] ?? "") !== (right[key] ?? "")) return false;
  return true;
}

function lines(text: string): string[] {
  return text.replaceAll("\r\n", "\n").split("\n").map(line => line.trimEnd()).filter(line => line.trim().length > 0);
}

/** The size of a download as the page writes it: KB or MB. */
export function downloadSize(bytes: number | null | undefined): string {
  if (!bytes || bytes <= 0) return "0 KB";
  return bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
