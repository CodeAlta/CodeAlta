import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { ConfigurationSnapshot } from "#neoastra";
import { BrandIcon, brandIconNames, brandTitle, isBrandIcon } from "./BrandIcon";
import { brandColor, modelBrand, namedBrand, pluginBrand, providerBrand, serviceBrand } from "./brands";
import { ModelIcon, ProviderBrandsContext, ProviderIcon, providerBrands } from "./ProviderIcon";
import { ProviderColorInput, ProviderIconPicker } from "./ProviderIconFields";
import { isSymbolIcon, symbolIconNames } from "./symbolIcons";

test("every provider CodeAlta ships is shown with the logo of its brand", () => {
  // The keys, the types and the names of the default configuration.
  const shipped: readonly (readonly [string, string, string, string])[] = [
    ["alibaba", "openai-chat", "Alibaba", "alibaba"], ["anthropic", "anthropic", "Anthropic", "anthropic"], ["azure-openai", "azure-openai", "Azure OpenAI", "azure"],
    ["claude-code", "claude-code", "Claude Code", "claudecode"], ["codex", "codex", "Codex", "codex"], ["copilot", "copilot", "Copilot", "githubcopilot"],
    ["deepseek", "openai-chat", "DeepSeek", "deepseek"], ["gemini", "google-genai", "Gemini", "gemini"], ["kimi-for-coding", "anthropic", "Kimi for Coding", "kimi"],
    ["minimax", "anthropic", "MiniMax", "minimax"], ["mistral", "mistral", "Mistral", "mistral"], ["openai", "openai-responses", "OpenAI", "openai"],
    ["openrouter", "openai-chat", "OpenRouter", "openrouter"], ["xiaomi", "openai-chat", "Xiaomi", "xiaomimimo"], ["xai", "xai", "xAI Grok", "xai"], ["zai", "openai-chat", "Z.ai", "zai"],
  ];
  for (const [key, type, name, icon] of shipped) assert.deepEqual(providerBrand({ key, type, name }), { icon }, key);
});

test("a provider of one's own takes the icon and the color its definition names, then what its key, name, address or type says", () => {
  // The icon that is written wins over everything the provider is called.
  assert.deepEqual(providerBrand({ key: "openai", type: "openai-responses", icon: "mistral", color: "#FA520F" }), { icon: "mistral", color: "#FA520F" });
  assert.deepEqual(providerBrand({ key: "work", icon: " Ollama " }), { icon: "ollama" });
  // A general icon, for a provider of no brand.
  assert.deepEqual(providerBrand({ key: "openai", icon: "rocket", color: "#0af" }), { icon: null, symbol: "rocket", color: "#0af" });
  // An id this version does not draw, or a color that is not one, is left out: the provider keeps what is found for it.
  assert.deepEqual(providerBrand({ key: "deepseek", icon: "no-such-icon", color: "red" }), { icon: "deepseek" });
  // A color alone tints the icon that is found, or the icon of a provider when none is.
  assert.deepEqual(providerBrand({ key: "anthropic", color: "#D97757" }), { icon: "anthropic", color: "#D97757" });
  assert.deepEqual(providerBrand({ key: "work", type: "openai-chat", color: "#123456" }), { icon: null, color: "#123456" });

  // By key, then by name, then by the address of the API, then by the type.
  assert.equal(providerBrand({ key: "my-openrouter", type: "openai-chat" }).icon, "openrouter");
  assert.equal(providerBrand({ key: "work", name: "Team Groq", type: "openai-chat" }).icon, "groq");
  assert.equal(providerBrand({ key: "work", type: "openai-chat", apiUrl: "https://api.deepseek.com/v1" }).icon, "deepseek");
  assert.equal(providerBrand({ key: "local", type: "openai-chat", apiUrl: "http://localhost:11434/v1" }).icon, "ollama");
  assert.equal(providerBrand({ key: "local", type: "openai-chat", apiUrl: "http://127.0.0.1:1234/v1" }).icon, "lmstudio");
  assert.equal(providerBrand({ key: "work", type: "anthropic" }).icon, "anthropic");
  assert.equal(providerBrand({ key: "work", type: "vertex-ai" }).icon, "vertexai");
  // The protocol of many providers names none of them, and a word is matched whole.
  assert.deepEqual(providerBrand({ key: "work", type: "openai-chat", apiUrl: "http://localhost:8080/v1" }), { icon: null });
  assert.equal(providerBrand({ key: "metadata-proxy", type: "openai-chat" }).icon, null);
  assert.deepEqual(providerBrand(null), { icon: null });
  assert.deepEqual(providerBrand({}), { icon: null });
});

test("a model is shown with the logo of its family, whatever provider serves it", () => {
  const copilot = providerBrand({ key: "copilot", type: "copilot" });
  for (const [id, icon] of [["claude-sonnet-4.5", "claude"], ["opus", "claude"], ["anthropic/claude-opus-4", "claude"], ["gpt-5.1-codex", "openai"], ["o4-mini", "openai"],
    ["gemini-2.5-pro", "gemini"], ["grok-code-fast-1", "grok"], ["deepseek-chat", "deepseek"], ["devstral-medium", "mistral"], ["meta-llama/llama-4", "meta"],
    ["qwen3-coder", "qwen"], ["kimi-k2", "kimi"], ["glm-4.6", "zhipu"], ["MiniMax-M2", "minimax"]] as const) assert.deepEqual(modelBrand(id, copilot), { icon }, id);
  // A model of no known family takes the logo of its provider, with the color chosen for it.
  assert.deepEqual(modelBrand("raptor-mini", copilot), { icon: "githubcopilot" });
  assert.deepEqual(modelBrand("local-model", { icon: null, symbol: "server", color: "#0af" }), { icon: null, symbol: "server", color: "#0af" });
  assert.deepEqual(modelBrand(null), { icon: null });
  assert.deepEqual(modelBrand("solo", undefined), { icon: null });
});

test("trackers, plugins and named servers have the logo of their product", () => {
  assert.deepEqual(["github", "gitlab", "bitbucket", "azure_devops", "jira", "other", null].map(serviceBrand), ["github", "gitlab", "bitbucket", "azure", "jira", null, null]);
  assert.deepEqual(["jira", "git", "mcp", "statistics", "ui"].map(pluginBrand), ["jira", "git", "mcp", null, null]);
  assert.deepEqual(["github", "atlassian-remote", "my-notion", "figma-dev", "filesystem"].map(namedBrand), ["github", "atlassian", "notion", "figma", null]);
  assert.deepEqual(["#0AF", "#00aaff", " #FA520F ", "0af", "#12345", "red", "", null].map(brandColor), ["#0AF", "#00aaff", "#FA520F", undefined, undefined, undefined, undefined, undefined]);
});

test("the two lists of icons a provider can name do not share a name", () => {
  assert.ok(brandIconNames.length >= 70 && symbolIconNames.length >= 40);
  assert.deepEqual(brandIconNames.filter(name => isSymbolIcon(name)), []);
  assert.ok(brandIconNames.every(name => /^[a-z0-9-]{1,64}$/.test(name) && brandTitle(name).length > 0) && symbolIconNames.every(name => /^[a-z0-9-]{1,64}$/.test(name)), "An icon is named as the host accepts it.");
  assert.ok(isBrandIcon("jira") && !isBrandIcon("toString") && !isBrandIcon(null) && isSymbolIcon("rocket") && !isSymbolIcon("constructor"));
});

test("a logo is drawn in the colors of its brand, in its tint, in the color of the text, or in the color chosen", () => {
  // A drawing in the colors of the brand.
  const claude = renderToStaticMarkup(<BrandIcon name="claude" size={20} />);
  assert.match(claude, /^<svg class="brand-icon" data-brand="claude" viewBox="0 0 24 24" width="20" height="20" fill="currentColor"/);
  assert.match(claude, /fill="#D97757"/i);
  assert.doesNotMatch(claude, /--brand-tint/);
  assert.match(claude, /aria-hidden="true"/);
  // A mark that has a color of its own, lighter on a dark background.
  const jira = renderToStaticMarkup(<BrandIcon name="jira" title="Jira" />);
  assert.match(jira, /style="--brand-tint:#1868DB;--brand-tint-dark:#579DFF"/);
  assert.match(jira, /role="img" aria-label="Jira"/);
  // A mark of no color follows the text: black on a light background, light on a dark one.
  const openai = renderToStaticMarkup(<BrandIcon name="openai" />);
  assert.doesNotMatch(openai, /style=|#[0-9a-f]{6}/i);
  assert.match(openai, /fill-rule="evenodd"/);
  // A color that was chosen draws the one-color mark, on both backgrounds.
  const tinted = renderToStaticMarkup(<BrandIcon name="claude" color="#00aaff" />);
  assert.match(tinted, /style="--brand-tint:#00aaff;--brand-tint-dark:#00aaff"/);
  assert.doesNotMatch(tinted, /#D97757/i);
  // The gradients of two drawings of one brand have names of their own.
  const twice = renderToStaticMarkup(<><BrandIcon name="gemini" /><BrandIcon name="gemini" /></>);
  const ids = [...twice.matchAll(/ id="([^"]+)"/g)].map(match => match[1]);
  assert.ok(ids.length >= 2 && new Set(ids).size === ids.length, "No two gradients share a name.");
  assert.ok(ids.every(id => twice.includes(`url(#${id})`)), "Every gradient is the one its drawing refers to.");
  assert.doesNotMatch(twice, /lobe-icons-/);
});

test("the logo of a provider is found by its key anywhere in the window", () => {
  const snapshot = { providers: [{ id: "codex", name: "Codex", type: "codex", enabled: true, isDefault: true, defaultModel: null, defaultReasoning: null },
    { id: "team", name: "Team", type: "openai-chat", enabled: true, isDefault: false, defaultModel: null, defaultReasoning: null }],
    // Every definition of the file, the disabled one included: its sessions keep its logo.
    providerBrands: [{ key: "Team", type: "openai-chat", name: "Team", icon: "flask-conical", color: "#22B8CD" }, { key: "old", type: "openai-chat", name: "Old", icon: "perplexity", color: null }],
    plugins: [], providerRuntimeAvailable: true, pluginRuntimeAvailable: true, providersTruncated: false, pluginsTruncated: false } satisfies ConfigurationSnapshot;
  const brands = providerBrands(snapshot);
  assert.deepEqual([...brands.keys()].sort(), ["codex", "old", "team"]);
  assert.equal(providerBrands(undefined).size, 0);
  const draw = (node: React.ReactNode) => renderToStaticMarkup(<ProviderBrandsContext.Provider value={brands}>{node}</ProviderBrandsContext.Provider>);
  assert.match(draw(<ProviderIcon providerKey="codex" />), /data-brand="codex"/);
  assert.match(draw(<ProviderIcon providerKey="old" />), /data-brand="perplexity"/);
  // A general icon in the color chosen for it; the key is matched whatever its case.
  assert.match(draw(<ProviderIcon providerKey="team" />), /class="lucide lucide-flask-conical symbol-icon"[^>]*style="color:#22B8CD"/);
  // What the caller knows comes after what the file says, and fills what it leaves out.
  assert.match(draw(<ProviderIcon providerKey="other" known={{ type: "anthropic" }} />), /data-brand="anthropic"/);
  assert.match(draw(<ProviderIcon providerKey="old" known={{ icon: "groq" }} />), /data-brand="groq"/);
  // A provider of no known brand has the icon the caller falls back to.
  assert.match(draw(<ProviderIcon providerKey="unknown" />), /lucide-plug/);
  assert.match(draw(<ProviderIcon providerKey={null} fallback="assistant" />), /lucide-bot/);
  // A model: its family, else its provider.
  assert.match(draw(<ModelIcon modelId="claude-opus-4" providerKey="codex" />), /data-brand="claude"/);
  assert.match(draw(<ModelIcon modelId="something" providerKey="old" />), /data-brand="perplexity"/);
  assert.match(draw(<ModelIcon modelId="something" providerKey="unknown" />), /lucide-cpu/);
});

test("the fields of the form show the icon the provider has and the color chosen for it", () => {
  const found = providerBrand({ key: "deepseek" });
  const draw = (value: string, shown = found) => renderToStaticMarkup(<ProviderIconPicker id="icon" value={value} automatic={found} shown={shown} onChange={() => assert.fail("rendering must not act")} />);
  // Nothing chosen: the logo found for the provider, named as automatic.
  assert.match(draw(""), /<button[^>]*id="icon"[^>]*>.*data-brand="deepseek".*Automatic/s);
  // A brand by its name, a general icon by the name of the icon, and an id this version does not draw as it is written.
  assert.match(draw("mistral", { icon: "mistral" }), /data-brand="mistral".*>Mistral</s);
  assert.match(draw("rocket", { icon: null, symbol: "rocket", color: "#0af" }), /lucide-rocket[^>]*style="color:#0af".*>rocket</s);
  assert.match(draw("later-icon"), /data-brand="deepseek".*later-icon \(Unknown\)/s);

  const color = (value: string) => renderToStaticMarkup(<ProviderColorInput id="color" value={value} onChange={() => assert.fail("rendering must not act")} />);
  // The palette of the system takes six digits; a blank field says that the colors of the icon are kept.
  assert.match(color("#0AF"), /type="color"[^>]*value="#00aaff"/);
  assert.match(color("#0AF"), /aria-label="Clear"/);
  assert.match(color(""), /data-unset="true"/);
  assert.match(color(""), /placeholder="Colors of the icon"/);
  assert.doesNotMatch(color(""), /aria-label="Clear"/);
  // What is not a color is marked, and the palette keeps its neutral value.
  assert.match(color("tomato"), /bp6-intent-danger/);
  assert.match(color("tomato"), /data-unset="true"/);
});
