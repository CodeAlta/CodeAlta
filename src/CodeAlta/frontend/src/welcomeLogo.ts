// Render just the TUI welcome words from its shared 3d.flf asset, with the same
// one-column letter spacing and per-line trimming as FigletFont.RenderLines.
export function welcomeLogo(font: string): Readonly<{ code: string; alta: string }> {
  const lines = font.split(/\r?\n/);
  const header = lines[0].split(/\s+/);
  const height = Number(header[1]);
  const comments = Number(header[5]);
  if (!header[0].startsWith("flf2a") || !Number.isInteger(height) || height <= 0
    || !Number.isInteger(comments) || comments < 0) throw new Error("Invalid TUI welcome font");
  const first = 1 + comments;
  const endMark = lines[first].at(-1)!;
  const word = (text: string) => Array.from({ length: height }, (_, row) => Array.from(text, letter => {
    const line = lines[first + (letter.charCodeAt(0) - 32) * height + row];
    let end = line.length;
    while (end > 0 && line[end - 1] === endMark) end--;
    return line.slice(0, end).replaceAll(header[0][5], " ");
  }).join(" ").trimEnd()).join("\n");
  return Object.freeze({ code: word("Code"), alta: word("Alta") });
}
