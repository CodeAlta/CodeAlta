import { isBrandIcon, type BrandIconName } from "./BrandIcon";
import { isSymbolIcon, type SymbolIconName } from "./symbolIcons";

/** What is known of a provider when its logo is chosen. Every part may be missing. */
export interface ProviderBrandSource {
  /** Its key in the configuration. */
  readonly key?: string | null;
  /** Its adapter type, such as `openai-chat`. */
  readonly type?: string | null;
  /** Its display name. */
  readonly name?: string | null;
  /** The address of its API. */
  readonly apiUrl?: string | null;
  /** The icon its definition names. */
  readonly icon?: string | null;
  /** The color its definition gives the icon. */
  readonly color?: string | null;
}

/**
 * The icon something is shown with: the logo of a brand, or a general icon that was chosen for it, or none
 * when no brand is known. A color is there only when one was chosen.
 */
export interface Brand {
  readonly icon: BrandIconName | null;
  readonly symbol?: SymbolIconName;
  readonly color?: string;
}

const none: Brand = { icon: null };

// A word of a key, of a name or of an address that names a brand. The first match wins, so a product comes
// before the company that makes it. A word is matched whole: `meta` is not found in `metadata`.
const words: readonly (readonly [RegExp, BrandIconName])[] = [
  [/claude[-_ ]?code/, "claudecode"], [/codex/, "codex"], [/copilot/, "githubcopilot"], [/github/, "github"],
  [/openrouter/, "openrouter"], [/azure/, "azure"], [/bedrock/, "bedrock"], [/vertex/, "vertexai"],
  [/gemini|generativelanguage/, "gemini"], [/gemma/, "gemma"], [/google/, "google"],
  [/anthropic/, "anthropic"], [/claude/, "claude"], [/openai|chatgpt/, "openai"],
  [/ollama/, "ollama"], [/lm[-_ ]?studio/, "lmstudio"], [/vllm/, "vllm"],
  [/mistral|codestral/, "mistral"], [/deepseek/, "deepseek"], [/grok/, "grok"], [/\bx\.?ai\b/, "xai"], [/groq/, "groq"],
  [/\bllama|\bmeta\b/, "meta"], [/qwen/, "qwen"], [/bailian/, "bailian"], [/alibaba|aliyun|dashscope/, "alibaba"],
  [/kimi/, "kimi"], [/moonshot/, "moonshot"], [/zhipu|bigmodel|\bglm\b/, "zhipu"], [/\bz\.?ai\b/, "zai"], [/minimax/, "minimax"],
  [/cohere/, "cohere"], [/perplexity/, "perplexity"], [/together/, "together"], [/fireworks/, "fireworks"],
  [/cerebras/, "cerebras"], [/nvidia/, "nvidia"], [/hugging[-_ ]?face/, "huggingface"], [/vercel/, "vercel"],
  [/workers[-_ ]?ai/, "workersai"], [/cloudflare/, "cloudflare"], [/replicate/, "replicate"], [/sambanova/, "sambanova"],
  [/silicon(cloud|flow)/, "siliconcloud"], [/novita/, "novita"], [/nebius/, "nebius"], [/deepinfra/, "deepinfra"], [/hyperbolic/, "hyperbolic"],
  [/doubao/, "doubao"], [/volc(engine|es)?\b/, "volcengine"], [/wenxin|ernie/, "wenxin"], [/baidu|qianfan/, "baidu"],
  [/hunyuan/, "hunyuan"], [/stepfun/, "stepfun"], [/xiaomi|\bmimo\b/, "xiaomimimo"], [/opencode/, "opencode"],
  [/jira/, "jira"], [/confluence/, "confluence"], [/bitbucket/, "bitbucket"], [/trello/, "trello"], [/atlassian/, "atlassian"],
  [/gitlab/, "gitlab"], [/\blinear\b/, "linear"], [/notion/, "notion"], [/figma/, "figma"],
];

// The adapter types that belong to one brand. `openai-chat` is the protocol of many: it names none.
const types: Readonly<Record<string, BrandIconName>> = {
  codex: "codex", copilot: "githubcopilot", "claude-code": "claudecode", xai: "xai", mistral: "mistral",
  "google-genai": "gemini", "vertex-ai": "vertexai", "azure-openai": "azure", "openai-responses": "openai", anthropic: "anthropic",
};

// The model families, by what their ids hold. A family comes before a provider: a model of Anthropic that is
// reached through another provider is still shown as Claude.
const families: readonly (readonly [RegExp, BrandIconName])[] = [
  [/claude|\b(opus|sonnet|haiku|fable)\b/, "claude"], [/gemini/, "gemini"], [/gemma/, "gemma"],
  [/gpt|chatgpt|codex|davinci|\bo[1-9](-|$)/, "openai"], [/grok/, "grok"], [/deepseek/, "deepseek"],
  [/mistral|codestral|devstral|magistral|pixtral|ministral/, "mistral"], [/llama/, "meta"],
  [/qwen|\bqwq\b/, "qwen"], [/kimi|moonshot/, "kimi"], [/\bglm\b/, "zhipu"], [/minimax|\babab/, "minimax"],
  [/doubao/, "doubao"], [/ernie/, "wenxin"], [/hunyuan/, "hunyuan"], [/\bmimo\b/, "xiaomimimo"],
  [/\bcommand-|cohere/, "cohere"], [/\bsonar\b/, "perplexity"], [/\bphi-?\d/, "microsoft"], [/\bstep-\d/, "stepfun"],
];

function find(table: readonly (readonly [RegExp, BrandIconName])[], text: string | null | undefined): BrandIconName | null {
  const value = text?.trim().toLowerCase();
  if (!value) return null;
  for (const [word, icon] of table) if (word.test(value)) return icon;
  return null;
}

/** A color as the configuration writes it, `#rgb` or `#rrggbb`; undefined for anything else. */
export function brandColor(text: string | null | undefined): string | undefined {
  const value = text?.trim();
  return value && /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(value) ? value : undefined;
}

/** The brand a text names: the key of a plugin or of an MCP server, the name of a product. */
export function namedBrand(text: string | null | undefined): BrandIconName | null {
  return find(words, text);
}

/**
 * The logo of a provider: the icon its definition names (a brand or a general icon), else the brand its key names, then its name, then
 * the address of its API, then its adapter type. A color applies to whichever icon is found.
 */
export function providerBrand(source: ProviderBrandSource | null | undefined): Brand {
  if (!source) return none;
  const written = source.icon?.trim().toLowerCase();
  const color = brandColor(source.color);
  // A general icon was chosen: no brand is looked for.
  if (isSymbolIcon(written)) return color ? { icon: null, symbol: written, color } : { icon: null, symbol: written };
  const icon = (isBrandIcon(written) ? written : null) ?? find(words, source.key) ?? find(words, source.name) ?? find(words, host(source.apiUrl))
    ?? types[source.type?.trim().toLowerCase() ?? ""] ?? null;
  return color ? { icon, color } : { icon };
}

/** The logo of a model: the brand of its family when its id names one, else the logo of its provider. */
export function modelBrand(modelId: string | null | undefined, provider?: Brand): Brand {
  const family = find(families, modelId);
  return family ? { icon: family } : provider ?? none;
}

/** The logo of an issue tracker or of a code host, by the name of its service. */
export function serviceBrand(service: string | null | undefined): BrandIconName | null {
  switch (service?.trim().toLowerCase()) {
    case "github": return "github";
    case "gitlab": return "gitlab";
    case "bitbucket": return "bitbucket";
    case "azure_devops": return "azure";
    case "jira": return "jira";
    default: return null;
  }
}

/** The logo of a plugin of the app, by its id. */
export function pluginBrand(pluginId: string | null | undefined): BrandIconName | null {
  switch (pluginId?.trim().toLowerCase()) {
    case "git": return "git";
    case "mcp": return "mcp";
    default: return namedBrand(pluginId);
  }
}

// The host and the port of an address: what names the service, and a local one by its port.
function host(url: string | null | undefined): string | null {
  const value = url?.trim();
  if (!value) return null;
  try {
    const parsed = new URL(value);
    if (/^(localhost|127\.0\.0\.1|\[::1\])$/.test(parsed.hostname)) return parsed.port === "11434" ? "ollama" : parsed.port === "1234" ? "lmstudio" : null;
    return parsed.hostname;
  } catch {
    return null;
  }
}
