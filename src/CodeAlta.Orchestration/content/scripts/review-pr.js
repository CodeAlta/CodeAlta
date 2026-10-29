// Reviews a pull request from several angles at once, one session per angle, then merges their findings into one review.
export const meta = {
  name: "review-pr",
  title: "Review a pull request",
  description: "Reviews a pull request from several angles in parallel, then merges the findings into one review with a verdict.",
  whenToUse: "Before merging a pull request of the project's tracker (GitHub, GitLab, Azure DevOps, Bitbucket).",
  tool: true,
  params: {
    pr: { type: "integer", description: "The number of the pull request; read from `event` when left out.", optional: true },
    event: { type: "string", description: "What started the run: an automation on pull requests gives it, with the number of the pull request.", optional: true },
    lenses: { type: "array", items: { type: "string" }, description: "The angles of the review, one session each.", default: ["correctness", "security", "tests", "readability"] },
    tier: { type: "string", description: "The tier of the reviewing sessions: complex, standard or quick.", default: "standard" },
  },
  steps: [
    { id: "read", title: "Read the pull request" },
    { id: "review", title: "Review each angle", after: ["read"], each: true },
    { id: "merge", title: "Merge the findings", after: ["review"] },
  ],
};

const lenses = args.lenses ?? meta.params.lenses.default;
const tier = args.tier ?? meta.params.tier.default;

// An automation on pull requests says which one in `event`: "… pull request #128 was opened."
const number = args.pr ?? Number(/(?:pull|merge) request #(\d+)/.exec(args.event ?? "")?.[1]);
if (!Number.isInteger(number)) throw new Error("review-pr needs the number of a pull request: give `pr`, or run it from an automation on pull requests.");

const pr = await step("read", () => alta.issue.show({ id: String(number), kind: "pr" }));
const brief = [
  `Pull request #${pr.id}: ${pr.title}`,
  pr.url,
  `It merges ${pr.sourceBranch ?? "its branch"} into ${pr.targetBranch ?? "the default branch"}.`,
  "",
  pr.description ?? "(no description)",
].join("\n");

present(`## Review of #${pr.id}: ${pr.title}\nReviewing ${lenses.map((lens) => `**${lens}**`).join(", ")}, one session each.`);

// One session per angle, at the same time: a session that fails is null, and the merge says so.
const findings = await step("review", () => parallel(lenses.map((lens) => () =>
  agent(`Review this pull request for ${lens}, and only that. Read its changes with git (the diff of its branch against ${pr.targetBranch ?? "the default branch"}). ` +
    `List concrete findings, each with its file and line, the most serious first; answer "none" when there are none. Change nothing.\n\n${brief}`,
    { tier, title: `Review #${pr.id}: ${lens}` }))));

// The review is shown to the user; what returns is its verdict, for what acts on it.
const merged = await step("merge", () => agent(
  `Merge these reviews of pull request #${pr.id} into one review in Markdown: remove duplicates, keep the most serious findings first. ` +
  `Then give the verdict: approve, or changes requested.\n\n` +
  lenses.map((lens, i) => `## ${lens}\n${findings[i] ?? "(this review failed)"}`).join("\n\n"),
  { tier, title: `Review #${pr.id}`, schema: { type: "object", properties: { review: { type: "string" }, verdict: { type: "string", enum: ["approve", "changes requested"] } },
    required: ["review", "verdict"] } }));
present(`${merged.review}\n\n**Verdict:** ${merged.verdict}`);
return { pr: pr.id, url: pr.url, verdict: merged.verdict, failedLenses: lenses.filter((_, i) => findings[i] === null) };
