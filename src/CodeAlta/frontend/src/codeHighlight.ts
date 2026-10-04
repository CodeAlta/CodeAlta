import hljs from "highlight.js/lib/common";
import cmake from "highlight.js/lib/languages/cmake";
import dart from "highlight.js/lib/languages/dart";
import dockerfile from "highlight.js/lib/languages/dockerfile";
import dos from "highlight.js/lib/languages/dos";
import elixir from "highlight.js/lib/languages/elixir";
import fsharp from "highlight.js/lib/languages/fsharp";
import haskell from "highlight.js/lib/languages/haskell";
import http from "highlight.js/lib/languages/http";
import nginx from "highlight.js/lib/languages/nginx";
import powershell from "highlight.js/lib/languages/powershell";
import properties from "highlight.js/lib/languages/properties";
import protobuf from "highlight.js/lib/languages/protobuf";
import scala from "highlight.js/lib/languages/scala";

// highlight.js's common set (C#, C/C++, JavaScript, TypeScript, Python, Go, Rust, Java, Kotlin, Swift, JSON,
// YAML, XML/HTML, CSS, SQL, shell, diff, Markdown, ...) plus the languages below.
for (const [name, language] of Object.entries({ cmake, dart, dockerfile, dos, elixir, fsharp, haskell, http, nginx,
  powershell, properties, protobuf, scala })) hljs.registerLanguage(name, language);
hljs.registerAliases(["jsonc", "json5"], { languageName: "json" });
hljs.registerAliases(["vue", "svelte", "razor", "cshtml", "xaml", "csproj", "props", "targets"], { languageName: "xml" });
hljs.registerAliases(["zsh", "fish"], { languageName: "bash" });

/** A longer block is shown as plain text: highlighting it would hold up the timeline. */
export const maximumHighlightedLength = 200_000;

/**
 * Highlights the text of a fenced code block as HTML made only of `span` elements with `hljs-` classes and
 * escaped text, or returns null when the language is unknown or plain. The language is the fence's first word.
 */
export function highlightCode(text: string, language: string): string | null {
  const name = language.toLowerCase();
  if (text.length > maximumHighlightedLength || !hljs.getLanguage(name) || hljs.getLanguage(name)!.name === "Plain text") return null;
  try { return hljs.highlight(text, { language: name, ignoreIllegals: true }).value; }
  catch { return null; }
}
