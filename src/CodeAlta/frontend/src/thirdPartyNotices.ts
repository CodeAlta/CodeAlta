// The notices of the npm packages the build of the frontend ships (vite.config.ts writes them beside the bundle): their licenses
// (MIT, BSD, ISC, Apache...) ask that their copyright and their permission text go with every copy, built ones included, and a
// minified bundle keeps none of the comments that held them. The list comes from what the bundle holds, so a new dependency
// cannot ship without its notice.
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, sep } from "node:path";

/** The folder of the npm package a module of the bundle comes from, or null for a module of the application or of the build. */
export function packageRootOf(moduleId: string): string | null {
  // A module of the build (`\0vite/...`) or a query (`?commonjs-exports`) is cut off: only a path of a file counts.
  const path = moduleId.startsWith("\0") ? "" : moduleId.split("?", 1)[0].replaceAll("/", sep);
  const marker = `${sep}node_modules${sep}`;
  const at = path.lastIndexOf(marker);
  if (at < 0) return null;
  const parts = path.slice(at + marker.length).split(sep);
  const depth = parts[0]?.startsWith("@") ? 2 : 1;
  if (parts.length <= depth) return null;
  return path.slice(0, at + marker.length) + parts.slice(0, depth).join(sep);
}

type Notice = Readonly<{ name: string; version: string; license: string; texts: readonly string[] }>;

// The files that hold the license of a package, and its NOTICE when it has one (Apache-2.0 asks for it to go along).
const licenseFile = /^(licen[cs]e|copying|notice)([-._].*)?$/iu;

function noticeOf(root: string): Notice | null {
  const manifest = join(root, "package.json");
  if (!existsSync(manifest)) return null;
  const json = JSON.parse(readFileSync(manifest, "utf8")) as { name?: string; version?: string; license?: unknown; licenses?: unknown };
  const declared = json.license ?? json.licenses;
  const license = typeof declared === "string" ? declared
    : Array.isArray(declared) ? declared.map(item => (item as { type?: string }).type ?? String(item)).join(" OR ")
    : (declared as { type?: string } | undefined)?.type ?? "UNKNOWN";
  const texts = readdirSync(root).filter(file => licenseFile.test(file)).sort()
    .map(file => readFileSync(join(root, file), "utf8").replaceAll("\r\n", "\n").trim());
  return { name: json.name ?? root.split(sep).at(-1)!, version: json.version ?? "", license, texts };
}

// A package nested in another (a `node_modules` of its own, or a `package.json` in a subfolder that only sets the type of its
// modules) is named by the nearest `package.json` that has a name.
function namedRoot(root: string): string {
  for (let folder = root; ; folder = dirname(folder)) {
    const manifest = join(folder, "package.json");
    if (existsSync(manifest) && (JSON.parse(readFileSync(manifest, "utf8")) as { name?: string }).name) return folder;
    if (dirname(folder) === folder) return root;
  }
}

/**
 * The text of the notices of the packages the modules of a bundle come from: each package once, sorted by name, with its
 * version, the license its `package.json` declares and the text of its license files (or a line that says it has none).
 */
export function thirdPartyNotices(moduleIds: Iterable<string>): string {
  const roots = new Set<string>();
  for (const id of moduleIds) {
    const root = packageRootOf(id);
    if (root) roots.add(namedRoot(root));
  }

  const notices = new Map<string, Notice>();
  for (const root of roots) {
    const notice = noticeOf(root);
    if (notice && !notices.has(`${notice.name}@${notice.version}`)) notices.set(`${notice.name}@${notice.version}`, notice);
  }

  const rule = "=".repeat(80);
  const sections = [...notices.values()].sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : a.version < b.version ? -1 : 1).map(notice => [
    rule,
    `${notice.name} ${notice.version} (${notice.license})`,
    rule,
    "",
    notice.texts.length ? notice.texts.join(`\n\n${"-".repeat(40)}\n\n`) : `The package has no license file; its package.json declares: ${notice.license}.`,
    "",
  ].join("\n"));
  return [
    "Third-party notices of the frontend of CodeAlta Desktop",
    "",
    "The window, the phone page and the libraries lent to plugins include the code of these packages, under the licenses below.",
    "",
    ...sections,
  ].join("\n");
}
