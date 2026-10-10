import { useEffect, useState } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import type { RemoteControlOpenRequest, RemoteControlView } from "./remoteControl";
import { RemoteLinkQr } from "./RemoteLinkQr";
import { useShellLanguage } from "./shellLanguage";

/**
 * The Remote Control of a session in the bar of its composer: its icon says where it stands, and it opens what it
 * has, the link of the session on claude.ai, and what turns it on or off. It is shown for a provider that has it.
 */
export function RemoteControlButton({ state, disabled = false, onSet, onOpenLink, openRequest = null }: {
  state: RemoteControlView;
  disabled?: boolean;
  /** Turns it on or off; settles with <c>ok</c>, or why the host refused. */
  onSet: (enabled: boolean) => Promise<string>;
  /** Opens the link in the browser of the system. */
  onOpenLink: (url: string) => void;
  /** The request of the Actions menu of the session to show it, until it is shown. */
  openRequest?: RemoteControlOpenRequest | null;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => { if (openRequest !== null) { setOpen(true); openRequest.done(); } }, [openRequest]);
  useEffect(() => { if (!copied) return; const timer = setTimeout(() => setCopied(false), 1400); return () => clearTimeout(timer); }, [copied]);
  const set = (enabled: boolean) => {
    setPending(true); setRefusal(null);
    void onSet(enabled).then(status => { if (status !== "ok") setRefusal(status); }, () => setRefusal("failed")).finally(() => setPending(false));
  };
  const label = t(state.status === "connected" ? "Remote Control: on" : state.status === "connecting" ? "Remote Control: connecting"
    : state.status === "failed" ? "Remote Control: failed" : "Remote Control: off");
  const refused = refusal === null ? null : t(refusal === "busy" ? "The session is busy: its provider is being changed. Try again in a moment."
    : refusal === "unavailable" ? "Remote Control is not available for this session." : "The request did not reach CodeAlta. Try again.");
  const content = <div className="remote-control" role="group" aria-label={t("Remote Control")}>
    <h6>{t("Remote Control")}</h6>
    {state.status === "off"
      ? <p className="detail">{t("Follow and drive this session from claude.ai or the Claude app, where it is listed under its title. Permission requests can be answered there or here.")}</p>
      : state.status === "connecting" ? <p role="status">{t("Connecting to claude.ai…")}</p>
      : state.status === "failed" ? <p role="alert" className="remote-control-error">{state.error ?? t("Claude Code could not connect this session.")}</p>
      : null}
    {state.url && <p className="remote-control-link"><code data-remote-control-url>{state.url}</code></p>}
    {state.url && state.status === "connected" && <RemoteLinkQr url={state.url} label={t("QR code of the link, to open the session on a phone")} />}
    {refused && <p role="alert" className="remote-control-error">{refused}</p>}
    <div className="remote-control-actions">
      {state.url && state.status !== "off" && <>
        <Button size="small" icon={<AppIcon name="openExternal" size={14} />} data-remote-control-open
          onClick={() => {
            // The user leaves for the browser: the popover does not keep the focus of the window, which a permission
            // request that appears meanwhile takes only from nowhere or from the composer.
            setOpen(false);
            onOpenLink(state.url!);
          }}>{t("Open in browser")}</Button>
        <Button size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} data-remote-control-copy
          onClick={() => void navigator.clipboard.writeText(state.url!).then(() => setCopied(true), () => { /* No clipboard: nothing is copied. */ })}>{t(copied ? "Copied" : "Copy link")}</Button>
      </>}
      {state.status === "off" || state.status === "failed"
        ? <Button size="small" intent="primary" loading={pending} disabled={disabled} data-remote-control-on onClick={() => set(true)}>{t(state.status === "failed" ? "Try again" : "Turn on")}</Button>
        : null}
      {state.status !== "off" && <Button size="small" loading={pending} disabled={disabled} data-remote-control-off onClick={() => set(false)}>{t("Turn off")}</Button>}
    </div>
    {state.status !== "off" && <p className="detail">{t("It stays on while CodeAlta runs.")}</p>}
  </div>;
  return <PopoverNext content={content} placement="top-end" isOpen={open} onInteraction={next => setOpen(next)} popoverClassName="remote-control-popover">
    <Button variant="minimal" icon={<AppIcon name="remote" size={16} />} data-remote-control={state.status} aria-label={label} title={label} />
  </PopoverNext>;
}
