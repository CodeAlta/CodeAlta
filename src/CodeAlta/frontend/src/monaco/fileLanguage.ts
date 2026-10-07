/** Monaco language ids the source editor can highlight; anything else is edited as plain text. */
export const editorLanguages = [
  "plaintext", "ini", "markdown", "json", "toml", "makefile", "diff", "abap", "apex", "azcli", "bat", "bicep", "cameligo", "clojure", "coffee",
  "cpp", "csharp", "csp", "css", "cypher", "dart", "dockerfile", "ecl", "elixir", "flow9", "freemarker2", "fsharp", "go", "graphql", "handlebars",
  "hcl", "html", "java", "javascript", "julia", "kotlin", "less", "lexon", "liquid", "lua", "m3", "mdx", "mips", "msdax", "mysql", "objective-c",
  "pascal", "pascaligo", "perl", "pgsql", "php", "pla", "postiats", "powerquery", "powershell", "protobuf", "pug", "python", "qsharp", "r", "razor",
  "redis", "redshift", "restructuredtext", "ruby", "rust", "sb", "scala", "scheme", "scss", "shell", "solidity", "sophia", "sparql", "sql", "st",
  "swift", "systemverilog", "tcl", "twig", "typescript", "typespec", "vb", "wgsl", "xml", "yaml",
] as const;
export type EditorLanguage = typeof editorLanguages[number];

const byExtension: Readonly<Record<string, EditorLanguage>> = {
  txt: "plaintext", log: "plaintext",
  ini: "ini", cfg: "ini", conf: "ini", properties: "ini", editorconfig: "ini", gitconfig: "ini", env: "ini",
  toml: "toml", md: "markdown", markdown: "markdown", mdx: "mdx", rst: "restructuredtext",
  json: "json", jsonc: "json", json5: "json", map: "json", webmanifest: "json",
  ts: "typescript", tsx: "typescript", mts: "typescript", cts: "typescript",
  js: "javascript", jsx: "javascript", mjs: "javascript", cjs: "javascript", coffee: "coffee",
  cs: "csharp", csx: "csharp", fs: "fsharp", fsx: "fsharp", fsi: "fsharp", vb: "vb", qs: "qsharp",
  xml: "xml", csproj: "xml", fsproj: "xml", vbproj: "xml", props: "xml", targets: "xml", slnx: "xml", xaml: "xml", axaml: "xml",
  resx: "xml", nuspec: "xml", config: "xml", svg: "xml", xsd: "xml", xslt: "xml", plist: "xml",
  html: "html", htm: "html", vue: "html", svelte: "html", css: "css", scss: "scss", less: "less",
  hbs: "handlebars", handlebars: "handlebars", liquid: "liquid", twig: "twig", pug: "pug", jade: "pug", ftl: "freemarker2",
  yml: "yaml", yaml: "yaml",
  py: "python", pyi: "python", rs: "rust", go: "go", java: "java", kt: "kotlin", kts: "kotlin", scala: "scala", swift: "swift",
  c: "cpp", h: "cpp", cpp: "cpp", hpp: "cpp", cc: "cpp", hh: "cpp", cxx: "cpp", hxx: "cpp", m: "objective-c", mm: "objective-c",
  sh: "shell", bash: "shell", zsh: "shell", ps1: "powershell", psm1: "powershell", psd1: "powershell", bat: "bat", cmd: "bat",
  sql: "sql", pgsql: "pgsql", rb: "ruby", php: "php", lua: "lua", pl: "perl", pm: "perl", r: "r", dart: "dart", jl: "julia",
  ex: "elixir", exs: "elixir", clj: "clojure", cljs: "clojure", cljc: "clojure", edn: "clojure", scm: "scheme", ss: "scheme", rkt: "scheme",
  pas: "pascal", pp: "pascal", dpr: "pascal", lpr: "pascal", tcl: "tcl", sol: "solidity", wgsl: "wgsl",
  sv: "systemverilog", svh: "systemverilog", v: "systemverilog", vh: "systemverilog", st: "st",
  cshtml: "razor", razor: "razor", graphql: "graphql", gql: "graphql", proto: "protobuf", tf: "hcl", hcl: "hcl", bicep: "bicep",
  tsp: "typespec", rq: "sparql", cypher: "cypher", dax: "msdax", pq: "powerquery", pqm: "powerquery", abap: "abap", apex: "apex",
  mk: "makefile", mak: "makefile", diff: "diff", patch: "diff",
};
const byName: Readonly<Record<string, EditorLanguage>> = {
  dockerfile: "dockerfile", containerfile: "dockerfile", makefile: "makefile", gnumakefile: "makefile",
  ".editorconfig": "ini", ".gitconfig": "ini", ".gitattributes": "plaintext", ".gitignore": "plaintext", ".npmrc": "ini", ".env": "ini",
  ".bashrc": "shell", ".zshrc": "shell", ".prettierrc": "json", ".eslintrc": "json",
};

// The extension a new file of a language is saved with: the usual one, where several name the language.
const usualExtension: Readonly<Partial<Record<EditorLanguage, string>>> = {
  plaintext: "txt", markdown: "md", json: "json", typescript: "ts", javascript: "js", csharp: "cs", fsharp: "fs", xml: "xml", html: "html", yaml: "yml",
  python: "py", cpp: "cpp", shell: "sh", powershell: "ps1", bat: "cmd", ini: "ini", clojure: "clj", scheme: "scm", pascal: "pas", systemverilog: "sv",
  razor: "cshtml", graphql: "graphql", hcl: "tf", makefile: "mk", diff: "diff", kotlin: "kt", handlebars: "hbs", pug: "pug", elixir: "ex", perl: "pl", "objective-c": "m",
};

const languageNames: Readonly<Partial<Record<EditorLanguage, string>>> = {
  plaintext: "Plain Text", csharp: "C#", cpp: "C/C++", fsharp: "F#", javascript: "JavaScript", typescript: "TypeScript", markdown: "Markdown", json: "JSON", yaml: "YAML",
  xml: "XML", html: "HTML", css: "CSS", scss: "SCSS", less: "Less", shell: "Shell Script", powershell: "PowerShell", bat: "Batch", sql: "SQL", mysql: "MySQL", pgsql: "PostgreSQL",
  php: "PHP", toml: "TOML", ini: "INI", dockerfile: "Dockerfile", makefile: "Makefile", "objective-c": "Objective-C", vb: "Visual Basic", graphql: "GraphQL",
  protobuf: "Protocol Buffers", hcl: "HCL (Terraform)", mdx: "MDX", qsharp: "Q#", restructuredtext: "reStructuredText", typespec: "TypeSpec", wgsl: "WGSL", r: "R",
  systemverilog: "SystemVerilog", coffee: "CoffeeScript", freemarker2: "FreeMarker", azcli: "Azure CLI", msdax: "DAX", powerquery: "Power Query", st: "Structured Text",
};

/** The name a language is shown with where one is chosen. */
export function languageName(language: EditorLanguage): string {
  return languageNames[language] ?? language.charAt(0).toUpperCase() + language.slice(1);
}

/** The extension of a new file of a language, without the dot; "txt" for a language no extension names. */
export function languageExtension(language: EditorLanguage): string {
  return usualExtension[language] ?? Object.keys(byExtension).find(extension => byExtension[extension] === language) ?? "txt";
}

/** The editor language of a project-relative path: the file name is matched before its extension, ignoring case. */
export function fileLanguage(path: string): EditorLanguage {
  const name = path.slice(Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\")) + 1).toLowerCase();
  const dot = name.lastIndexOf(".");
  return byName[name] ?? (name.startsWith("dockerfile.") ? "dockerfile" : undefined)
    ?? (dot >= 0 ? byExtension[name.slice(dot + 1)] : undefined) ?? "plaintext";
}
