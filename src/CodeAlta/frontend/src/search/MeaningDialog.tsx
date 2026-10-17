import { useRef, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, Radio, RadioGroup } from "@blueprintjs/core";
import type { LocalSearchMeaning } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { frontLayer } from "../frontLayer";
import { useShellLanguage } from "../shellLanguage";
import { useLocalSearch } from "./localSearchContext";
import { durationParts, kilobytesText, meaningEstimate } from "./meaningEstimate";

/**
 * The dialog that turns search by meaning on: what it gives, what it takes (the download, the disk and the time of the
 * first indexing, estimated from the index), and the details one click away (the models, where the files come from and
 * go, what is indexed). Offered, it can be put off or refused; opened from Settings, it is only closed.
 */
export function MeaningDialog({ meaning, chunks, offered, onTurnOn, onLater, onNever, onClose, onKeepOut }: {
  meaning: LocalSearchMeaning;
  /** The chunks of the index, which the estimates are made from. */
  chunks: number;
  /** Whether the dialog comes from an offer, which can be put off or refused. */
  offered: boolean;
  onTurnOn: (modelId: string) => void;
  onLater: () => void;
  onNever: () => void;
  onClose: () => void;
  /** Opens Settings › Search, where projects and spaces are kept out. */
  onKeepOut?: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const layer = useRef(frontLayer());
  const [model, setModel] = useState(meaning.models.some(candidate => candidate.id === meaning.modelId) ? meaning.modelId : meaning.models[0]?.id ?? meaning.modelId);
  const chosen = meaning.models.find(candidate => candidate.id === model) ?? meaning.models[0];
  const estimate = meaningEstimate(model, chosen && !chosen.installed ? chosen.kilobytes : 0, chunks, meaning.runtimeInstalled);
  const time = durationParts(estimate.seconds);
  const duration = time.unit === "seconds" ? t("about {count} s", { count: time.value })
    : time.unit === "minutes" ? t("about {count} min", { count: time.value }) : t("about {hours} h {minutes} min", { hours: time.value, minutes: time.minutes });
  return <Dialog isOpen className="meaning-dialog" title={t("Search by meaning")} portalContainer={layer.current} onClose={onClose} canOutsideClickClose={false}>
    <DialogBody>
      <p className="meaning-dialog-lead">{t("Find past conversations, skills, tools and documentation from what they are about, not only from the words they contain.")}</p>
      <ul className="meaning-dialog-points">
        <li><AppIcon name="search" size={15} /><span>{t("Search (Ctrl+P): “where did we fix the scroll jump?” finds the conversation, whatever its wording or its language.")}</span></li>
        <li><AppIcon name="assistant" size={15} /><span>{t("Agents: alta search brings back earlier work and finds the right skill or MCP tool among many.")}</span></li>
        <li><AppIcon name="shield" size={15} /><span>{t("Private: the model runs on this computer. Nothing is sent.")}</span></li>
      </ul>
      <h3 className="meaning-dialog-heading">{t("What it takes")}</h3>
      <dl className="meaning-dialog-costs">
        <dt>{t("Download")}</dt>
        <dd>{estimate.downloadKilobytes > 0 ? t("{model} · {size}, once", { model: chosen?.name ?? model, size: kilobytesText(estimate.downloadKilobytes, locale) }) : t("Nothing: the model is already there.")}</dd>
        <dt>{t("Disk")}</dt>
        <dd>{t("about {size} of index for your {count} sessions", { size: kilobytesText(Math.max(estimate.diskKilobytes, 1), locale), count: meaning.sessions })}</dd>
        <dt>{t("Time")}</dt>
        <dd>{t("{duration} of indexing in the background; it pauses while you dictate", { duration })}</dd>
      </dl>
      <details className="meaning-dialog-details">
        <summary>{t("Details")}</summary>
        <RadioGroup label={t("Model")} selectedValue={model} onChange={event => setModel(event.currentTarget.value)}>
          {meaning.models.map(candidate => <Radio key={candidate.id} value={candidate.id}
            labelElement={<span className="meaning-dialog-model"><strong>{candidate.name}</strong> · {candidate.author} · {candidate.license} · {kilobytesText(candidate.kilobytes, locale)}
              <span className="meaning-dialog-model-summary">{candidate.summary}</span></span>} />)}
        </RadioGroup>
        <p>{t("The model comes from Hugging Face at a pinned commit and the ONNX Runtime from nuget.org, both checked by SHA-256. The model goes to ~/.alta/cache/search/models, the index to the data folder of CodeAlta.")}</p>
        <p>{t("Indexed: the prompt and the final answer of each turn, session titles and notes, plans and tasks, skills, tools, MCP servers, plugins and the user guide. Never indexed: steering messages, the messages between tool calls, tool outputs, reasoning, and the settings of MCP servers.")}</p>
        <p>{t("Keyword search already works without the model.")} {onKeepOut && <Button variant="minimal" size="small" intent="primary" onClick={onKeepOut}>{t("Keep a project or a space out of search…")}</Button>}</p>
      </details>
      <p className="meaning-dialog-note">{t("You can turn it off at any time in Settings › Search.")}</p>
    </DialogBody>
    <DialogFooter actions={<>
      {offered ? <>
        <Button onClick={onNever}>{t("Don't ask again")}</Button>
        <Button onClick={onLater}>{t("Not now")}</Button>
      </> : <Button onClick={onClose}>{t("Cancel")}</Button>}
      <Button intent="primary" autoFocus disabled={!meaning.supported} onClick={() => onTurnOn(model)}>{t("Turn on")}</Button>
    </>} />
  </Dialog>;
}

/**
 * The dialog over the local search of the host: what it answers is sent there. Offered, the answers to the offer are
 * kept with the settings of the user.
 */
export function MeaningDialogHost({ offered, onClose, onKeepOut, onTurnedOn }: {
  offered: boolean;
  onClose: () => void;
  onKeepOut?: () => void;
  /** Called once search by meaning was turned on: the window follows its progress. */
  onTurnedOn?: () => void;
}) {
  const { hub, state } = useLocalSearch();
  const meaning = state.status?.semantic;
  if (!hub || !meaning) return null;
  return <MeaningDialog meaning={meaning} chunks={state.status?.chunks ?? 0} offered={offered} onKeepOut={onKeepOut} onClose={onClose}
    onTurnOn={modelId => { onClose(); void hub.turnOnMeaning(modelId).then(status => { if (status === "ok") onTurnedOn?.(); }); }}
    onLater={() => { onClose(); void hub.answerOffer("later"); }}
    onNever={() => { onClose(); void hub.answerOffer("never"); }} />;
}
