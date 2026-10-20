import { useEffect, useMemo, useState, type ReactNode } from "react";
import { projectFiles } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { showToast } from "../appToaster";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import { ReviewBar } from "../review/ReviewBar";
import { threadCounts, threadsOnLines, type ReviewThread } from "../review/reviewThreads";
import type { ReviewSessionChoice } from "../review/reviewSessions";
import { reviewSubmitter, SubmitReviewButton } from "../review/SubmitReviewForm";
import { useMarkdownBlockComments } from "../review/useMarkdownBlockComments";
import { useReviewActions, useReviewList, useReviewsHub } from "../review/useReviews";

/**
 * A plan of Work items with the comments of its review: the whole file rendered (its front matter too, so that the
 * blocks are on the lines of the file), a **+** on each block, and the review bar with **Submit review ▾**. Null
 * shows nothing until the file is read; a file that cannot be read shows `fallback`.
 */
export function PlanReview({ epoch, projectId, path, sessions, onOpenSession, fallback, api = projectFiles }: {
  epoch: string;
  projectId: string;
  /** The plan, relative to the project folder. */
  path: string;
  /** The sessions the review can go to. */
  sessions: readonly ReviewSessionChoice[];
  onOpenSession: (sessionId: string) => void;
  /** What shows when the file cannot be read: the text Work items read. */
  fallback: ReactNode;
  api?: Pick<typeof projectFiles, "read">;
}) {
  const { t } = useShellLanguage();
  const hub = useReviewsHub();
  const scope = useMemo(() => ({ projectId, checkout: null }), [projectId]);
  const target = useMemo(() => ({ kind: "file" as const, path }), [path]);
  const list = useReviewList(hub, scope, target);
  const actions = useReviewActions(hub, scope, !!list.readOnly);
  const [text, setText] = useState<Readonly<{ path: string; content: string | null }> | null>(null);
  const [container, setContainer] = useState<HTMLDivElement | null>(null);
  // Read again when the review changes: the agent revises the plan in reply to it.
  const changed = list.threads.map(thread => `${thread.id}:${thread.updatedAt}`).join() + list.rounds.length;
  useEffect(() => {
    const controller = new AbortController();
    void api.read({ expectedEpoch: epoch, projectId, path, reload: false }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!controller.signal.aborted) setText({ path, content: value.status === "ok" ? value.content : null }); },
        () => { if (!controller.signal.aborted) setText({ path, content: null }); });
    return () => controller.abort();
  }, [api, epoch, projectId, path, changed]);
  const content = text?.path === path ? text.content : null;
  const drawn = useMemo(() => threadsOnLines(list.threads, "new", path), [list.threads, path]);
  async function add(lines: Readonly<{ startLine: number; endLine: number }>, body: string, suggestion: string | null) {
    if (!hub) return false;
    const done = await hub.add(scope, target, { side: "new", startLine: lines.startLine, endLine: lines.endLine, body, suggestion });
    if (!done.ok && done.message) showToast({ message: done.message, intent: "warning", icon: "warning-sign", timeout: 6000 });
    return done.ok;
  }
  const blocks = useMarkdownBlockComments({ container: content !== null ? container : null, source: content ?? "", path, threads: drawn, actions,
    onAdd: hub ? add : null, blocked: list.readOnly });
  if (!hub) return <>{fallback}</>;
  if (text?.path !== path) return <p className="work-text-state"><ActivitySpinner size={16} /></p>;
  if (content === null) return <>{fallback}</>;
  const reveal = (thread: ReviewThread) => { if (!blocks.reveal(thread.id)) showToast({ message: t("This comment is not on a line of the file."), timeout: 4000 }); };
  return <div className="work-plan-review">
    <ReviewBar projectId={projectId} title={<span className="review-bar-file" title={path}>{path.slice(path.lastIndexOf("/") + 1)}</span>} threads={list.threads} readOnly={list.readOnly}
      onReveal={reveal} onResolveAddressed={async () => (await hub.resolveAddressed(scope, target)).ok}
      submit={<SubmitReviewButton disabled={!!list.readOnly} comments={threadCounts(list.threads)} sessions={sessions} plan
        onSubmit={reviewSubmitter(hub, scope, target, t, onOpenSession)} />} />
    <div className="work-text review-document" ref={setContainer} tabIndex={0}>
      {blocks.gutter}
      <MarkdownContent source={content} document sourceLines />
    </div>
    {blocks.portals}
  </div>;
}
