import { useState } from "react";
import { Checkbox, HTMLSelect, Switch, Tag } from "@blueprintjs/core";
import { globalConfig } from "#neoastra";
import { readTierState, recommendedAdaptive, tierLabels, tierNames, type AdaptiveChoice, type TierName, type TierState, type TierStore } from "./modelTiers";
import { useShellLanguage } from "./shellLanguage";

type SaveAdaptive = Pick<typeof globalConfig, "saveAdaptive">;

/**
 * Adaptive on Settings > Models, under the tiers: whether it is set up, the tier its turns run on, the tier a turn in
 * trouble continues on and the recheck, with how to choose the models. Saved at once; the tiers it names need models
 * before a session can be put on it, which the section says.
 */
export function AdaptiveSettings({ epoch, state, api, store, onSaved }: {
  epoch: string; state: TierState; api: SaveAdaptive; store: TierStore; onSaved: (state: TierState) => void;
}) {
  const { t } = useShellLanguage();
  const [shown, setShown] = useState<AdaptiveChoice | null | undefined>(undefined);
  const [notice, setNotice] = useState<string | null>(null);
  const adaptive = shown === undefined ? state.adaptive : shown;

  async function save(next: AdaptiveChoice | null) {
    setShown(next); setNotice(null);
    try {
      const reply = await api.saveAdaptive({ expectedEpoch: epoch, adaptive: next }, { timeoutMilliseconds: 30_000 });
      const saved = reply.status === "ok" ? readTierState(reply) : null;
      if (saved) { store.set(epoch, saved); onSaved(saved); setShown(undefined); return; }
      setShown(undefined);
      setNotice(reply.message ?? t("Adaptive was not saved ({status}).", { status: reply.status }));
    } catch {
      setShown(undefined);
      setNotice(t("Adaptive was not saved ({status}).", { status: "no_answer" }));
    }
  }

  // The first tier it names that has no model: a session cannot be put on Adaptive until it has one.
  const missing = adaptive ? [adaptive.code, adaptive.escalate].find(tier => state.tiers[tier].length === 0) : undefined;
  const tierSelect = (id: string, label: string, value: TierName, change: (tier: TierName) => void) =>
    <label className="adaptive-field" htmlFor={id}>
      <span>{label}</span>
      <HTMLSelect id={id} value={value} onChange={event => change(event.currentTarget.value as TierName)}>
        {tierNames.map(tier => <option key={tier} value={tier}>{t(tierLabels[tier])}</option>)}
      </HTMLSelect>
    </label>;

  return <section className="adaptive-settings" aria-labelledby="adaptive-title">
    <header>
      <h2 id="adaptive-title">{t("Adaptive")}<Tag minimal round intent="warning" className="adaptive-experimental">{t("Experimental")}</Tag></h2>
      <p className="settings-field-help">{t("A session on Adaptive runs its turns on one tier; a turn that changed code checks its change against the request, and a turn that ends in trouble continues once on a stronger tier.")}</p>
    </header>
    <Switch className="settings-checkbox" checked={!!adaptive} label={t("Set up Adaptive")}
      onChange={event => void save(event.currentTarget.checked ? state.adaptive ?? recommendedAdaptive : null)} />
    {adaptive && <div className="adaptive-fields">
      {tierSelect("adaptive-code", t("Turns run on"), adaptive.code, code => void save({ ...adaptive, code }))}
      {tierSelect("adaptive-escalate", t("A turn in trouble continues on"), adaptive.escalate, escalate => void save({ ...adaptive, escalate }))}
      <Checkbox className="settings-checkbox" checked={adaptive.recheck} label={t("Recheck each change of code against the request")}
        onChange={event => void save({ ...adaptive, recheck: event.currentTarget.checked })} />
      {missing && <p role="status" className="adaptive-missing">{t("Add a model to the {tier} tier to use Adaptive.", { tier: t(tierLabels[missing]) })}</p>}
    </div>}
    {notice && <p role="alert" className="error-text">{notice}</p>}
    <div className="adaptive-guidance">
      <h3>{t("How to choose the models")}</h3>
      <p>{t("Turns: a fast, inexpensive model that writes good code once it rechecks it. Escalation: your strongest model; it runs only when a turn ends in trouble.")}</p>
      <p>{t("Tested in October 2026: Claude Haiku 5.5 for the turns and Opus 5.5 on trouble came close to Opus alone on real repositories, for about a tenth of its cost. With Codex, choose GPT-6.1 Terra rather than GPT-6 Luna for the turns.")}</p>
      <p>{t("To compare other models by their results on coding tasks and their price, see independent leaderboards such as")}{" "}
        <a href="https://artificialanalysis.ai/" target="_blank" rel="noreferrer">Artificial Analysis</a>.</p>
    </div>
  </section>;
}
