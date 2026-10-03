// Disposable fake transport; production panel, context and operation owner.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceDeleteSessionRequest, WorkspaceDeleteSessionResponse } from "#neoastra";
import { ShellLanguageContext } from "./shellLanguage";
import { translate, type Locale, type MessageKey } from "./localization";
import { SessionBatchDeletePanel } from "./SessionBatchDeletePanel";
import { createSessionBatchDeletion } from "./sessionBatchDeletion";
import { createMutationCapability } from "./sessionOperations";

function transport<Request, Response>() {
  const calls: Request[] = [];
  let resolve!: (value: Response) => void;
  let reject!: (reason: Error) => void;
  return { calls, invoke: (request: Request) => { calls.push(request); return new Promise<Response>((done, fail) => { resolve = done; reject = fail; }); },
    resolve: (value: Response) => resolve(value), reject: () => reject(new Error("Disposable transport failure")) };
}
function createFixture() {
  const epoch = "11111111-1111-4111-8111-111111111111";
  const capability = createMutationCapability(epoch);
  const deletion = transport<WorkspaceDeleteSessionRequest, WorkspaceDeleteSessionResponse>();
  const batch = createSessionBatchDeletion(deletion.invoke);
  const candidates: WorkspaceDeleteSessionRequest[] = ["Unknown", "Description", "Settings"].map(sessionId => ({ expectedHostEpoch: epoch, scope: "global", projectId: null,
    projectPath: "C:/Disposable/Settings", sessionId, confirmedTitle: sessionId }));
  return { epoch, capability, deletion, batch, candidates,
    deleteReply: (status: string) => { const request = deletion.calls.at(-1)!; deletion.resolve({ hostEpoch: epoch, scope: request.scope, projectId: request.projectId,
      projectPath: request.projectPath, sessionId: request.sessionId, status }); },
    counts: () => [deletion.calls.length],
  };
}
function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [model] = useState(createFixture);
  Object.assign(window, { management: { ...model, setLocale,
    text: (key: MessageKey) => translate(locale, key),
    click: (key: MessageKey) => { const button = Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find(value => value.textContent === translate(locale, key));
      if (!button) throw new Error(`Missing button ${key}`); button.click(); },
    input: (selector: string, value: string) => { const element = document.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!;
      const prototype = element.tagName === "SELECT" ? HTMLSelectElement.prototype : element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(prototype, "value")!.set!.call(element, value);
      element.dispatchEvent(new Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true })); },
  } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <main style={{ height: "100dvh", overflow: "auto", padding: 12 }}>
      <SessionBatchDeletePanel controls={{ owner: model.batch, epoch: model.epoch, canReview: model.capability.canMutate(),
        review: requests => model.batch.review(requests, () => true, model.capability) }} candidates={model.candidates} inputKey="original-input-generation" />
    </main>
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<Fixture />);
