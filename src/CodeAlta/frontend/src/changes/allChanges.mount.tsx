// The view of all changed files alone, in a frame of a set size, over literal files: no application host.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { ShellLanguageContext } from "../shellLanguage";
import { AllChanges } from "./AllChanges";
import type { ChangeContent, ChangedFile } from "./projectChanges";

const text = (name: string, count: number) => Array.from({ length: count }, (_, index) => `${name} line ${index + 1}`).join("\n") + "\n";
const changed = (path: string, values: Partial<ChangedFile> = {}): ChangedFile =>
  ({ path, originalPath: null, status: "modified", insertions: 3, deletions: 3, binary: false, revision: `r-${path}`, ...values });
// Ten files with three changed lines in four hundred, a new one, a deleted one, a binary one and a new one too long to be shown whole.
const edited = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"].map(name => `${name}.txt`);
const files: readonly ChangedFile[] = [...edited.map(path => changed(path)), changed("new.txt", { status: "untracked", insertions: 30, deletions: 0 }),
  changed("gone.txt", { status: "deleted", insertions: 0, deletions: 10 }), changed("image.bin", { binary: true, insertions: null, deletions: null }),
  changed("long.txt", { status: "added", insertions: 1500, deletions: 0 })];

const reads: Record<string, number> = {};
function content(file: ChangedFile): ChangeContent | string {
  const side = (original: string, modified: string, originalState: ChangeContent["originalState"] = "text", modifiedState: ChangeContent["modifiedState"] = "text"): ChangeContent =>
    ({ path: file.path, revision: file.revision, original, originalState, modified, modifiedState });
  if (file.path === "new.txt") return side("", text("new", 30), "absent");
  if (file.path === "gone.txt") return side(text("gone", 10), "", "text", "absent");
  if (file.path === "image.bin") return side("", "", "binary", "binary");
  if (file.path === "long.txt") return side("", text("long", 1500), "absent");
  const original = text(file.path, 400);
  return side(original, original.replace(/ line 20[123]\n/g, match => match.replace(" line ", " changed ")));
}
const read = (file: ChangedFile, signal: AbortSignal) => new Promise<ChangeContent | string>((resolve, reject) => {
  reads[file.path] = (reads[file.path] ?? 0) + 1;
  setTimeout(() => signal.aborted ? reject(new DOMException("Aborted", "AbortError")) : resolve(content(file)), 10);
});
const look = { sideBySide: false, wrap: false, ignoreWhitespace: false, collapseUnchanged: true };
const openable = () => null;
const nothing = () => { };

function Fixture() {
  const [current, setCurrent] = useState<string | null>(files[0].path);
  const [reveal, setReveal] = useState<Readonly<{ path: string }> | null>(null);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const [toggle] = useState(() => (path: string) => setCollapsed(known => { const next = new Set(known); if (!next.delete(path)) next.add(path); return next; }));
  Object.assign(window, { allChangesFixture: { reads, current, toggle, reveal: (path: string) => { setCurrent(path); setReveal({ path }); } } });
  return <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: nothing }}>
    <div className="changes-diff-surface" style={{ width: 900, height: 600 }}>
      <AllChanges files={files} scope="fixture" visible look={look} current={current} reveal={reveal} collapsed={collapsed} truncated={false}
        read={read} openable={openable} onCurrent={setCurrent} onToggle={toggle} onOpenFile={nothing} onStale={nothing} />
    </div>
  </ShellLanguageContext.Provider>;
}
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
