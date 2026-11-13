// csvlite 1.4.2 (vendored). Third-party code: do not edit, it is replaced on upgrade.

/** Parses CSV text into rows of fields. */
export function parse(text, separator = ",") {
  const rows = [];
  let start = 0;
  for (let index = 0; index < text.length; index++) {
    if (text[index] === "\n") {
      rows.push(text.slice(start, index).replace(/\r$/, "").split(separator));
      start = index + 1;
    }
  }
  return rows;
}
