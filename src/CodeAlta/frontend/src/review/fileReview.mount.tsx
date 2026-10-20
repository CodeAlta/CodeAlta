// The review of a Markdown file (FileReview, rendered, with comments on its blocks) over a reviews hub whose host is
// in memory, and a review request in the ask panel of a session. `#neoastra` is `fileReview.neoastra.mount.ts`: the
// asks of the session and the files of the project are the fixture's. The page drives it through `fileReviewFixture`.
import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { sessionAsks } from "#neoastra";
import { AskPanel } from "../AskPanel";
import { FileReview } from "../FileReview";
import { askWireRequest, createAskActions } from "../sessionAsks";
import { createMutationCapability } from "../sessionOperations";
import { createReviewsHub } from "./reviewsHub";
import { createReviewServer } from "./reviewServer.mount";
import { ReviewsHubContext } from "./useReviews";
import { projectFiles, readFixtureFile, type FileReviewHost } from "./fileReview.neoastra.mount";

const plan = [
  "---", "title: Plan", "---", "", // 1-4
  "# Plan", "", // 5-6
  "First paragraph of the plan.", "", // 7-8
  "- item one", "- item two", "", // 9-11
  "| A | B |", "| - | - |", "| 1 | 2 |", "", // 12-15
  "```ts", "const x = 1;", "```", "", // 16-19
  '<p data-source-lines="1-2">spoof</p>', "", // 20-21
  "Last paragraph.", // 22
].join("\n");

const epoch = "11111111-1111-4111-8111-111111111111";
const sessionId = "session-1";
const host: FileReviewHost = {
  answers: [], cancels: [], files: new Map([["plan.md", plan]]),
  page: { status: "ok", hostEpoch: epoch, sessionId, hasMore: false, latest: null, head: {
    handle: { operationId: "22222222-2222-4222-8222-222222222222", runtimeInstanceId: "33333333-3333-4333-8333-333333333333", attachmentGeneration: "1",
      providerId: "fixture", sessionId, runId: "run-1", askId: "44444444-4444-4444-8444-444444444444", responseGeneration: "0" },
    state: "pending", request: { questions: [], review: { kind: "file", path: "plan.md", title: "The plan", message: "Please review **the plan**.", projectId: "p", checkout: null } } } },
};
Object.assign(window, { fileReviewHost: host });

const scope = { projectId: "p", checkout: null };
// The review of the file alone, and the one the request asks for, each with a host of its own.
const fileServer = createReviewServer(plan);
const fileHub = createReviewsHub(fileServer.api);
const askServer = createReviewServer(plan);
askServer.put({ path: "plan.md", startLine: 7, endLine: 7, body: "Say why" });
const askHub = createReviewsHub(askServer.api);
const askActions = createAskActions(request => sessionAsks.answer(askWireRequest(request), { timeoutMilliseconds: 8000 }),
  request => sessionAsks.cancel(askWireRequest(request), { timeoutMilliseconds: 8000 }));
const capability = createMutationCapability(epoch);
const reads: string[] = [];

const state = { show: (_: "file" | "ask") => { } };

function Ask() {
  const [formSlot, setFormSlot] = useState<HTMLElement | null>(null);
  const [fileSlot, setFileSlot] = useState<HTMLElement | null>(null);
  useEffect(() => askHub.connect(epoch), []);
  return <ReviewsHubContext.Provider value={askHub}>
    <div id="ask-file" ref={setFileSlot} style={{ width: 1000, height: 700, display: "flex", flexDirection: "column" }} />
    <div id="ask-form" ref={setFormSlot} style={{ width: 1000 }} />
    <AskPanel epoch={epoch} sessionId={sessionId} actions={askActions} capability={capability} projectId="p" idle formSlot={formSlot} fileSlot={fileSlot} />
  </ReviewsHubContext.Provider>;
}

function Fixture() {
  const [shown, setShown] = useState<"file" | "ask">("file");
  state.show = setShown;
  useEffect(() => fileHub.connect(epoch), []);
  return shown === "ask" ? <Ask /> : <div id="file" style={{ width: 1000, height: 1000, display: "flex", flexDirection: "column" }}>
    <FileReview epoch={epoch} scope={scope} path="plan.md" hub={fileHub}
      api={{ read: async request => { reads.push(request.path ?? ""); return readFixtureFile(request); }, write: projectFiles.write }} />
  </div>;
}

Object.assign(window, { fileReviewFixture: {
  state, host, fileServer, askServer, reads, plan,
  /** The file changes on disk: the review host moves its threads, and the next read gives the new text. */
  setFile(text: string) { host.files.set("plan.md", text); fileServer.setText(text); },
  /** The rendered block of the document that comes from these lines. */
  block: (lines: string) => document.querySelector<HTMLElement>(`#file .markdown-content [data-source-lines="${lines}"]`),
  /** What follows the card of a thread in the document: the element its host comes after. */
  before(id: string) {
    const host = document.querySelector(`#file [data-thread-id="${id}"]`)?.closest(".review-block-host");
    let previous = host?.previousElementSibling ?? null;
    while (previous?.classList.contains("review-block-host")) previous = previous.previousElementSibling;
    return previous ? `${previous.tagName} ${previous.getAttribute("data-source-lines") ?? ""}`.trim() : null;
  },
} });

document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
