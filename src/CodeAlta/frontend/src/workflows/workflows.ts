// What the Workflows tab computes from what the host says: the columns of the graph of a script, the fields of the
// form of Run…, the arguments they give, and the order of the runs.
import type { WorkflowRunItem, WorkflowScriptItem, WorkflowScriptParameter, WorkflowStepItem } from "#neoastra";

/** The steps of a script in columns: a column runs after the one before it, and the steps of a column side by side. */
export function stepColumns(steps: readonly WorkflowStepItem[]): readonly (readonly WorkflowStepItem[])[] {
  const level = new Map<string, number>();
  const columns: WorkflowStepItem[][] = [];
  steps.forEach((step, index) => {
    // No `after`: after the step declared before it; an empty list: at the start.
    const after = step.after ?? (index > 0 ? [steps[index - 1].id] : []);
    const at = after.reduce((most, id) => Math.max(most, (level.get(id) ?? -1) + 1), 0);
    level.set(step.id, at);
    (columns[at] ??= []).push(step);
  });
  return columns.filter(column => column !== undefined);
}

/** The scope of a script as the tab names it. */
export const scopeLabel = (scope: string) => scope === "project" ? "Project" : scope === "global" ? "Mine" : "Sample";

/**
 * The path of a script of a project in that project, as its code editor names its files (`.alta/scripts/triage.js`); null for
 * a script outside it (yours, the samples), which the editor of a project does not open.
 */
export function projectScriptPath(script: Readonly<{ scope: string; path: string }>, projectPath: string | null): string | null {
  if (script.scope !== "project" || !projectPath) return null;
  const file = script.path.replaceAll("\\", "/"), root = projectPath.replaceAll("\\", "/").replace(/\/+$/u, "") + "/";
  // Paths of Windows differ in case alone; the others keep theirs.
  const within = /^[a-z]:\//iu.test(root) ? file.toLowerCase().startsWith(root.toLowerCase()) : file.startsWith(root);
  return within ? file.slice(root.length) : null;
}

/** The scripts whose name, title or description has every word of a query. */
export function filterScripts(scripts: readonly WorkflowScriptItem[], query: string): readonly WorkflowScriptItem[] {
  const words = query.toLowerCase().split(/\s+/u).filter(Boolean);
  if (words.length === 0) return scripts;
  return scripts.filter(script => {
    const text = [script.name, script.title ?? "", script.description ?? ""].join(" ").toLowerCase();
    return words.every(word => text.includes(word));
  });
}

/** How a field of Run… edits a parameter: by its JSON type. */
export type FieldKind = "text" | "number" | "integer" | "boolean" | "json";
export type ScriptField = Readonly<{ name: string; kind: FieldKind; required: boolean; description: string | null }>;
/** The value of a field: text for every kind but a check box. */
export type FieldValue = string | boolean;

export function fieldKind(type: string | null): FieldKind {
  switch (type) {
    case "string": return "text";
    case "number": return "number";
    case "integer": return "integer";
    case "boolean": return "boolean";
    // An array, an object, or a value of any type is written as JSON.
    default: return "json";
  }
}

export const fieldsOf = (parameters: readonly WorkflowScriptParameter[]): readonly ScriptField[] =>
  parameters.map(parameter => ({ name: parameter.name, kind: fieldKind(parameter.type), required: parameter.required, description: parameter.description }));

/** The values the form starts with: the defaults of the parameters. */
export function initialValues(parameters: readonly WorkflowScriptParameter[]): Record<string, FieldValue> {
  const values: Record<string, FieldValue> = {};
  for (const parameter of parameters) {
    const kind = fieldKind(parameter.type);
    let value: unknown;
    try { value = parameter.default === null ? undefined : JSON.parse(parameter.default); } catch { value = undefined; }
    values[parameter.name] = kind === "boolean" ? value === true
      : value === undefined ? ""
      : kind === "json" ? JSON.stringify(value, null, 2)
      : typeof value === "string" ? value : String(value);
  }
  return values;
}

/** What is wrong with a field, as a message and its values. */
export type FieldProblem = Readonly<{ field: string; message: "Fill in {name}." | "{name} is a whole number." | "{name} is a number." | "{name} is not valid JSON.";
  values: Readonly<Record<string, string>> }>;

/**
 * The arguments of a run, a JSON object, from the values of the form. A field left empty is left out, unless it is
 * required; a check box always gives its value. The host checks them again against the parameters of the script.
 */
export function argumentsOf(fields: readonly ScriptField[], values: Readonly<Record<string, FieldValue>>): Readonly<{ json: string } | { problem: FieldProblem }> {
  const result: Record<string, unknown> = {};
  for (const field of fields) {
    const value = values[field.name];
    if (field.kind === "boolean") { result[field.name] = value === true; continue; }
    const text = typeof value === "string" ? value : "";
    if (text.trim() === "") {
      if (field.required) return { problem: { field: field.name, message: "Fill in {name}.", values: { name: field.name } } };
      continue;
    }
    switch (field.kind) {
      case "text": result[field.name] = text; break;
      case "number": case "integer": {
        const number = Number(text.trim());
        if (!Number.isFinite(number) || (field.kind === "integer" && !Number.isInteger(number)))
          return { problem: { field: field.name, message: field.kind === "integer" ? "{name} is a whole number." : "{name} is a number.", values: { name: field.name } } };
        result[field.name] = number;
        break;
      }
      case "json":
        try { result[field.name] = JSON.parse(text); }
        catch { return { problem: { field: field.name, message: "{name} is not valid JSON.", values: { name: field.name } } }; }
        break;
    }
  }
  return { json: JSON.stringify(result) };
}

/** The runs as the tab lists them: the running ones first, then the newest. */
export function sortRuns(runs: readonly WorkflowRunItem[]): readonly WorkflowRunItem[] {
  return [...runs].sort((a, b) => Number(b.state === "running") - Number(a.state === "running") || Date.parse(b.startedAt) - Date.parse(a.startedAt));
}

/** The tone of a run, for its mark. */
export const runTone = (state: string) => state === "running" ? "running" : state === "completed" ? "ok" : state === "failed" ? "failed" : "muted";

export const runStateLabel = (state: string) => state === "running" ? "Running" : state === "completed" ? "Completed" : state === "failed" ? "Failed" : "Stopped";

/** How long a run ran, or has run so far. */
export function runDuration(run: WorkflowRunItem, now: number): number {
  const start = Date.parse(run.startedAt);
  const end = run.endedAt ? Date.parse(run.endedAt) : now;
  return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : 0;
}

/** How often the runs are read again while the tab shows: often while one runs, else now and then. */
export const runsRefreshMilliseconds = (runs: readonly WorkflowRunItem[]) => runs.some(run => run.state === "running") ? 2000 : 10_000;
