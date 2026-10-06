/** One row of a unified diff as it is shown: a file header, a hunk header, or a line with its numbers. */
export type DiffLine = Readonly<{ kind: "file" | "hunk" | "added" | "removed" | "context" | "note"; text: string;
  oldLine: number | null; newLine: number | null }>;

const maximumLines = 4000;

/**
 * Reads a unified diff (one file or several, with or without `diff --git` headers) into rows for display.
 * The lines git writes around a file header (`index`, `---`, `+++`, modes) are folded into the row of the
 * file; a text that is no diff comes back as plain context rows. Nothing is guessed: rows only say what the
 * text says.
 */
export function parseUnifiedDiff(diff: string): DiffLine[] {
  const rows: DiffLine[] = [];
  let oldLine = 0, newLine = 0, inHunk = false, named = false;
  const lines = diff.replaceAll("\r\n", "\n").split("\n");
  if (lines.at(-1) === "") lines.pop();
  for (const line of lines) {
    if (rows.length >= maximumLines) { rows.push({ kind: "note", text: "…", oldLine: null, newLine: null }); break; }
    if (line.startsWith("diff --git ")) {
      const match = /^diff --git a\/(.+) b\/(.+)$/.exec(line);
      rows.push({ kind: "file", text: match ? match[2] : line.slice(11), oldLine: null, newLine: null });
      inHunk = false; named = true; continue;
    }
    if (!inHunk || line.startsWith("--- ") || line.startsWith("+++ ")) {
      // Header lines of a file: only `+++` names a file that no `diff --git` line named.
      if (/^(?:index |new file mode |deleted file mode |old mode |new mode |similarity index |rename from |rename to |--- )/.test(line) && !inHunk) continue;
      if (line.startsWith("+++ ") && !inHunk) {
        if (!named && line.length > 4 && line !== "+++ /dev/null") rows.push({ kind: "file", text: line.slice(4).replace(/^b\//, ""), oldLine: null, newLine: null });
        named = false; continue;
      }
    }
    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@ ?(.*)$/.exec(line);
    if (hunk) {
      oldLine = Number(hunk[1]); newLine = Number(hunk[2]); inHunk = true;
      rows.push({ kind: "hunk", text: hunk[3], oldLine: null, newLine: null });
      continue;
    }
    if (line.startsWith("\\")) { rows.push({ kind: "note", text: line.slice(1).trim(), oldLine: null, newLine: null }); continue; }
    if (inHunk && line.startsWith("+")) rows.push({ kind: "added", text: line.slice(1), oldLine: null, newLine: newLine++ });
    else if (inHunk && line.startsWith("-")) rows.push({ kind: "removed", text: line.slice(1), oldLine: oldLine++, newLine: null });
    else if (inHunk) rows.push({ kind: "context", text: line.startsWith(" ") ? line.slice(1) : line, oldLine: oldLine++, newLine: newLine++ });
    else rows.push({ kind: line.startsWith("+") ? "added" : line.startsWith("-") ? "removed" : "context", text: line, oldLine: null, newLine: null });
  }
  return rows;
}

/** The added and removed lines of the rows. */
export function diffLineCounts(rows: readonly DiffLine[]): Readonly<{ added: number; removed: number }> {
  let added = 0, removed = 0;
  for (const row of rows) if (row.kind === "added") added++; else if (row.kind === "removed") removed++;
  return { added, removed };
}
