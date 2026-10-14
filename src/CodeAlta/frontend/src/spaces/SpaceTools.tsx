import { useEffect, useState, useSyncExternalStore } from "react";
import { Card } from "@blueprintjs/core";
import { globalConfig } from "#neoastra";
import { builtInToolNames, DisabledToolsField } from "../DisabledToolsField";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceSectionProps } from "./spaceSection";

/** What every session does without, and the names of the built-in tools, from the settings of the user. */
export type GlobalTools = Readonly<{ disabled: readonly string[]; known: readonly string[] }>;

/** Reads the tools every session does without; null when the host cannot say. */
export async function readGlobalTools(epoch: string): Promise<GlobalTools | null> {
  try {
    const reply = await globalConfig.disabledTools({ expectedEpoch: epoch }, { timeoutMilliseconds: 15_000 });
    return reply.status === "ok" ? { disabled: reply.tools, known: reply.known } : null;
  } catch { return null; }
}

/**
 * The section "Tools" of the page of a space: the built-in tools its sessions do without, saved at once. What the
 * Permissions page turns off for every session is shown beside them.
 */
export function SpaceTools({ space, hub, canEdit, busy, run, read = readGlobalTools }: SpaceSectionProps & Readonly<{
  /** Reads what every session does without; the Permissions page of Settings by default. */
  read?: (epoch: string) => Promise<GlobalTools | null>;
}>) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const epoch = hub.hostEpoch();
  const [global, setGlobal] = useState<GlobalTools | null>(null);
  useEffect(() => {
    if (!epoch) return;
    let alive = true;
    void read(epoch).then(value => { if (alive) setGlobal(value); });
    return () => { alive = false; };
  }, [epoch, read]);
  if (!state.available) return null;
  const id = `space-${space.id}`;
  return <Card className="appearance-settings space-section" aria-labelledby={`${id}-tools`}>
    <h2 id={`${id}-tools`}>{t("Tools")}</h2>
    <Field label={t("Tools turned off")} notice={<p className="settings-field-help">{t("The sessions of this space do without them.")}</p>}>
      <DisabledToolsField tools={space.disabledTools} known={global?.known ?? builtInToolNames} inherited={global?.disabled ?? []}
        disabled={!canEdit || busy} label={t("Tools turned off")} onChange={tools => void run(() => hub.setDisabledTools(space.id, tools), true)} />
    </Field>
  </Card>;
}
