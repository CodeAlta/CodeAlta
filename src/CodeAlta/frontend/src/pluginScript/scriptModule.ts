import type { ComponentType } from "react";
import type { Alta } from "./alta";

/** What a script of a plugin turned out to be: a React component the window draws in its own tree, or a function that fills an element. */
export type ScriptModule =
  | Readonly<{ kind: "component"; component: ComponentType }>
  | Readonly<{ kind: "mount"; mount: (root: HTMLElement, alta: Alta) => unknown }>;

/** A script that failed: it did not load, it is not a module of either form, or it threw. `stage` says where. */
export class ScriptError extends Error {
  constructor(readonly stage: "load" | "shape" | "mount" | "render", message: string) {
    super(message);
    this.name = "ScriptError";
  }
}

/** Loads the module at a path of the application's origin (`/plugin/...`). */
export type ScriptLoader = (path: string) => Promise<unknown>;

/** The loader of the window: a dynamic import, which the policy of the page accepts for its own origin. */
export const importScript: ScriptLoader = path => import(/* @vite-ignore */ path);

/** Whether a path is one the host serves for scripts: `/plugin/<key>/<stamp>/<file>`, plain segments only, or a module of the application's own build, `/lib/app/<name>.js`. */
export function isScriptPath(path: string): boolean {
  if (/^\/lib\/app\/[a-z][a-z0-9-]{0,63}\.js$/u.test(path)) return true;
  return path.length <= 2048 && /^\/plugin\/[A-Za-z0-9_-]+\/[A-Za-z0-9_-]+(\/[^/\\?#:*"<>|\u0000-\u001f]+)+$/u.test(path)
    && !path.split("/").some(segment => segment === ".." || segment === ".");
}

/** The message of whatever a script threw. */
export function scriptErrorMessage(error: unknown): string {
  const text = error instanceof Error ? error.message : typeof error === "string" ? error : "The script failed.";
  return text.length > 600 ? `${text.slice(0, 600)}…` : text;
}

const exotic = (value: unknown): boolean => typeof value === "object" && value !== null && "$$typeof" in value;

/**
 * Reads what a module exports. A default export that is a component (a function, or a `memo` or `forwardRef` of one) is drawn by
 * the window; otherwise `mount(root, alta)` is called on the element of the fragment. A module with both is a component.
 *
 * @throws ScriptError When the module is neither.
 */
export function readScriptModule(namespace: unknown): ScriptModule {
  const exports = (namespace ?? {}) as Record<string, unknown>;
  const component = exports.default;
  if (typeof component === "function" || exotic(component)) return { kind: "component", component: component as ComponentType };
  if (typeof exports.mount === "function") return { kind: "mount", mount: exports.mount as (root: HTMLElement, alta: Alta) => unknown };
  throw new ScriptError("shape", "The script exports no default component and no mount function.");
}

/** Loads a script and reads its form. @throws ScriptError When it does not load or is neither form. */
export async function loadScriptModule(path: string, load: ScriptLoader = importScript): Promise<ScriptModule> {
  if (!isScriptPath(path)) throw new ScriptError("load", "The address of the script is not valid.");
  let namespace: unknown;
  try {
    namespace = await load(path);
  } catch (error) {
    throw new ScriptError("load", scriptErrorMessage(error));
  }

  return readScriptModule(namespace);
}
