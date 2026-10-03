import type { IconName } from "./AppIcon";

/** How a file or folder is drawn in pickers: an icon and a color tone (a CSS `data-file-tone`). */
export type FileAppearance = Readonly<{ icon: IconName; tone: string }>;

// The terminal UI's file-type table (ProjectFileAppearanceRegistry): one tone per language family.
const byExtension: Readonly<Record<string, FileAppearance>> = {
  cs: { icon: "fileCode", tone: "purple" }, csproj: { icon: "fileCode", tone: "purple" }, sln: { icon: "fileCode", tone: "purple" },
  kt: { icon: "fileCode", tone: "purple" }, scss: { icon: "fileCode", tone: "purple" },
  json: { icon: "fileJson", tone: "yellow" }, yml: { icon: "config", tone: "yellow" }, yaml: { icon: "config", tone: "yellow" },
  toml: { icon: "config", tone: "yellow" }, js: { icon: "fileCode", tone: "yellow" }, jsx: { icon: "fileCode", tone: "yellow" }, mjs: { icon: "fileCode", tone: "yellow" },
  md: { icon: "fileText", tone: "blue" }, ts: { icon: "fileCode", tone: "blue" }, tsx: { icon: "fileCode", tone: "blue" },
  cpp: { icon: "fileCode", tone: "blue" }, h: { icon: "fileCode", tone: "blue" }, c: { icon: "fileCode", tone: "blue" }, ps1: { icon: "fileTerminal", tone: "blue" },
  py: { icon: "fileCode", tone: "teal" }, go: { icon: "fileCode", tone: "cyan" }, sql: { icon: "database", tone: "cyan" },
  rs: { icon: "fileCode", tone: "orange" }, java: { icon: "fileCode", tone: "orange" }, html: { icon: "fileCode", tone: "orange" },
  css: { icon: "fileCode", tone: "azure" }, xml: { icon: "fileCode", tone: "cadet" },
  sh: { icon: "fileTerminal", tone: "green" }, bat: { icon: "fileTerminal", tone: "green" }, cmd: { icon: "fileTerminal", tone: "green" },
  png: { icon: "fileImage", tone: "pink" }, jpg: { icon: "fileImage", tone: "pink" }, jpeg: { icon: "fileImage", tone: "pink" },
  gif: { icon: "fileImage", tone: "pink" }, svg: { icon: "fileImage", tone: "pink" }, webp: { icon: "fileImage", tone: "pink" }, ico: { icon: "fileImage", tone: "pink" },
  txt: { icon: "fileText", tone: "muted" },
};
const byName: Readonly<Record<string, FileAppearance>> = { dockerfile: { icon: "fileCode", tone: "blue" } };

/** Icon and tone for a project-relative path; the file name is matched before its extension, ignoring case. */
export function fileAppearance(path: string, directory: boolean): FileAppearance {
  if (directory) return { icon: "folder", tone: "gold" };
  const name = path.slice(path.lastIndexOf("/") + 1).toLowerCase();
  const dot = name.lastIndexOf(".");
  return byName[name] ?? (dot > 0 ? byExtension[name.slice(dot + 1)] : undefined) ?? { icon: "fileGeneric", tone: "muted" };
}

/** A path split for display: the base name and its parent folder ("" at the project root). */
export function splitProjectPath(path: string): { name: string; parent: string } {
  const slash = path.lastIndexOf("/");
  return slash < 0 ? { name: path, parent: "" } : { name: path.slice(slash + 1), parent: path.slice(0, slash) };
}
