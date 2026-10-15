import { useMemo, useState, useSyncExternalStore } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import { globalConfig } from "#neoastra";
import { composerTierEntries, readTierState, tierLists, tierStore, useTiers, type TierStore } from "../modelTiers";
import { ModelTierSettings, type TierProviderSpaces } from "../ModelTierSettings";
import { useProviderModels } from "../providerModels";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceSectionProps } from "./spaceSection";
import { resourceSpaces } from "./spaces";

type Save = Pick<typeof globalConfig, "saveModelTiers">;

/**
 * The model tiers and the fallback of the new sessions of a space: the global ones of Settings > Models, or tiers and a
 * fallback of its own, which replace them in the space. Customizing starts from a copy of the global tiers; the fallback
 * stays the global one until it is changed here. Its tiers offer the providers the space has.
 */
export function SpaceModelTiers({ space, hub, canEdit, busy, api = globalConfig, store = tierStore }: SpaceSectionProps & { api?: Save; store?: TierStore }) {
  const { t } = useShellLanguage();
  const epoch = hub.hostEpoch();
  const scope = { spaceId: space.id };
  const tiers = useTiers(epoch, scope, store);
  const { providers, models } = useProviderModels(epoch);
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const spaces = useMemo<TierProviderSpaces>(() => ({
    of: key => resourceSpaces(state.assignments, "provider", key),
    name: id => state.spaces.find(item => item.id === id)?.name ?? id,
  }), [state.assignments, state.spaces]);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const own = tiers?.source === "space";

  async function choose(value: string) {
    if (!epoch || !tiers || (value === "space") === own) return;
    setSaving(true); setNotice(null);
    try {
      // Customizing copies what the global tiers hold; going back removes the tiers of the space.
      const reply = await api.saveModelTiers({ expectedEpoch: epoch, tiers: value === "space" ? tierLists(tiers) : [], fallback: tiers.fallback,
        spaceId: space.id, useGlobal: value !== "space" }, { timeoutMilliseconds: 30_000 });
      const saved = reply.status === "ok" ? readTierState(reply) : null;
      if (saved) store.set(epoch, saved, scope);
      else setNotice(reply.message ?? t("The model tiers were not saved ({status}).", { status: reply.status }));
    } catch {
      setNotice(t("The model tiers were not saved ({status}).", { status: "no_answer" }));
    } finally { setSaving(false); }
  }

  const gives = composerTierEntries(tiers, t);
  return <div className="space-model-tiers">
    <Field label={t("Model tiers")} htmlFor={`space-tiers-${space.id}`}
      notice={!own && gives ? <p className="settings-field-help">{gives.map(entry => `${entry.label}: ${entry.detail ?? t("No model ready")}`).join(" · ")}</p> : undefined}>
      <HTMLSelect id={`space-tiers-${space.id}`} value={own ? "space" : "global"} disabled={!canEdit || busy || saving || !tiers || !epoch}
        onChange={event => void choose(event.target.value)}>
        <option value="global">{t("Use global")}</option>
        <option value="space">{t("Customize")}</option>
      </HTMLSelect>
    </Field>
    {notice && <p role="alert" className="error-text">{notice}</p>}
    {own && epoch && <ModelTierSettings epoch={epoch} spaceId={space.id} spaces={spaces} providers={providers} models={models} api={api} store={store} />}
  </div>;
}
