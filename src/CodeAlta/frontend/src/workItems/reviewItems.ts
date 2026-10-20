// The reviews in flight as Work items lists them, beside the tasks and the plans: one row per review target of a
// checkout (the uncommitted changes, the branch, or a file such as a plan), at the stage that says who it waits for.
import type { ReviewOverviewDto, ReviewsOverviewProject } from "#neoastra";
import type { MessageKey } from "../localization";
import type { WorkItem, WorkStage } from "./workItems";

/** A review as a row of Work items: a work item of the kind `review`, with what the overview says of it. */
export type ReviewWorkItem = WorkItem & Readonly<{ kind: "review"; review: ReviewOverviewDto }>;

/** Whether a work item is a review. */
export const isReviewItem = (item: WorkItem): item is ReviewWorkItem => item.kind === "review";

/**
 * Who a review waits for. The user: a request of an agent, comments the agent addressed and the user resolves,
 * drafts not submitted, or a checkout that is gone. The agent: comments sent and not answered. Nobody: done.
 */
export function reviewStage(entry: ReviewOverviewDto): WorkStage {
  if (entry.missing || entry.request || entry.drafts > 0 || entry.addressed > 0) return "todo";
  return entry.open > 0 ? "running" : "closed";
}

/** The folder name of a checkout, or null for the project folder. */
export function checkoutName(checkout: string | null): string | null {
  if (!checkout) return null;
  const parts = checkout.replace(/\\/g, "/").replace(/\/+$/, "").split("/");
  return parts[parts.length - 1] || checkout;
}

/** The key of a review among the reviews of a project: its checkout, its target and its file. */
export const reviewItemId = (entry: Pick<ReviewOverviewDto, "checkout" | "target" | "path">) => `${entry.checkout ?? ""}|${entry.target}|${entry.path ?? ""}`;

/** What a review is about, as its row names it. */
export function reviewTitle(entry: Pick<ReviewOverviewDto, "target" | "path">, t: (key: MessageKey) => string): string {
  if (entry.target === "file" && entry.path) return entry.path.slice(entry.path.lastIndexOf("/") + 1);
  return t(entry.target === "branch" ? "The changes of the branch" : "Uncommitted changes");
}

/** The counts of a review in a few words: "2 drafts · 3 open · 1 addressed". */
export function reviewCountsText(entry: ReviewOverviewDto, t: (key: MessageKey, values?: Record<string, string | number>) => string): string {
  const parts: string[] = [];
  if (entry.drafts) parts.push(t(entry.drafts === 1 ? "{count} draft" : "{count} drafts", { count: entry.drafts }));
  if (entry.open) parts.push(t("{count} open", { count: entry.open }));
  if (entry.addressed) parts.push(t("{count} addressed", { count: entry.addressed }));
  if (!parts.length && entry.resolved) parts.push(t("{count} resolved", { count: entry.resolved }));
  return parts.join(" · ");
}

/** Every review of the listed projects as rows of Work items, the ones that wait for the user first. */
export function reviewItems(projects: readonly ReviewsOverviewProject[], t: (key: MessageKey, values?: Record<string, string | number>) => string): readonly ReviewWorkItem[] {
  return projects.flatMap(project => project.entries.map(entry => {
    const where = checkoutName(entry.checkout);
    const summary = [where ? `${t("Worktree")} ${where}` : t("Project folder"), reviewCountsText(entry, t)].filter(Boolean).join(" · ");
    return {
      id: reviewItemId(entry), kind: "review" as const, title: reviewTitle(entry, t), summary, category: null, status: reviewStage(entry), statusText: null,
      created: entry.updatedAt, file: entry.path ?? "", proposedBy: null, runner: entry.request?.sessionId ?? null, acknowledged: false, runsWith: null,
      projectId: project.projectId, stage: reviewStage(entry), review: entry,
    };
  }));
}

/** The reviews in flight of the plans of the project folder, by file: a plan row shows that it is being reviewed. */
export function plansUnderReview(items: readonly ReviewWorkItem[]): ReadonlySet<string> {
  return new Set(items.filter(item => item.review.target === "file" && !item.review.checkout && item.stage !== "closed")
    .map(item => `${item.projectId}\n${item.review.path}`));
}
