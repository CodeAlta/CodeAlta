import "monaco-editor/languages/definitions/ini/register.js";
import "monaco-editor/languages/definitions/markdown/register.js";
import { monaco } from "./monacoEnvironment";
import type { EditorLanguage } from "./fileLanguage";

// Monaco's JSON support is a language service with its own worker; highlighting only needs these tokens.
function registerJson() {
  if (monaco.languages.getLanguages().some(language => language.id === "json")) return;
  monaco.languages.register({ id: "json", extensions: [".json", ".jsonc"], aliases: ["JSON", "json"] });
  monaco.languages.setLanguageConfiguration("json", { comments: { lineComment: "//", blockComment: ["/*", "*/"] },
    brackets: [["{", "}"], ["[", "]"]], autoClosingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, { open: '"', close: '"', notIn: ["string"] }] });
  monaco.languages.setMonarchTokensProvider("json", { defaultToken: "", tokenPostfix: ".json", tokenizer: {
    root: [
      [/"(?:[^"\\]|\\.)*"(?=\s*:)/, "type.identifier"],
      [/"/, "string", "@string"],
      [/-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?/, "number"],
      [/\b(?:true|false|null)\b/, "keyword"],
      [/\/\/.*$/, "comment"],
      [/\/\*/, "comment", "@comment"],
      [/[{}[\]]/, "@brackets"],
      [/[,:]/, "delimiter"],
    ],
    string: [[/[^\\"]+/, "string"], [/\\./, "string.escape"], [/"/, "string", "@pop"]],
    comment: [[/[^/*]+/, "comment"], [/\*\//, "comment", "@pop"], [/[/*]/, "comment"]],
  } });
}

// Each registration is a small module of its own; the grammar behind it loads when a model first uses the language.
// INI (configuration) and Markdown (prompts) are part of the main bundle and registered above.
const loaders: Readonly<Record<Exclude<EditorLanguage, "plaintext" | "ini" | "markdown">, () => Promise<unknown>>> = {
  json: async () => registerJson(),
  typescript: () => import("monaco-editor/languages/definitions/typescript/register.js"),
  javascript: () => import("monaco-editor/languages/definitions/javascript/register.js"),
  csharp: () => import("monaco-editor/languages/definitions/csharp/register.js"),
  fsharp: () => import("monaco-editor/languages/definitions/fsharp/register.js"),
  vb: () => import("monaco-editor/languages/definitions/vb/register.js"),
  xml: () => import("monaco-editor/languages/definitions/xml/register.js"),
  html: () => import("monaco-editor/languages/definitions/html/register.js"),
  css: () => import("monaco-editor/languages/definitions/css/register.js"),
  scss: () => import("monaco-editor/languages/definitions/scss/register.js"),
  less: () => import("monaco-editor/languages/definitions/less/register.js"),
  yaml: () => import("monaco-editor/languages/definitions/yaml/register.js"),
  python: () => import("monaco-editor/languages/definitions/python/register.js"),
  rust: () => import("monaco-editor/languages/definitions/rust/register.js"),
  go: () => import("monaco-editor/languages/definitions/go/register.js"),
  java: () => import("monaco-editor/languages/definitions/java/register.js"),
  kotlin: () => import("monaco-editor/languages/definitions/kotlin/register.js"),
  scala: () => import("monaco-editor/languages/definitions/scala/register.js"),
  swift: () => import("monaco-editor/languages/definitions/swift/register.js"),
  cpp: () => import("monaco-editor/languages/definitions/cpp/register.js"),
  "objective-c": () => import("monaco-editor/languages/definitions/objective-c/register.js"),
  shell: () => import("monaco-editor/languages/definitions/shell/register.js"),
  powershell: () => import("monaco-editor/languages/definitions/powershell/register.js"),
  bat: () => import("monaco-editor/languages/definitions/bat/register.js"),
  sql: () => import("monaco-editor/languages/definitions/sql/register.js"),
  dockerfile: () => import("monaco-editor/languages/definitions/dockerfile/register.js"),
  ruby: () => import("monaco-editor/languages/definitions/ruby/register.js"),
  php: () => import("monaco-editor/languages/definitions/php/register.js"),
  lua: () => import("monaco-editor/languages/definitions/lua/register.js"),
  perl: () => import("monaco-editor/languages/definitions/perl/register.js"),
  r: () => import("monaco-editor/languages/definitions/r/register.js"),
  dart: () => import("monaco-editor/languages/definitions/dart/register.js"),
  razor: () => import("monaco-editor/languages/definitions/razor/register.js"),
  graphql: () => import("monaco-editor/languages/definitions/graphql/register.js"),
  protobuf: () => import("monaco-editor/languages/definitions/protobuf/register.js"),
  hcl: () => import("monaco-editor/languages/definitions/hcl/register.js"),
  bicep: () => import("monaco-editor/languages/definitions/bicep/register.js"),
};

/** Registers a language with Monaco the first time it is needed; false when its definition could not be loaded. */
export function ensureMonacoLanguage(language: EditorLanguage): Promise<boolean> {
  // A repeated import resolves to the module already evaluated, so asking again registers nothing twice.
  return language === "plaintext" || language === "ini" || language === "markdown" ? Promise.resolve(true)
    : loaders[language]().then(() => true, () => false);
}
