// Writes src/brandIcons.gen.ts: the logos of the brands the app names (model providers, models, coding agents,
// issue trackers), taken from two packages that hold nothing but SVG files and have no dependency:
//   @lobehub/icons-static-svg (MIT)  the AI brands, each as a one-color mark and often as a colored one
//   simple-icons (CC0)               the brands it does not have, as one-color marks
// Both are development dependencies: the app ships only the marks listed here, as text in the generated file.
// Run `npm run icons` after a change of the list or of the version of a package.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const lobe = join(root, "node_modules", "@lobehub", "icons-static-svg", "icons");
const simple = join(root, "node_modules", "simple-icons", "icons");

// id, title, and for a mark that has no colored drawing: the color it is drawn in, on a light and on a dark
// background. A mark without any color is drawn in the color of the text around it.
const lobeIcons = [
  ["openai", "OpenAI"], ["codex", "Codex"], ["anthropic", "Anthropic"], ["claude", "Claude"], ["claudecode", "Claude Code"],
  ["github", "GitHub"], ["githubcopilot", "GitHub Copilot"], ["copilot", "Microsoft Copilot"],
  ["gemini", "Gemini"], ["geminicli", "Gemini CLI"], ["gemma", "Gemma"], ["google", "Google"], ["vertexai", "Vertex AI"],
  ["azure", "Azure"], ["azureai", "Azure AI"], ["microsoft", "Microsoft"], ["aws", "AWS"], ["bedrock", "Amazon Bedrock"],
  ["ollama", "Ollama"], ["lmstudio", "LM Studio", "#4338CA", "#8B83F0"], ["vllm", "vLLM"], ["openrouter", "OpenRouter"],
  ["mistral", "Mistral"], ["deepseek", "DeepSeek"], ["xai", "xAI"], ["grok", "Grok"], ["groq", "Groq", "#F55036"],
  ["meta", "Meta"], ["qwen", "Qwen"], ["alibaba", "Alibaba"], ["alibabacloud", "Alibaba Cloud"], ["bailian", "Bailian"],
  ["kimi", "Kimi"], ["moonshot", "Moonshot AI"], ["zhipu", "Zhipu"], ["zai", "Z.ai"], ["minimax", "MiniMax"],
  ["cohere", "Cohere"], ["perplexity", "Perplexity"], ["together", "Together AI"], ["fireworks", "Fireworks AI"],
  ["cerebras", "Cerebras"], ["nvidia", "NVIDIA"], ["huggingface", "Hugging Face"], ["vercel", "Vercel"],
  ["cloudflare", "Cloudflare"], ["workersai", "Workers AI"], ["replicate", "Replicate", "#EA2805"], ["sambanova", "SambaNova"],
  ["siliconcloud", "SiliconCloud"], ["novita", "Novita AI"], ["nebius", "Nebius"], ["deepinfra", "DeepInfra"], ["hyperbolic", "Hyperbolic"],
  ["doubao", "Doubao"], ["volcengine", "Volcengine"], ["baidu", "Baidu"], ["wenxin", "Wenxin"], ["hunyuan", "Hunyuan"], ["stepfun", "StepFun"], ["xiaomimimo", "Xiaomi MiMo"],
  ["opencode", "opencode"], ["cursor", "Cursor"], ["windsurf", "Windsurf"], ["cline", "Cline"], ["roocode", "Roo Code"],
  ["amp", "Amp"], ["goose", "Goose"], ["kiro", "Kiro"], ["mcp", "Model Context Protocol"], ["notion", "Notion"], ["figma", "Figma"],
];
// The colored drawing of these disappears on one of the two backgrounds, too dark or white: the one-color mark is used.
const monoOnly = new Set(["deepinfra", "kimi"]);
const simpleIcons = [
  ["jira", "Jira", "#1868DB", "#579DFF"], ["atlassian", "Atlassian", "#1868DB", "#579DFF"], ["confluence", "Confluence", "#1868DB", "#579DFF"],
  ["bitbucket", "Bitbucket", "#1868DB", "#579DFF"], ["trello", "Trello", "#1868DB", "#579DFF"],
  ["git", "Git", "#F05032"], ["gitlab", "GitLab", "#FC6D26"], ["linear", "Linear", "#5E6AD2", "#8C95EE"],
];

// What is between the tags of the root element, without its title: the drawing. The files are data of packages
// that are trusted to hold drawings only, and are still refused when they hold anything that runs or loads.
function drawing(file) {
  const text = readFileSync(file, "utf8").trim();
  const match = /^<svg\b([^>]*)>([\s\S]*)<\/svg>$/.exec(text);
  if (!match) throw new Error(`${file}: not one svg element`);
  if (!/\bviewBox="0 0 24 24"/.test(match[1])) throw new Error(`${file}: not drawn in a 24 by 24 box`);
  const body = match[2].replace(/<title>[\s\S]*?<\/title>/g, "").trim();
  if (/<\s*(script|foreignObject|image|use|a|style)\b|\son\w+\s*=|javascript:|(href|src)\s*=\s*"(?!#)/i.test(body)) throw new Error(`${file}: holds more than a drawing`);
  return { body, evenodd: /\bfill-rule="evenodd"/.test(match[1]) };
}

const icons = {};
for (const [id, title, tint, darkTint] of lobeIcons) {
  const mono = drawing(join(lobe, `${id}.svg`));
  const colored = join(lobe, `${id}-color.svg`);
  icons[id] = { title, mono: mono.body, ...(mono.evenodd ? { evenodd: true } : {}), ...(existsSync(colored) && !monoOnly.has(id) ? { color: drawing(colored).body } : {}), ...(tint ? { tint } : {}), ...(darkTint ? { darkTint } : {}) };
}
for (const [id, title, tint, darkTint] of simpleIcons) {
  if (icons[id]) throw new Error(`${id} is listed twice`);
  icons[id] = { title, mono: drawing(join(simple, `${id}.svg`)).body, ...(tint ? { tint } : {}), ...(darkTint ? { darkTint } : {}) };
}

const version = name => JSON.parse(readFileSync(join(root, "node_modules", ...name.split("/"), "package.json"), "utf8")).version;
const lines = [
  `// Generated by scripts/generate-brand-icons.mjs from @lobehub/icons-static-svg ${version("@lobehub/icons-static-svg")} (MIT) and simple-icons ${version("simple-icons")} (CC0). Do not edit: run \`npm run icons\`.`,
  "/** The logo of a brand, drawn in a 24 by 24 box. */",
  "export interface BrandIconData {",
  "  /** The name of the brand. */",
  "  readonly title: string;",
  "  /** The mark in one color, the color of the text around it unless it has a tint. */",
  "  readonly mono: string;",
  "  /** Whether the one-color mark fills by the even-odd rule. */",
  "  readonly evenodd?: true;",
  "  /** The mark in the colors of the brand, when it has such a drawing. */",
  "  readonly color?: string;",
  "  /** The color of the brand for a mark that has no colored drawing, on a light and on a dark background. */",
  "  readonly tint?: string;",
  "  readonly darkTint?: string;",
  "}",
  "",
  "export const brandIcons = {",
  ...Object.keys(icons).sort().map(id => `  ${JSON.stringify(id)}: ${JSON.stringify(icons[id])},`),
  "} as const satisfies Record<string, BrandIconData>;",
  "",
];
writeFileSync(join(root, "src", "brandIcons.gen.ts"), lines.join("\n"));
console.log(`${Object.keys(icons).length} brand icons written to src/brandIcons.gen.ts`);
