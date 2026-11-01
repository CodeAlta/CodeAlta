import { Fragment } from "react";
import { Card } from "@blueprintjs/core";
import type { PluginSettingsPage } from "../canvases/canvasPages";
import { SettingsPage } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { SettingControl } from "./SettingControl";
import type { SettingEntry } from "./settingsCatalog";
import type { SettingValueSource } from "./settingValues";

/**
 * The pages of the settings plugins declare without a settings canvas of their own: one for each plugin, in the Extensions
 * group of Settings. Their page is `plugin/<key>`; a plugin with a settings canvas has its settings on the page of that canvas.
 */
export function typedSettingsPages(entries: readonly SettingEntry[]): readonly PluginSettingsPage[] {
  const pages = new Map<string, PluginSettingsPage>();
  for (const entry of entries) {
    if (!entry.plugin || pages.has(entry.page)) continue;
    const parts = entry.page.split("/");
    if (parts.length !== 2 || parts[0] !== "plugin") continue;
    pages.set(entry.page, Object.freeze({ pluginKey: parts[1], canvasId: "", title: entry.pluginName ?? entry.plugin, description: null, icon: null, plugin: null }));
  }
  return [...pages.values()];
}

/** The page of Settings a page of a plugin is, as the catalog of the settings names it. */
export const pluginPageId = (page: Pick<PluginSettingsPage, "pluginKey" | "canvasId">) => page.canvasId ? `plugin/${page.pluginKey}/${page.canvasId}` : `plugin/${page.pluginKey}`;

/**
 * The settings a plugin declares, drawn from their descriptors and grouped under their headings: each one is saved at
 * once in the configuration, and the plugin is told. `framed` draws the frame of a page of Settings around them, for a
 * plugin that has no settings canvas; otherwise they go above the canvas.
 */
export function PluginSettingsForm({ page, entries, source, revision, framed }: {
  page: PluginSettingsPage; entries: readonly SettingEntry[]; source: SettingValueSource; revision: unknown; framed: boolean;
}) {
  const { t } = useShellLanguage();
  const groups: { heading: string | null; entries: SettingEntry[] }[] = [];
  for (const entry of entries) {
    const last = groups.at(-1);
    if (last && last.heading === entry.section) last.entries.push(entry); else groups.push({ heading: entry.section, entries: [entry] });
  }
  const cards = <div className="settings-grid plugin-settings-form">{groups.map((group, index) => <Card key={`${group.heading ?? ""}:${index}`} className="appearance-settings">
    {group.heading && <h2>{group.heading}</h2>}
    {group.entries.map(entry => <Fragment key={entry.id}>
      <div className="settings-field" data-setting={entry.id}>
        <span className="settings-field-label">{entry.label}</span>
        <div className="settings-field-control"><SettingControl entry={entry} source={source} revision={revision} label={entry.label} /></div>
        {entry.help && <p className="settings-field-help">{entry.help}</p>}
        {entry.applies === "nextStart" && <p className="settings-field-help">{t("Applies at next startup")}</p>}
      </div>
    </Fragment>)}
  </Card>)}</div>;
  if (!framed) return cards;
  return <SettingsPage className="plugin-typed-settings" label={page.title} group="Extensions" title={{ text: page.title }} notice={null} loading={false} busy={false} onReload={() => { }}>
    {cards}
  </SettingsPage>;
}
