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

/** The grammars the app defines itself, by language id. */
export const ownGrammars = { json, toml, makefile, diff } as const;
