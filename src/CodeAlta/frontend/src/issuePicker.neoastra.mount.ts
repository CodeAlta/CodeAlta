// The host of the issue picker fixture: three open issues of a GitHub repository, a closed one for who asks, and a second
// page of two older issues once the test sets `window.issuePages`. Every other service is the inert one of the demo.
// The searches are recorded in `window.issueSearches`, and answered after `window.issueDelay` milliseconds when set.
export * from "./demo-api";

const issue = (number: number, updatedAt: string, open = true) => ({ kind: "issue", number, title: `Issue ${number}`, state: open ? "open" : "closed", open,
  url: `https://github.com/owner/repo/issues/${number}`, updatedAt });
const fixture = window as unknown as { issueSearches?: { includeClosed: boolean; cursor: string | null; query: string }[]; issueDelay?: number; issuePages?: boolean };
export const gitIssues = {
  search: async (request: { query: string; includeClosed: boolean; cursor: string | null }) => {
    (fixture.issueSearches ??= []).push({ includeClosed: request.includeClosed, cursor: request.cursor, query: request.query });
    if (fixture.issueDelay) await new Promise(resolve => setTimeout(resolve, fixture.issueDelay));
    const issues = request.cursor === "page-2" ? [issue(8, "2026-09-02T10:00:00Z"), issue(7, "2026-09-01T10:00:00Z")]
      : [...[1, 2, 3].map(number => issue(number, "2026-10-18T10:00:00Z")), ...(request.includeClosed ? [issue(4, "2026-10-17T10:00:00Z", false)] : [])];
    return { status: "ok", provider: "github", repository: "owner/repo", message: null, issues, nextCursor: fixture.issuePages && !request.cursor ? "page-2" : null };
  },
};
