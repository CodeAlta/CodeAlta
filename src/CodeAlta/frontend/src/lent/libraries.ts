/** One library the application lends to the modules of plugins: its bare name, the file that holds it, and where the build emits it. */
export type LentLibrary = Readonly<{
  /** The name a module imports, as the import map of `index.html` knows it. */
  name: string;
  /** The entry file, relative to the frontend folder. */
  entry: string;
  /** The file in the build output; the address of the module is `app://codealta/<file>`. */
  file: string;
}>;

/**
 * The libraries the application lends to the modules of plugins. Each is an entry of the build of its own, at a fixed
 * address and sharing its code with the application, so a module that imports `react` or `@blueprintjs/core` gets the
 * very instance the application runs. `codealta` is the small module the application adds (see `codealta.ts`).
 */
export const lentLibraries: readonly LentLibrary[] = [
  { name: "react", entry: "src/lent/react.ts", file: "lib/react.js" },
  { name: "react/jsx-runtime", entry: "src/lent/react-jsx-runtime.ts", file: "lib/react-jsx-runtime.js" },
  { name: "react-dom", entry: "src/lent/react-dom.ts", file: "lib/react-dom.js" },
  { name: "react-dom/client", entry: "src/lent/react-dom-client.ts", file: "lib/react-dom-client.js" },
  { name: "@blueprintjs/core", entry: "src/lent/blueprint-core.ts", file: "lib/blueprintjs-core.js" },
  { name: "@blueprintjs/table", entry: "src/lent/blueprint-table.ts", file: "lib/blueprintjs-table.js" },
  { name: "flexlayout-react", entry: "src/lent/flexlayout.ts", file: "lib/flexlayout-react.js" },
  { name: "lucide-react", entry: "src/lent/lucide.ts", file: "lib/lucide-react.js" },
  { name: "codealta", entry: "src/lent/codealta.ts", file: "lib/codealta.js" },
];

/**
 * The text of the import map of `index.html`. It is inline, so the content security policy of the page names its SHA-256
 * (`assets.csp` of `neoastra.json`): a change here is a change of both, which `lent.test.ts` checks.
 */
export function importMapText(): string {
  return JSON.stringify({ imports: Object.fromEntries(lentLibraries.map(library => [library.name, `./${library.file}`])) });
}
