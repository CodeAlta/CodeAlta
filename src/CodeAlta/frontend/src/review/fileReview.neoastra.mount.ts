// The host of the file review fixture: the asks of one session and the files of one project, as the page sets them
// on `window.fileReviewHost`; every other service is the inert one of the browser demo.
import type { ProjectFileReadRequest, ProjectFileReadResponse, ProjectFileWriteRequest, ProjectFileWriteResponse, SessionAskActionRequest, SessionAskObservationRequest, SessionAsksPage,
  SessionAskResult } from "../../../obj/neoastra/neoastra";
export * from "../demo-api";

export type FileReviewHost = { page: SessionAsksPage; answers: SessionAskActionRequest[]; cancels: SessionAskActionRequest[]; files: Map<string, string> };
const host = () => (window as Window & { fileReviewHost?: FileReviewHost }).fileReviewHost!;
const admitted = (request: SessionAskActionRequest, status: string): SessionAskResult =>
  ({ status: "ok", hostEpoch: request.expectedHostEpoch, disposition: { actionId: request.action.actionId, handle: request.action.handle, status, runId: status === "admitted" ? "run-2" : null } });

export const sessionAsks = {
  list: async () => host().page,
  answer: async (request: SessionAskActionRequest) => { host().answers.push(request); return admitted(request, "admitted"); },
  cancel: async (request: SessionAskActionRequest) => { host().cancels.push(request); return admitted(request, "cancelled"); },
  observe: async (request: SessionAskObservationRequest): Promise<SessionAskResult> => ({ status: "ok", hostEpoch: request.expectedHostEpoch, disposition: null }),
};

/** Reads a file of the fixture's project; a file it does not have is not found. */
export async function readFixtureFile(request: ProjectFileReadRequest): Promise<ProjectFileReadResponse> {
  const content = host().files.get(request.path ?? "") ?? null;
  return { status: content === null ? "not_found" : "ok", content, revision: content === null ? null : `r${content.length}`, readOnly: false, encoding: "utf-8",
    length: content?.length ?? 0, path: request.path, stamp: null };
}

export const projectFiles = {
  read: readFixtureFile,
  write: async (request: ProjectFileWriteRequest): Promise<ProjectFileWriteResponse> => {
    host().files.set(request.path ?? "", request.content ?? "");
    return { status: "ok", revision: `r${request.content?.length ?? 0}`, path: request.path, stamp: null };
  },
};
