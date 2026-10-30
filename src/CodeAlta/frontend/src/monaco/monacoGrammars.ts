import type { languages } from "monaco-editor/editor/editor.api.js";

/** A language Monaco does not ship: how it is registered, bracketed and tokenized. */
export type OwnGrammar = Readonly<{ extensions: readonly string[]; aliases: readonly string[];
  configuration: languages.LanguageConfiguration; tokens: languages.IMonarchLanguage }>;

const quotes = [{ open: '"', close: '"', notIn: ["string"] }, { open: "'", close: "'", notIn: ["string"] }];

// Monaco's own JSON support is a language service with a worker; highlighting only needs these tokens.
const json: OwnGrammar = {
  extensions: [".json", ".jsonc"], aliases: ["JSON", "json"],
  configuration: { comments: { lineComment: "//", blockComment: ["/*", "*/"] }, brackets: [["{", "}"], ["[", "]"]],
    autoClosingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, { open: '"', close: '"', notIn: ["string"] }] },
  tokens: { defaultToken: "", tokenPostfix: ".json", tokenizer: {
    root: [
      [/"(?:[^"\\]|\\.)*"(?=\s*:)/, "type.identifier"],
      [/"/, "string", "@string"],
      [/-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?/, "number"],
      [/\b(?:true|false|null)\b/, "keyword"],
      [/\/\/.*$/, "comment"],
      [/\/\*/, "comment", "@comment"],
      [/[{}[\]]/, "@brackets"],
      [/[,:]/, "delimiter"],
    ],
    string: [[/[^\\"]+/, "string"], [/\\./, "string.escape"], [/"/, "string", "@pop"]],
    comment: [[/[^/*]+/, "comment"], [/\*\//, "comment", "@pop"], [/[/*]/, "comment"]],
  } },
};

const toml: OwnGrammar = {
  extensions: [".toml"], aliases: ["TOML", "toml"],
  configuration: { comments: { lineComment: "#" }, brackets: [["{", "}"], ["[", "]"]],
    autoClosingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, ...quotes] },
  tokens: { defaultToken: "", tokenPostfix: ".toml", tokenizer: {
    root: [
      [/#.*$/, "comment"],
      [/^\s*\[\[?[^\]#]*\]\]?/, "type.identifier"],
      [/(?:[A-Za-z0-9_-]+|"(?:[^"\\]|\\.)*"|'[^']*')(?=\s*(?:\.\s*(?:[A-Za-z0-9_-]+|"[^"]*"|'[^']*')\s*)*=)/, "key"],
      [/"""/, "string", "@multilineBasic"],
      [/'''/, "string", "@multilineLiteral"],
      [/"/, "string", "@basic"],
      [/'[^']*'/, "string"],
      [/\b(?:true|false)\b/, "keyword"],
      [/\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d+)?)?(?:[Zz]|[+-]\d{2}:\d{2})?/, "number"],
      [/\d{2}:\d{2}:\d{2}(?:\.\d+)?/, "number"],
      [/[+-]?(?:0x[0-9A-Fa-f_]+|0o[0-7_]+|0b[01_]+|inf|nan|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d[\d_]*)?)/, "number"],
      [/[{}[\]]/, "@brackets"],
      [/[=,.]/, "delimiter"],
    ],
    basic: [[/[^\\"]+/, "string"], [/\\./, "string.escape"], [/"/, "string", "@pop"]],
    multilineBasic: [[/"""/, "string", "@pop"], [/\\./, "string.escape"], [/[^\\"]+|"/, "string"]],
    multilineLiteral: [[/'''/, "string", "@pop"], [/[^']+|'/, "string"]],
  } },
};

const makefile: OwnGrammar = {
  extensions: [".mk", ".mak"], aliases: ["Makefile", "makefile"],
  configuration: { comments: { lineComment: "#" }, brackets: [["(", ")"], ["{", "}"]],
    autoClosingPairs: [{ open: "(", close: ")" }, { open: "{", close: "}" }, ...quotes] },
  tokens: { defaultToken: "", tokenPostfix: ".makefile", tokenizer: {
    root: [
      [/#.*$/, "comment"],
      [/^\s*-?(?:include|sinclude|ifeq|ifneq|ifdef|ifndef|else|endif|define|endef|export|unexport|override|undefine|vpath)\b/, "keyword"],
      [/^\.[A-Z_]+(?=\s*:)/, "keyword"],
      [/^[A-Za-z0-9_.-]+(?=\s*(?:[?:+!]|::)?=)/, "variable"],
      [/^[^\s:#=][^:#=]*(?=:(?!=))/, "type.identifier"],
      [/\$[({][^)}]*[)}]/, "variable.predefined"],
      [/\$[@<^+?*%|$]/, "variable.predefined"],
      [/"(?:[^"\\]|\\.)*"/, "string"],
      [/'[^']*'/, "string"],
      [/(?:[?:+!]|::)?=/, "delimiter"],
    ],
  } },
};

const diff: OwnGrammar = {
  extensions: [".diff", ".patch"], aliases: ["Diff", "diff"],
  configuration: {},
  tokens: { defaultToken: "", tokenPostfix: ".diff", tokenizer: {
    root: [
      [/^(?:diff|index|new file|deleted file|old mode|new mode|similarity|rename|copy|Binary files|From|Date|Subject)\b.*$/, "keyword"],
      [/^(?:---|\+\+\+) .*$/, "type.identifier"],
      [/^@@.*?@@/, "number"],
      [/^\+.*$/, "inserted"],
      [/^-.*$/, "deleted"],
    ],
  } },
};

// LaTeX: commands, environments and the structure of a document, `%` comments, and math, whose commands keep their color
// in the color of a string.
const latex: OwnGrammar = {
  extensions: [".tex", ".sty", ".cls", ".ltx", ".dtx"], aliases: ["LaTeX", "latex", "tex"],
  configuration: { comments: { lineComment: "%" }, brackets: [["{", "}"], ["[", "]"], ["(", ")"]],
    autoClosingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, { open: "(", close: ")" }],
    surroundingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, { open: "(", close: ")" }, { open: "$", close: "$" }] },
  tokens: { defaultToken: "", tokenPostfix: ".latex", tokenizer: {
    root: [
      [/%.*$/, "comment"],
      [/(\\(?:begin|end))(\s*)(\{)([^}]*)(\})/, ["keyword", "", "@brackets", "type.identifier", "@brackets"]],
      [/\\(?:documentclass|usepackage|RequirePackage|input|include|includeonly|part|chapter|section|subsection|subsubsection|paragraph|subparagraph|title|author|date|maketitle|tableofcontents|item|caption|label|ref|eqref|pageref|cite|citep|citet|bibliography|bibliographystyle|addbibresource|printbibliography|newcommand|renewcommand|providecommand|newenvironment|renewenvironment|def|let|DeclareMathOperator)(?![a-zA-Z@])\*?/, "keyword"],
      [/\\\[/, "string", "@displayMath"],
      [/\\\(/, "string", "@inlineMath"],
      [/\$\$/, "string", "@dollarDisplay"],
      [/\$/, "string", "@dollarInline"],
      [/\\[a-zA-Z@]+\*?/, "tag"],
      [/\\./, "string.escape"],
      [/[{}[\]()]/, "@brackets"],
      [/[&~]/, "delimiter"],
      [/\d+(?:\.\d+)?(?:pt|em|ex|cm|mm|in|bp|pc|sp|mu)?/, "number"],
    ],
    displayMath: [[/\\\]/, "string", "@pop"], { include: "@math" }],
    inlineMath: [[/\\\)/, "string", "@pop"], { include: "@math" }],
    dollarDisplay: [[/\$\$/, "string", "@pop"], { include: "@math" }],
    dollarInline: [[/\$/, "string", "@pop"], { include: "@math" }],
    math: [[/%.*$/, "comment"], [/\\[a-zA-Z@]+/, "tag"], [/\\./, "string.escape"], [/[{}[\]()]/, "@brackets"], [/[^\\$%{}[\]()]+/, "string"]],
  } },
};

// BibTeX: the type and key of an entry, its fields, and their values in braces (nested) or quotes.
const bibtex: OwnGrammar = {
  extensions: [".bib"], aliases: ["BibTeX", "bibtex"],
  configuration: { comments: { lineComment: "%" }, brackets: [["{", "}"], ["(", ")"]],
    autoClosingPairs: [{ open: "{", close: "}" }, { open: "(", close: ")" }, { open: '"', close: '"', notIn: ["string"] }] },
  tokens: { defaultToken: "", tokenPostfix: ".bibtex", tokenizer: {
    root: [
      [/%.*$/, "comment"],
      [/(@[a-zA-Z]+)(\s*)([{(])(\s*)([^,\s{}()]+)/, ["keyword", "", "@brackets", "", "type.identifier"]],
      [/@[a-zA-Z]+/, "keyword"],
      [/[a-zA-Z][\w:.-]*(?=\s*=)/, "key"],
      [/(=)(\s*)(\{)/, ["delimiter", "", { token: "string", next: "@braced" }]],
      [/"/, "string", "@quoted"],
      [/\d+/, "number"],
      [/[{}()]/, "@brackets"],
      [/[=,#]/, "delimiter"],
    ],
    braced: [[/[^{}]+/, "string"], [/\{/, "string", "@braced"], [/\}/, "string", "@pop"]],
    quoted: [[/[^"{}\\]+/, "string"], [/\\./, "string.escape"], [/\{[^}]*\}/, "string"], [/"/, "string", "@pop"]],
  } },
};

/** The grammars the app defines itself, by language id. */
export const ownGrammars = { json, toml, makefile, diff, latex, bibtex } as const;
