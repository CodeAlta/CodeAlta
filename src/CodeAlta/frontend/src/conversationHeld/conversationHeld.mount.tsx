// The card of a run whose Claude Code conversation another process holds, with a transport the test answers: the
// question, its answer, then the wait with Stop.
import { useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type { ConversationHeldEntry, ConversationHeldListRequest, ConversationHeldPage, ConversationHeldResolveRequest, ConversationHeldResult } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import { ConversationHeldPanel } from "./ConversationHeldPanel";
import { createConversationHeldReviewer } from "./conversationHeld";

const epoch = "11111111-1111-4111-8111-111111111111";
function transport<Request, Response>() {
  const calls: { request: Request; resolve: (value: Response) => void; reject: (error: Error) => void }[] = [];
  return { calls, invoke: (request: Request) => new Promise<Response>((resolve, reject) => calls.push({ request, resolve, reject })) };
}
const lists = transport<ConversationHeldListRequest, ConversationHeldPage>();
const answers = transport<ConversationHeldResolveRequest, ConversationHeldResult>();
const reviewer = createConversationHeldReviewer(lists.invoke, answers.invoke);
const entry = (origin: string, originText: string, choices: string[], attempt = "22222222-2222-4222-8222-222222222222"): ConversationHeldEntry => ({
  handle: { runtimeInstanceId: "33333333-3333-4333-8333-333333333333", attachmentGeneration: "1", sessionId: "session", runId: "run",
    requestId: "request", attemptId: attempt },
  providerId: "claude-code", timestamp: "2026-10-14T00:00:00Z", conversationId: "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11",
  holder: { origin, originText, processId: 48212, entrypoint: null, kind: null, status: "idle",
    workingDirectory: "C:\\dev\\a-project-with-a-rather-long-name\\and\\a\\deep\\folder", name: null },
  choices,
});
const page = (entries: ConversationHeldEntry[]): ConversationHeldPage => ({ status: "ok", hostEpoch: epoch, sessionId: "session", entries });
const stops: number[] = [];

function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [running, run] = useState(true);
  const [wait, setWait] = useState<{ originText: string } | null>(null);
  useLayoutEffect(() => {
    Object.assign(window, { held: { language, run, setWait, lists: lists.calls, answers: answers.calls, stops, epoch, page, entry,
      setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => { } }}>
    <div className="ide-shell"><div className="timeline-notices">
      <ConversationHeldPanel reviewer={reviewer} epoch={epoch} sessionId="session" running={running} wait={wait} canReview={() => true}
        onStop={() => stops.push(Date.now())} />
    </div></div>
  </ShellLanguageContext>;
}

document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
