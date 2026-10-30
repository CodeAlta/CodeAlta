import type { MathLibrary } from "./mathLibrary";

export type { MathFormulas, MathLibrary } from "./mathLibrary";

// KaTeX is a chunk of its own, loaded the first time a text holds a formula, as Pele is for diagrams: who never writes
// math never loads it. What is kept here is the loaded module, not a state of a session.
let library: Promise<MathLibrary> | undefined;
let loaded: MathLibrary | undefined;

/** Loads KaTeX (with its chemistry and copy extensions) once; a failed load is tried again the next time. */
export function loadMath(): Promise<MathLibrary> {
  return library ??= import("./mathLibrary").then(module => loaded = module.createMathLibrary(window), error => {
    library = undefined;
    throw error;
  });
}

/** KaTeX once it is loaded: a render draws formulas at once with it; undefined before, when a render asks for it. */
export function loadedMath(): MathLibrary | undefined {
  return loaded;
}
