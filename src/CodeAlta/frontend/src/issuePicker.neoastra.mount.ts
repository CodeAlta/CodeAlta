// The host of the issue picker fixture: three issues of a GitHub repository. Every other service is the inert one of the demo.
export * from "./demo-api";

const issues = [1, 2, 3].map(number => ({ kind: "issue", number, title: `Issue ${number}`, state: "Open", open: true,
  url: `https://github.com/owner/repo/issues/${number}`, updatedAt: "2026-10-18T10:00:00Z" }));
export const gitIssues = {
  search: async () => ({ status: "ok", provider: "github", repository: "owner/repo", message: null, issues }),
};
