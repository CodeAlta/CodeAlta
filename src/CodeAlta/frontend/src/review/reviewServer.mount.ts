// A reviews host in memory for the review fixtures: one file, whose comments are anchored on the text of their lines
// and follow that text when the file changes, and a watch that tells each change. It records what it is asked.
import type { ReviewThreadDto } from "#neoastra";
import type { ReviewsApi } from "./reviewsHub";

const split = (text: string) => text.split(/\r\n|\r|\n/u);

export function createReviewServer(initialText: string) {
  const threads: ReviewThreadDto[] = [];
  const asked: Record<string, unknown>[] = [];
  const waiting = new Set<() => void>();
  let text = initialText;
  let next = 1;
  let revision = 0;
  const tell = () => { revision++; const all = [...waiting]; waiting.clear(); for (const resolve of all) resolve(); };
  const anchorOf = (start: number, end: number) => start < 1 ? "" : split(text).slice(start - 1, end).join("\n");
  // Where the anchor of a thread is now: the same lines if they still hold it, else the nearest place that does.
  function follow(thread: ReviewThreadDto): ReviewThreadDto {
    if (thread.startLine === 0) return thread;
    const lines = split(text), anchor = split(thread.anchor), span = thread.endLine - thread.startLine;
    const at = (start: number) => anchor.every((line, index) => lines[start - 1 + index] === line);
    if (at(thread.startLine)) return thread;
    let found = -1;
    for (let start = 1; start + anchor.length - 1 <= lines.length; start++)
      if (at(start) && (found < 0 || Math.abs(start - thread.startLine) < Math.abs(found - thread.startLine))) found = start;
    return found < 0 ? { ...thread, outdated: true } : { ...thread, startLine: found, endLine: found + span, outdated: false };
  }
  const find = (id: string) => threads.findIndex(thread => thread.id === id);
  const api = {
    async list(request) {
      asked.push({ call: "list", path: request.path });
      return { status: "ok", threads: threads.filter(thread => request.path === null || thread.path === request.path), rounds: [], viewed: [], request: null, readOnly: null, message: null };
    },
    async add(request) {
      asked.push({ call: "add", path: request.path, startLine: request.startLine, endLine: request.endLine, body: request.body, suggestion: request.suggestion });
      const thread: ReviewThreadDto = { id: `c${next++}`, target: request.target, path: request.path, side: request.side, startLine: request.startLine, endLine: request.endLine,
        anchor: anchorOf(request.startLine, request.endLine), body: request.body ?? "", suggestion: request.suggestion, state: "draft", outdated: false, replies: [], round: null,
        createdAt: "2026-10-20T10:00:00Z", updatedAt: "2026-10-20T10:00:00Z" };
      threads.push(thread);
      tell();
      return { status: "ok", thread, message: null };
    },
    async update(request) {
      asked.push({ call: "update", id: request.id, body: request.body, suggestion: request.suggestion, clearSuggestion: request.clearSuggestion });
      const index = find(request.id);
      if (index < 0) return { status: "not_found", thread: null, message: null };
      threads[index] = { ...threads[index], body: request.body ?? threads[index].body, suggestion: request.clearSuggestion ? null : request.suggestion ?? threads[index].suggestion };
      tell();
      return { status: "ok", thread: threads[index], message: null };
    },
    async delete(request) {
      asked.push({ call: "delete", id: request.id });
      const index = find(request.id);
      if (index >= 0) threads.splice(index, 1);
      tell();
      return { status: index >= 0 ? "ok" : "not_found", message: null };
    },
    async resolve(request) {
      const index = find(request.id);
      threads[index] = { ...threads[index], state: request.resolved ? "resolved" : "open" };
      tell();
      return { status: "ok", thread: threads[index], message: null };
    },
    async resolveAddressed() { return { status: "ok", message: null }; },
    async reply() { return { status: "ok", thread: null, message: null }; },
    async viewed() { return { status: "ok", message: null }; },
    async submit(request) {
      asked.push({ call: "submit", verdict: request.verdict, overall: request.overall });
      return { status: "ok", sessionId: "s1", roundId: "r1", message: null };
    },
    async counts() {
      return { status: "ok", checkouts: [{ checkout: null, drafts: threads.filter(thread => thread.state === "draft").length, open: 0, addressed: 0, missing: false }] };
    },
    async watch(_request, options) {
      return (async function* () {
        yield { revision };
        while (!options?.signal?.aborted) {
          await new Promise<void>(resolve => { waiting.add(resolve); options?.signal?.addEventListener("abort", () => resolve(), { once: true }); });
          if (!options?.signal?.aborted) yield { revision };
        }
      })();
    },
  } as ReviewsApi;
  return {
    api, threads, asked,
    get text() { return text; },
    /** The file changed: the threads follow their text, and the watch tells it. */
    setText(value: string) {
      text = value;
      threads.splice(0, threads.length, ...threads.map(follow));
      tell();
    },
    /** A thread written elsewhere (an agent, another window), on lines of the file as it is. */
    put(thread: Partial<ReviewThreadDto> & Pick<ReviewThreadDto, "path" | "startLine" | "endLine">) {
      threads.push({ id: `c${next++}`, target: "file", side: "new", anchor: anchorOf(thread.startLine, thread.endLine), body: "", suggestion: null, state: "draft",
        outdated: false, replies: [], round: null, createdAt: "2026-10-20T10:00:00Z", updatedAt: "2026-10-20T10:00:00Z", ...thread });
      tell();
    },
  };
}

export type ReviewServer = ReturnType<typeof createReviewServer>;
