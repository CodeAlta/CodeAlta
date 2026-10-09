/**
 * The prompt CodeAlta gives a session when one of its background jobs has ended: the facts of the job, then the
 * end of what its command wrote. The host records it as a user prompt whose first line names what it is.
 */
export type JobResult = Readonly<{
  jobId: string;
  /** What the job was for, when its session said it. */
  title: string | null;
  command: string | null;
  /** How the command ended, as the host words it: "succeeded (exit code 0) after 12 s". */
  result: string;
  /** Whether the command succeeded. */
  succeeded: boolean;
  /** What is shown of the prompt: the command and the end of its output, as Markdown. */
  body: string;
}>;

const marker = "[CodeAlta background job]";

/** Reads the prompt that tells the end of a background job, or returns null for any other text. */
export function parseJobResult(text: string | null | undefined): JobResult | null {
  if (!text?.startsWith(marker)) return null;
  const lines = text.split(/\r?\n/);
  const fields = new Map<string, string>();
  let index = 1;
  for (; index < lines.length && lines[index].trim() !== ""; index++) {
    const split = lines[index].indexOf(": ");
    // The sentence that says where the prompt comes from is not a field.
    if (split < 1 || split > 16) continue;
    fields.set(lines[index].slice(0, split), lines[index].slice(split + 2).trim());
  }
  const jobId = fields.get("Job");
  const result = fields.get("Result");
  if (!jobId || !result) return null;
  const command = fields.get("Command") ?? null;
  const output = lines.slice(index + 1).join("\n").trim();
  // The command in a fence longer than any run of backticks it has.
  const fence = "`".repeat(Math.max(3, ...[...(command ?? "").matchAll(/`+/g)].map(run => run[0].length + 1)));
  return { jobId, title: fields.get("Title") ?? null, command, result, succeeded: result.startsWith("succeeded"),
    body: [command ? `${fence}\n${command}\n${fence}` : null, output || null].filter(part => part !== null).join("\n\n") };
}
