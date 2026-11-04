import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { fleetServer, type FleetServerView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import { bannerOf } from "./policies";

/** How often the window reads whether a stop holds: the state of the links, kept by the host, costs no call to a server. */
const pollMilliseconds = 10_000;

/**
 * The state of the kill switch in the title bar of the window, shown while a signed stop of a server holds: what it reaches and
 * why, and Settings > Fleet > Policies one click away. Nothing while the work of the fleet goes on.
 */
export function FleetStopIndicator({ epoch, onOpen, api = fleetServer }: { epoch: string | null; onOpen: () => void; api?: Pick<typeof fleetServer, "status"> }) {
  const { t } = useShellLanguage();
  const [servers, setServers] = useState<readonly FleetServerView[]>([]);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    const read = () => api.status({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (!abort.signal.aborted && value.status === "ok") setServers(value.servers);
    }, () => undefined);
    void read();
    const timer = setInterval(() => void read(), pollMilliseconds);
    return () => { abort.abort(); clearInterval(timer); };
  }, [api, epoch]);
  const banner = bannerOf(servers);
  const shown = !!epoch && !!banner;
  // The title bar leaves the indicator the room it takes, as it does for the switch of spaces.
  const button = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    const element = button.current;
    const shell = element?.closest<HTMLElement>(".ide-shell");
    if (!shown || !element || !shell) return;
    const measure = () => shell.style.setProperty("--fleet-stop-width", `${Math.ceil(element.getBoundingClientRect().width) + 4}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => { observer.disconnect(); shell.style.removeProperty("--fleet-stop-width"); };
  }, [shown]);
  if (!epoch || !banner) return null;
  const reason = banner.server.stop?.reason;
  const label = t(banner.label, banner.values);
  const title = reason ? `${label} — ${reason}` : label;
  return <Button size="small" intent="danger" ref={button} className="fleet-stop-indicator" icon={<AppIcon name="hand" size={14} />} text={label} title={title}
    aria-label={title} onClick={onOpen} />;
}
