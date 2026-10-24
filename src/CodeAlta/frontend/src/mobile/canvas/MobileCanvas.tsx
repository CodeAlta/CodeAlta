import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Spinner } from "@blueprintjs/core";
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
      {(item.fix || item.retry) && <div className="mobile-problem-actions">
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
        <p className="mobile-instruction">{t("Scan it with the camera of the phone. The phone must be signed in to the same tailnet.")}</p>
      </figcaption>
    </figure>}
    {view.waiting && <div className="mobile-waiting" role="status"><Spinner size={16} /><span>{t(view.waiting)}</span></div>}
    {view.problems.map(problem)}
    {failure && <Callout className="mobile-problem" intent="warning" title={t("The Mobile plugin does not answer.")}><p>{failure}</p></Callout>}
  </div>;
}
