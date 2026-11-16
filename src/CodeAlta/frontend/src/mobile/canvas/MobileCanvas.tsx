import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Spinner, Switch } from "@blueprintjs/core";
import { AppIcon } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import { mobileView, type MobileProblem, type MobileStatus } from "./status";

/** What the tab asks of the window and of the plugin. */
export type MobileCanvasActions = Readonly<{
  /** Asks Tailscale again; the new status comes back through `status`. */
  retry(): Promise<void>;
  /** Writes a text to the clipboard: true when it was written. */
  copy(text: string): Promise<boolean>;
  /** Opens a page of the web in the browser of the computer. */
  openLink(url: string): void;
  /** Asks the plugin for its status again, after it did not answer. */
  reload(): void;
  /** Runs an action of the provider (a sign-in); its outcome comes through `status`. */
  act(action: string): Promise<void>;
  /** Asks the machine for a code that connects one phone (a node makes it when it is asked for); it comes through `status`. Absent where the code is always shown. */
  pair?(): Promise<void>;
  /** Changes the owner's opt-in for the machines this desktop reaches. */
  otherMachines?(enabled: boolean): Promise<void>;
}>;

/**
 * The Mobile tab: the QR code a phone scans to open its page, the address with a copy button and one line of instruction; and, only when
 * something keeps a phone from connecting, what it is with what fixes it.
 */
export function MobileCanvas({ status, failure, actions }: { status: MobileStatus | null; failure: string | null; actions: MobileCanvasActions }) {
  const { t } = useShellLanguage();
  const view = useMemo(() => status ? mobileView(status) : null, [status]);
  const [copied, setCopied] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [pairing, setPairing] = useState(false);
  const [sharing, setSharing] = useState(false);
  const copiedTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copiedTimer.current), []);

  const copy = async (text: string) => {
    if (await actions.copy(text)) {
      setCopied(true);
      clearTimeout(copiedTimer.current);
      copiedTimer.current = setTimeout(() => setCopied(false), 1500);
    }
  };
  const retry = async () => {
    setRetrying(true);
    try { await actions.retry(); } finally { setRetrying(false); }
  };

  const pair = async () => {
    setPairing(true);
    try { await actions.pair?.(); } finally { setPairing(false); }
  };

  if (!view) {
    return <div className="mobile-canvas">
      {failure
        ? <Callout className="mobile-problem" intent="danger" title={t("The Mobile plugin does not answer.")}>
          <p>{failure}</p>
          <div className="mobile-problem-actions"><Button size="small" icon={<AppIcon name="refresh" size={14} />} onClick={actions.reload}>{t("Try again")}</Button></div>
        </Callout>
        : <div className="mobile-waiting" role="status" aria-busy="true"><Spinner size={20} /></div>}
    </div>;
  }

  const problem = (item: MobileProblem) => {
    const detail = item.detail ?? (item.hint ? t(item.hint) : null);
    return <Callout key={item.key} className="mobile-problem" intent={item.intent} title={t(item.title, item.parameters)} data-problem={item.key}>
      {detail && <p>{detail}</p>}
      {(item.fix || item.retry || item.actions.length > 0) && <div className="mobile-problem-actions">
        {item.actions.map(action => <Button key={action.action} size="small" intent={action.primary ? "primary" : "none"} onClick={() => void actions.act(action.action)}>{"label" in action ? t(action.label) : action.text}</Button>)}
        {item.fix && <Button size="small" intent="primary" icon={<AppIcon name="openExternal" size={14} />} onClick={() => actions.openLink(item.fix!.url)}>{t(item.fix.label)}</Button>}
        {item.retry && <Button size="small" icon={<AppIcon name="refresh" size={14} />} loading={retrying} onClick={() => void retry()}>{t("Try again")}</Button>}
      </div>}
    </Callout>;
  };

  return <div className="mobile-canvas">
    {view.code && <figure className="mobile-code">
      <svg className="mobile-qr" viewBox={`0 0 ${view.code.size} ${view.code.size}`} role="img" aria-label={t("QR code of the link of the phone page")} shapeRendering="crispEdges">
        <rect width={view.code.size} height={view.code.size} fill="#fff" />
        <path d={view.code.path} fill="#000" />
      </svg>
      <figcaption>
        <div className="mobile-address">
          <code>{view.code.address}</code>
          <Button variant="minimal" size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={copied ? t("Copied") : t("Copy address")}
            title={copied ? t("Copied") : t("Copy address")} onClick={() => void copy(view.code!.address)} />
        </div>
        <p className="mobile-instruction">{t(view.instruction)}</p>
      </figcaption>
    </figure>}
    {status?.pairsOnce && status.served && <div className="mobile-pairing">
      <p className="mobile-instruction" data-phones={status.phones ?? 0}>
        {view.code && status.pairingSeconds ? t("The code connects one phone, within {minutes} min.", { minutes: Math.max(1, Math.ceil(status.pairingSeconds / 60)) })
          : t("A code connects one phone. Ask for one when a phone is ready.")}
        {" "}{status.phones ? t(status.phones === 1 ? "{count} phone is connected." : "{count} phones are connected.", { count: status.phones }) : t("No phone is connected yet.")}
      </p>
      {actions.pair && <Button size="small" intent={view.code ? "none" : "primary"} icon={<AppIcon name="refresh" size={14} />} loading={pairing} onClick={() => void pair()}>
        {view.code ? t("New code") : t("Show a code")}</Button>}
    </div>}
    {view.waiting && <div className="mobile-waiting" role="status"><Spinner size={16} /><span>{t(view.waiting)}</span></div>}
    {status?.hasOtherMachines && actions.otherMachines && <div className="mobile-sharing">
      <Switch label={t("Show my other machines on the phone")} checked={status.showOtherMachines === true} disabled={sharing}
        onChange={event => {
          setSharing(true);
          void actions.otherMachines!(event.currentTarget.checked).finally(() => setSharing(false));
        }} />
      <p className="settings-field-help">{t("Every connected phone can use the machines this computer reaches, with this computer's permissions.")}</p>
    </div>}
    {view.problems.map(problem)}
    {failure && <Callout className="mobile-problem" intent="warning" title={t("The Mobile plugin does not answer.")}><p>{failure}</p></Callout>}
  </div>;
}
