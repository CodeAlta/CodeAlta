import "monaco-editor/languages/definitions/ini/register.js";
import "monaco-editor/languages/definitions/markdown/register.js";
import { monaco } from "./monacoEnvironment";
import type { EditorLanguage } from "./fileLanguage";
import { ownGrammars } from "./monacoGrammars";

// A grammar of the app is registered with its tokenizer in one step, so a model can take the language at once.
function registerOwn(id: keyof typeof ownGrammars) {
  if (monaco.languages.getLanguages().some(language => language.id === id)) return;
  const grammar = ownGrammars[id];
  monaco.languages.register({ id, extensions: [...grammar.extensions], aliases: [...grammar.aliases] });
  monaco.languages.setLanguageConfiguration(id, grammar.configuration);
  monaco.languages.setMonarchTokensProvider(id, grammar.tokens);
}

// Every language Monaco ships is registered on demand: each registration is a small module of its own, and the
// grammar behind it loads when a model first uses the language. INI (configuration) and Markdown (prompts) are part
// of the main bundle and registered above.
const loaders: Readonly<Record<Exclude<EditorLanguage, "plaintext" | "ini" | "markdown">, () => Promise<unknown>>> = {
  json: async () => registerOwn("json"),
  toml: async () => registerOwn("toml"),
  makefile: async () => registerOwn("makefile"),
  diff: async () => registerOwn("diff"),
  abap: () => import("monaco-editor/languages/definitions/abap/register.js"),
  apex: () => import("monaco-editor/languages/definitions/apex/register.js"),
  azcli: () => import("monaco-editor/languages/definitions/azcli/register.js"),
  bat: () => import("monaco-editor/languages/definitions/bat/register.js"),
  bicep: () => import("monaco-editor/languages/definitions/bicep/register.js"),
  cameligo: () => import("monaco-editor/languages/definitions/cameligo/register.js"),
  clojure: () => import("monaco-editor/languages/definitions/clojure/register.js"),
  coffee: () => import("monaco-editor/languages/definitions/coffee/register.js"),
  cpp: () => import("monaco-editor/languages/definitions/cpp/register.js"),
  csharp: () => import("monaco-editor/languages/definitions/csharp/register.js"),
  csp: () => import("monaco-editor/languages/definitions/csp/register.js"),
  css: () => import("monaco-editor/languages/definitions/css/register.js"),
  cypher: () => import("monaco-editor/languages/definitions/cypher/register.js"),
  dart: () => import("monaco-editor/languages/definitions/dart/register.js"),
  dockerfile: () => import("monaco-editor/languages/definitions/dockerfile/register.js"),
  ecl: () => import("monaco-editor/languages/definitions/ecl/register.js"),
  elixir: () => import("monaco-editor/languages/definitions/elixir/register.js"),
  flow9: () => import("monaco-editor/languages/definitions/flow9/register.js"),
  freemarker2: () => import("monaco-editor/languages/definitions/freemarker2/register.js"),
  fsharp: () => import("monaco-editor/languages/definitions/fsharp/register.js"),
  go: () => import("monaco-editor/languages/definitions/go/register.js"),
  graphql: () => import("monaco-editor/languages/definitions/graphql/register.js"),
  handlebars: () => import("monaco-editor/languages/definitions/handlebars/register.js"),
  hcl: () => import("monaco-editor/languages/definitions/hcl/register.js"),
  html: () => import("monaco-editor/languages/definitions/html/register.js"),
  java: () => import("monaco-editor/languages/definitions/java/register.js"),
  javascript: () => import("monaco-editor/languages/definitions/javascript/register.js"),
  julia: () => import("monaco-editor/languages/definitions/julia/register.js"),
  kotlin: () => import("monaco-editor/languages/definitions/kotlin/register.js"),
  less: () => import("monaco-editor/languages/definitions/less/register.js"),
  lexon: () => import("monaco-editor/languages/definitions/lexon/register.js"),
  liquid: () => import("monaco-editor/languages/definitions/liquid/register.js"),
  lua: () => import("monaco-editor/languages/definitions/lua/register.js"),
  m3: () => import("monaco-editor/languages/definitions/m3/register.js"),
  mdx: () => import("monaco-editor/languages/definitions/mdx/register.js"),
  mips: () => import("monaco-editor/languages/definitions/mips/register.js"),
  msdax: () => import("monaco-editor/languages/definitions/msdax/register.js"),
  mysql: () => import("monaco-editor/languages/definitions/mysql/register.js"),
  "objective-c": () => import("monaco-editor/languages/definitions/objective-c/register.js"),
  pascal: () => import("monaco-editor/languages/definitions/pascal/register.js"),
  pascaligo: () => import("monaco-editor/languages/definitions/pascaligo/register.js"),
  perl: () => import("monaco-editor/languages/definitions/perl/register.js"),
  pgsql: () => import("monaco-editor/languages/definitions/pgsql/register.js"),
  php: () => import("monaco-editor/languages/definitions/php/register.js"),
  pla: () => import("monaco-editor/languages/definitions/pla/register.js"),
  postiats: () => import("monaco-editor/languages/definitions/postiats/register.js"),
  powerquery: () => import("monaco-editor/languages/definitions/powerquery/register.js"),
  powershell: () => import("monaco-editor/languages/definitions/powershell/register.js"),
  protobuf: () => import("monaco-editor/languages/definitions/protobuf/register.js"),
  pug: () => import("monaco-editor/languages/definitions/pug/register.js"),
  python: () => import("monaco-editor/languages/definitions/python/register.js"),
  qsharp: () => import("monaco-editor/languages/definitions/qsharp/register.js"),
  r: () => import("monaco-editor/languages/definitions/r/register.js"),
  razor: () => import("monaco-editor/languages/definitions/razor/register.js"),
  redis: () => import("monaco-editor/languages/definitions/redis/register.js"),
  redshift: () => import("monaco-editor/languages/definitions/redshift/register.js"),
  restructuredtext: () => import("monaco-editor/languages/definitions/restructuredtext/register.js"),
  ruby: () => import("monaco-editor/languages/definitions/ruby/register.js"),
  rust: () => import("monaco-editor/languages/definitions/rust/register.js"),
  sb: () => import("monaco-editor/languages/definitions/sb/register.js"),
  scala: () => import("monaco-editor/languages/definitions/scala/register.js"),
  scheme: () => import("monaco-editor/languages/definitions/scheme/register.js"),
  scss: () => import("monaco-editor/languages/definitions/scss/register.js"),
  shell: () => import("monaco-editor/languages/definitions/shell/register.js"),
  solidity: () => import("monaco-editor/languages/definitions/solidity/register.js"),
  sophia: () => import("monaco-editor/languages/definitions/sophia/register.js"),
  sparql: () => import("monaco-editor/languages/definitions/sparql/register.js"),
  sql: () => import("monaco-editor/languages/definitions/sql/register.js"),
  st: () => import("monaco-editor/languages/definitions/st/register.js"),
  swift: () => import("monaco-editor/languages/definitions/swift/register.js"),
  systemverilog: () => import("monaco-editor/languages/definitions/systemverilog/register.js"),
  tcl: () => import("monaco-editor/languages/definitions/tcl/register.js"),
  twig: () => import("monaco-editor/languages/definitions/twig/register.js"),
  typescript: () => import("monaco-editor/languages/definitions/typescript/register.js"),
  typespec: () => import("monaco-editor/languages/definitions/typespec/register.js"),
  vb: () => import("monaco-editor/languages/definitions/vb/register.js"),
  wgsl: () => import("monaco-editor/languages/definitions/wgsl/register.js"),
  xml: () => import("monaco-editor/languages/definitions/xml/register.js"),
  yaml: () => import("monaco-editor/languages/definitions/yaml/register.js"),
};

/** Registers a language with Monaco the first time it is needed; false when its definition could not be loaded. */
export function ensureMonacoLanguage(language: EditorLanguage): Promise<boolean> {
  // A repeated import resolves to the module already evaluated, so asking again registers nothing twice.
  return language === "plaintext" || language === "ini" || language === "markdown" ? Promise.resolve(true)
    : loaders[language]().then(() => true, () => false);
}
