---
title: "Decide the default context size of a session on the Anthropic API"
kind: problem
status: pending
created: 2026-10-30
summary: "A session of the Claude API starts with the whole input of a 1M-token model (872K) as its limit, and CodeAlta sends no context-1m beta: if the endpoint needs that beta for the window, every request above 200K is refused until the overflow recovery lowers the limit."
---

## Why

Found while reviewing #274, which added 200K as a size a session *can* choose on the Claude API but left the default alone.

- [models_dev_db.json](src/CodeAlta.Agent/Data/models_dev_db.json) gives every Claude 5 model `limit.context = 1000000`, `limit.output = 128000` and no `limit.input`, so [AgentTokenBudgetResolver.ResolveInputContextLimit](src/CodeAlta.Agent/Runtime/Compaction/AgentTokenBudgetResolver.cs#L135) derives an input limit of **872,000** — unless the Models API reports `max_input_tokens`, which [AnthropicModelProviderRuntime.ToAgentModelInfo](src/CodeAlta.Agent.Anthropic/AnthropicModelProviderRuntime.cs#L610) reads into `maxInputTokens`. Either way the default is far above the 200K standard window.
- Nothing sends a `context-1m-*` beta: `grep -i "context-1m|1m-2025|context_1m"` over `src` finds no match, and the only `anthropic-beta` header CodeAlta writes is the adaptive-thinking one in [AnthropicAdaptiveThinkingChatClient.cs](src/CodeAlta.Agent.Anthropic/AnthropicAdaptiveThinkingChatClient.cs#L43).

If the endpoint requires that beta to accept more than 200K for a model, a long session keeps filling to 95% of 872K, every request is refused, and only the overflow recovery (`ResolveForCompaction` with the last accepted input size) brings it back — a visible error and a compaction the user did not ask for, each time. If the window needs no beta, the default is right and nothing is to be done but say so.

## What to do

Check what the Claude API accepts today for a 1M-token model without a beta header. Then either send the beta with the request when the session's size is above the standard window, or make 200K the default size of these models on the Claude API (`StandardContextSize` is already offered as a choice), or record that the window is generally available and leave it.

## Where

`src/CodeAlta.Agent.Anthropic/AnthropicModelProviderRuntime.cs`, `src/CodeAlta.Agent.Anthropic/AnthropicAdaptiveThinkingChatClient.cs`, `src/CodeAlta.Agent/Runtime/Compaction/AgentTokenBudgetResolver.cs`.
