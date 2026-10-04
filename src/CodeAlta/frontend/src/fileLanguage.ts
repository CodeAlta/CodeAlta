/** Monaco language ids the source editor can highlight; anything else is edited as plain text. */
export const editorLanguages = ["plaintext", "ini", "markdown", "json", "typescript", "javascript", "csharp", "fsharp", "vb", "xml", "html", "css", "scss", "less",
  "yaml", "python", "rust", "go", "java", "kotlin", "scala", "swift", "cpp", "objective-c", "shell", "powershell", "bat", "sql", "dockerfile",
  "ruby", "php", "lua", "perl", "r", "dart", "razor", "graphql", "protobuf", "hcl", "bicep"] as const;
export type EditorLanguage = typeof editorLanguages[number];

const byExtension: Readonly<Record<string, EditorLanguage>> = {
  txt: "plaintext", log: "plaintext",
  toml: "ini", ini: "ini", cfg: "ini", conf: "ini", properties: "ini", editorconfig: "ini", gitconfig: "ini",
  md: "markdown", markdown: "markdown",
  json: "json", jsonc: "json", json5: "json", map: "json", webmanifest: "json",
  ts: "typescript", tsx: "typescript", mts: "typescript", cts: "typescript",
  js: "javascript", jsx: "javascript", mjs: "javascript", cjs: "javascript",
  cs: "csharp", csx: "csharp", fs: "fsharp", fsx: "fsharp", fsi: "fsharp", vb: "vb",
  xml: "xml", csproj: "xml", fsproj: "xml", vbproj: "xml", props: "xml", targets: "xml", slnx: "xml", xaml: "xml", axaml: "xml",
  resx: "xml", nuspec: "xml", config: "xml", svg: "xml", xsd: "xml", xslt: "xml", plist: "xml",
  html: "html", htm: "html", css: "css", scss: "scss", less: "less",
  yml: "yaml", yaml: "yaml",
  py: "python", pyi: "python", rs: "rust", go: "go", java: "java", kt: "kotlin", kts: "kotlin", scala: "scala", swift: "swift",
  c: "cpp", h: "cpp", cpp: "cpp", hpp: "cpp", cc: "cpp", hh: "cpp", cxx: "cpp", hxx: "cpp", m: "objective-c", mm: "objective-c",
  sh: "shell", bash: "shell", zsh: "shell", ps1: "powershell", psm1: "powershell", psd1: "powershell", bat: "bat", cmd: "bat",
  sql: "sql", rb: "ruby", php: "php", lua: "lua", pl: "perl", pm: "perl", r: "r", dart: "dart",
  cshtml: "razor", razor: "razor", graphql: "graphql", gql: "graphql", proto: "protobuf", tf: "hcl", hcl: "hcl", bicep: "bicep",
};
const byName: Readonly<Record<string, EditorLanguage>> = {
  dockerfile: "dockerfile", containerfile: "dockerfile", ".editorconfig": "ini", ".gitconfig": "ini", ".gitattributes": "plaintext",
  ".gitignore": "plaintext", ".npmrc": "ini", ".bashrc": "shell", ".zshrc": "shell", ".prettierrc": "json", ".eslintrc": "json",
};

/** The editor language of a project-relative path: the file name is matched before its extension, ignoring case. */
export function fileLanguage(path: string): EditorLanguage {
  const name = path.slice(Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\")) + 1).toLowerCase();
  const dot = name.lastIndexOf(".");
  return byName[name] ?? (name.startsWith("dockerfile.") ? "dockerfile" : undefined)
    ?? (dot >= 0 ? byExtension[name.slice(dot + 1)] : undefined) ?? "plaintext";
}
