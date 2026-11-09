import { StrictMode, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { MarkdownContent, MarkdownLinksContext } from "./MarkdownContent";
import { SessionLinkHostContext, type SessionLinkHost, type SessionLinkInfo, type SessionLinkTarget } from "./sessionLinks";

// A message that names a running session (at one of its messages), an idle one of the chats, an archived one and one the window
// does not know; a raw link of another scheme stays no link. A second text has inert links (the preview of a folded row).
const source = "- Running: [Parser review](session:s-run#120)\n- Idle: [Notes](session:s-idle)\n- Archived: [Old](session:s-old)\n"
  + "- Unknown: [Gone](session:s-gone)\n- Raw: <a href=\"sessions:s-run\">raw</a>";
const state = { opened: [] as SessionLinkTarget[], links: [] as string[], copies: [] as string[], stop: () => { } };
Object.assign(window, { sessionLinkFixture: state });
Object.defineProperty(navigator, "clipboard", { value: { writeText: async (text: string) => { state.copies.push(text); } } });

function Message() {
  const [running, setRunning] = useState(true);
  state.stop = () => setRunning(false);
  const host = useMemo<SessionLinkHost>(() => {
    const sessions = new Map<string, SessionLinkInfo>([
      ["s-run", { title: "Parser review", scope: "CodeAlta", state: running ? "running" : "idle", updatedAt: "2026-11-09T10:00:00Z" }],
      ["s-idle", { title: "Notes", scope: "Chats", state: "idle", updatedAt: "2026-11-08T10:00:00Z" }],
      ["s-old", { title: "Old", scope: null, state: "archived", updatedAt: null }]]);
    return { describe: id => sessions.get(id) ?? null, open: target => { state.opened.push(target); } };
  }, [running]);
  return <div style={{ width: 640, padding: 24 }}><MarkdownLinksContext.Provider value={address => { state.links.push(address); }}>
    <SessionLinkHostContext.Provider value={host}>
      <div id="live"><MarkdownContent source={source} /></div>
      <div id="inert"><MarkdownContent source={source} onOpenLink={null} /></div>
    </SessionLinkHostContext.Provider></MarkdownLinksContext.Provider></div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Message /></StrictMode>);
