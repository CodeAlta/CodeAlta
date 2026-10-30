// The plugin of the markdown-it organization ships no types.
declare module "markdown-it-footnote" {
  import type { MarkdownIt } from "markdown-it";
  /** Reads `[^label]` references, `[^label]: …` notes and `^[…]` inline notes. */
  const footnote: (md: MarkdownIt) => void;
  export default footnote;
}
