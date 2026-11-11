import { useEffect, useMemo, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, InputGroup, ProgressBar, Tag } from "@blueprintjs/core";
import { machines, type MachineProviderOfferView, type MachineProviderOperationView, type MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { ProviderIcon } from "../ProviderIcon";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { managementProblem } from "./machineManagement";
import { offerGroups, offerNeed, offerProgram, offerMatches } from "./providerOffers";

/** The calls of the picker of the providers a machine can add. */
export type ProviderPickerApi = Pick<typeof machines, "providerOffers" | "addProvider" | "providerOperationWait">;

/** What the picker did: a provider added (its key, and the offer it came from), or a type chosen whose key is typed next. */
export type ProviderPicked = { added: string; offer: MachineProviderOfferView } | { keyFor: MachineProviderOfferView };

type Problem = { key: MessageKey; detail?: string | null } | null;

/**
 * The providers a machine can add, as a list with their icons rather than a list of names: Claude Code and the agents of its
 * plugins (Codex CLI, the ACP registry), the subscriptions it signs in to, the APIs that take a key, its model servers. A filter
 * keeps the ones whose name or description has the words typed. An agent is added in one click: the machine installs the program it
 * runs when it is missing, with its progress here; a type that takes a key opens its form.
 */
export function ProviderPicker({ epoch, machine, api = machines, onClose }: {
  epoch: string; machine: MachineView; api?: ProviderPickerApi; onClose: (picked: ProviderPicked | null) => void;
}) {
  const { t } = useShellLanguage();
  const [offers, setOffers] = useState<readonly MachineProviderOfferView[] | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [problem, setProblem] = useState<Problem>(null);
  const [filter, setFilter] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [adding, setAdding] = useState<{ offer: MachineProviderOfferView; state: MachineProviderOperationView | null } | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    api.providerOffers({ expectedEpoch: epoch, machineId: machine.id, refresh: refresh > 0 }, { signal: abort.signal, timeoutMilliseconds: 60_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) { setProblem({ key: failed, detail: value.message }); return; }
      setOffers(value.offers);
      setCatalogError(value.error ?? null);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, refresh]);
  const groups = useMemo(() => offerGroups((offers ?? []).filter(offer => offerMatches(offer, filter))), [offers, filter]);

  async function add(offer: MachineProviderOfferView) {
    if (offer.credential === "key" || offer.credential === "none") { onClose({ keyFor: offer }); return; }
    setAdding({ offer, state: null });
    setProblem(null);
    try {
      let state = await api.addProvider({ expectedEpoch: epoch, machineId: machine.id, offerId: offer.id, key: null, makeDefault: false }, { timeoutMilliseconds: 60_000 });
      // The machine installs, then writes the provider: the picker follows it until it ends.
      while (state.status === "ok" && state.state === "running" && state.id) {
        setAdding({ offer, state });
        state = await api.providerOperationWait({ expectedEpoch: epoch, machineId: machine.id, id: state.id }, { timeoutMilliseconds: 40_000 });
      }

      if (state.status !== "ok") { setProblem({ key: managementProblem(state.status) ?? "The machine could not do it.", detail: state.message }); setAdding(null); return; }
      if (state.state !== "completed" || !state.key) { setProblem({ key: "{name} could not be added.", detail: state.message }); setAdding(null); return; }
      onClose({ added: state.key, offer });
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
      setAdding(null);
    }
  }

  const progress = adding?.state;
  return <Dialog isOpen className="machine-provider-picker" title={t("Add a provider to {machine}", { machine: machine.name })} canOutsideClickClose={!adding}
    onClose={() => { if (!adding) onClose(null); }}>
    <DialogBody>
      {adding ? <div className="machine-provider-adding" role="status">
        <p><ActivitySpinner size={14} /> {progress?.message ?? t("{machine} adds {name}…", { machine: machine.name, name: adding.offer.name })}</p>
        {progress?.fraction != null ? <ProgressBar intent="primary" value={progress.fraction} /> : <ProgressBar intent="primary" />}
      </div> : <>
        <InputGroup type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={filter} autoFocus placeholder={t("Filter the providers")} aria-label={t("Filter the providers")}
          onChange={event => setFilter(event.target.value)} />
        {!offers && !problem && <p className="muted-text"><ActivitySpinner size={14} /> {t("{machine} lists what it can run…", { machine: machine.name })}</p>}
        {catalogError && <Callout compact intent="warning">{catalogError}</Callout>}
        {groups.map(group => <section key={group.id} className="machine-offer-group" aria-label={t(group.label)}>
          <h3>{t(group.label)}</h3>
          <ul className="machine-offers">{group.offers.map(offer => {
            const program = offerProgram(offer);
            return <li key={offer.id} data-offer={offer.id}>
              <button type="button" className="machine-offer" disabled={!!offer.unavailable} onClick={() => void add(offer)}>
                <span className="machine-offer-icon"><ProviderIcon providerKey={offer.type} known={{ type: offer.type, name: offer.name, iconData: offer.icon }} size={22} /></span>
                <span className="machine-offer-main">
                  <span className="machine-offer-title"><strong>{offer.name}</strong>
                    {offer.configuredKey && <Tag minimal round>{t("Added")}</Tag>}</span>
                  <small className="muted-text">{[t(offerNeed(offer)), program && t(program.key, program.values)].filter(Boolean).join(" · ")}</small>
                  {offer.unavailable && <small className="machine-provider-problem">{offer.unavailable}</small>}
                </span>
              </button>
            </li>;
          })}</ul>
        </section>)}
        {offers && groups.length === 0 && <p className="muted-text">{t("No provider matches.")}</p>}
      </>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key, { name: adding?.offer.name ?? "" })}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {!adding && <Button minimal icon={<AppIcon name="refresh" size={14} />} text={t("Refresh")} onClick={() => setRefresh(value => value + 1)} />}
      <Button disabled={!!adding} text={t("Cancel")} onClick={() => onClose(null)} />
    </>} />
  </Dialog>;
}
