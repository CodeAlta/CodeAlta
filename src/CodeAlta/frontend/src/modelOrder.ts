// The order of a provider's model lists, on the desktop and on the phone, read from the model ids. Each id is first
// classified into a family: its words without the version and without the variants ("gemini-argon" for
// "gemini-4-argon", "claude-opus" for "claude-opus-4-7" and "claude-4.5-opus", "gpt-sol" for "gpt-6.1-sol-fast"); the
// version is read within it. "Frontier" holds the entries carrying the highest version of their family; all older ones
// go to "Other models", below a separator. Each part is sorted by vendor (the first word of the family, for providers
// that serve several), then by version, highest first, then by family name: "claude-sonnet-4-5" comes before
// "gemini-4-argon", which comes before "gemini-3.8-flash".
// Ids are normalized first: "claude-opus-4-7" reads 4.7, "kimi-k3" reads 3, a "cursor-" prefix and a trailing release
// date ("-20250929", "-2025-01-31", "-0613") are dropped, and a "/" separates words ("anthropic/claude-opus-4.7").
const versionWord = /^k?(\d+(?:\.\d+)*)$/i;
const releaseDate = /[-_](?:\d{8}|\d{4}-\d{2}-\d{2}|\d{4})$/;
// Words that name a variant of a model, not another family.
const variantWordSet: ReadonlySet<string> = new Set(["thinking", "fast", "preview", "latest"]);

/** The family, the version and the variant suffix of a model id. */
export function parseModel(id: string): { family: string; version: number[]; suffix: string } {
  let words = id.toLowerCase().replace(/^cursor-/, "").replace(releaseDate, "").split(/[-_/\s]+/).filter(Boolean);
  let version: number[] = [];
  const at = words.findIndex(word => versionWord.test(word));
  if (at >= 0) {
    let end = at + 1;
    const parts = [versionWord.exec(words[at])![1]];
    while (end < words.length && /^\d{1,2}$/.test(words[end])) parts.push(words[end++]); // "4-7" is 4.7
    version = parts.join(".").split(".").map(Number);
    words = [...words.slice(0, at), ...words.slice(end)];
  }
  return {
    family: words.filter(word => !variantWordSet.has(word)).join("-"),
    version,
    suffix: words.filter(word => variantWordSet.has(word)).join("-"),
  };
}

const compareVersions = (a: readonly number[], b: readonly number[]) => {
  for (let i = 0; i < Math.max(a.length, b.length); i++) { const d = (a[i] ?? 0) - (b[i] ?? 0); if (d) return d; }
  return 0;
};
// Within a family and a version: the base model, then its thinking variant, then the other suffixes by name.
const suffixKey = (suffix: string) => suffix === "" ? "0" : suffix === "thinking" ? "1" : `2${suffix}`;

/** Splits models into the latest of each family ("frontier") and the older ones, each by vendor, version descending, then family. */
export function orderModels<T extends { id: string }>(models: readonly T[]): { frontier: T[]; other: T[] } {
  const parsed = models.map(model => ({ model, p: parseModel(model.id) }));
  const latest = new Map<string, number[]>();
  for (const { p } of parsed) { const v = latest.get(p.family); if (!v || compareVersions(p.version, v) > 0) latest.set(p.family, p.version); }
  const vendor = (family: string) => family.split("-")[0];
  const sort = (a: typeof parsed[number], b: typeof parsed[number]) => vendor(a.p.family).localeCompare(vendor(b.p.family)) || compareVersions(b.p.version, a.p.version)
    || a.p.family.localeCompare(b.p.family) || suffixKey(a.p.suffix).localeCompare(suffixKey(b.p.suffix)) || a.model.id.localeCompare(b.model.id);
  const top = (x: typeof parsed[number]) => compareVersions(x.p.version, latest.get(x.p.family)!) === 0;
  return { frontier: parsed.filter(top).sort(sort).map(x => x.model), other: parsed.filter(x => !top(x)).sort(sort).map(x => x.model) };
}
