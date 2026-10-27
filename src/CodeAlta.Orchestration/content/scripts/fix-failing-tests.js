// Finds the failing tests, then fixes them one session per test, and reports what each session changed.
export const meta = {
  name: "fix-failing-tests",
  title: "Fix failing tests",
  description: "Runs the tests, then gives each failing test to a session of its own that fixes it, and reports what changed.",
  whenToUse: "When several tests fail independently of each other, such as after an upgrade.",
  params: {
    command: { type: "string", description: "The command that runs the tests.", default: "dotnet test" },
    at_once: { type: "integer", description: "How many tests are fixed at once. The sessions share the folder of the project: raise it only when the failures touch different files.", default: 1 },
    max: { type: "integer", description: "The most failing tests to fix in one run.", default: 10 },
  },
  steps: [
    { id: "find", title: "Find the failing tests" },
    { id: "fix", title: "Fix each test", after: ["find"], each: true },
    { id: "report", title: "Report", after: ["fix"] },
  ],
};

const command = args.command ?? meta.params.command.default;
const failing = await step("find", () => agent(
  `Run the tests with \`${command}\` and list the tests that fail, by their full name. Change nothing.`,
  { tier: "quick", title: "Find the failing tests", schema: { type: "array", items: { type: "string" } } }));
const chosen = failing.slice(0, args.max ?? meta.params.max.default);

const fixes = await step("fix", () => parallel(chosen.map((test) => () =>
  agent(`The test \`${test}\` fails (\`${command}\` runs the tests). Find why, and make it pass with the smallest right change: ` +
    `fix the code when the code is wrong, the test only when the test is wrong. Do not commit. Run the test again, then say what you changed and why.`,
    { title: `Fix ${test}` })), { limit: args.at_once ?? meta.params.at_once.default }));

return step("report", () => [
  `${chosen.length} of ${failing.length} failing tests given to a session.`,
  ...chosen.map((test, i) => `## ${test}\n${fixes[i] ?? "(the session failed)"}`),
].join("\n\n"));
