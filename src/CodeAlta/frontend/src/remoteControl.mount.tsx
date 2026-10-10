// The Remote Control button of the composer in each of its states, beside a minimal button of the same bar.
import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { RemoteControlButton } from "./RemoteControlButton";
import type { RemoteControlOpenRequest, RemoteControlStatus } from "./remoteControl";
import { ShellLanguageContext } from "./shellLanguage";

const statuses: RemoteControlStatus[] = ["off", "connecting", "connected", "failed"];
const opened: string[] = [];
const remoteControl: Record<string, unknown> = { opened,
  setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } };
Object.assign(window, { remoteControl });
document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");

// A button the Actions menu of its session asks to open, as the window does it; mounted again, as a session panel is
// when the space changes or its tab is opened again.
function Asked() {
  const [request, setRequest] = useState<RemoteControlOpenRequest | null>(null);
  const [mount, setMount] = useState(0);
  useEffect(() => {
    remoteControl.ask = () => { const asked: RemoteControlOpenRequest = { done: () => setRequest(current => current === asked ? null : current) }; setRequest(asked); };
    remoteControl.remount = () => setMount(value => value + 1);
  }, []);
  return <span data-asked><RemoteControlButton key={mount} openRequest={request} onSet={async () => "ok"} onOpenLink={() => { }}
    state={{ status: "off", url: null, error: null }} /></span>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <div className="ide-shell">
      {statuses.map(status => <RemoteControlButton key={status} onSet={async () => "ok"} onOpenLink={url => opened.push(url)}
        state={{ status, url: status === "connected" ? "https://claude.ai/code/session_test" : null, error: null }} />)}
      <Button variant="minimal" icon={<AppIcon name="copy" size={16} />} aria-label="Neighbour" data-neighbour />
      {(["green", "yellow", "red"] as const).map(name => <span key={name} data-color={name} style={{ color: `var(--${name})` }}>{name}</span>)}
      <Asked />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
