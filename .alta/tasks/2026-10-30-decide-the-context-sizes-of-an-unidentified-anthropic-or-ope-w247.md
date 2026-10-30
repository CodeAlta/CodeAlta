---
title: "Decide the context sizes of an unidentified Anthropic or OpenAI endpoint"
kind: problem
status: pending
created: 2026-10-30
summary: "A gateway whose endpoint models.dev does not know falls back to the vendor's provider id and to the Claude API, so it inherits the vendor's price tiers and the 200K standard window as context sizes a session can choose."
---

## Why

Two guesses let a gateway be taken for the vendor's own endpoint. Neither changes the default size of a session or the requests it sends: both only add a size the user can choose that the gateway may not price differently. The tests of #274 pass only because they name a models.dev provider id the registry builder does not produce.

- [ConfiguredModelProviderRegistryBuilder.cs](src/CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs#L1396) `ResolveModelsDevProviderId` falls back to the type's default (`openai`, `anthropic`) for an endpoint `TryFindProviderByApiUrl` does not know, so the `foundProviderId == modelsDevProviderId` guard of [AgentModelMetadataEnricher.cs](src/CodeAlta.Agent/ModelCatalog/AgentModelMetadataEnricher.cs#L78) ("the prices of the provider itself, not those of the vendor of a model it resells") does not hold: the gateway gets the vendor's `cost.tiers` as sizes. The same fallback already gives it the vendor's prices, so the gap is older than #274.
- [AnthropicModelProviderRuntime.cs](src/CodeAlta.Agent.Anthropic/AnthropicModelProviderRuntime.cs#L523) reads `IsClaudeApi(provider.BaseUri ?? providerDescriptor.BaseUri)`, which is true when neither is set — but the SDK then resolves `ANTHROPIC_BASE_URL`, as the comment at line 444 says, where `IsClaudeApi(new Uri(client.BaseUrl))` is used instead. A gateway configured only by that variable gets the 200K standard window. Line 424 reads the base URI the same way for the thinking-block binding, where being wrong matters more.

## What to do

Tell a models.dev provider id the user configured or an endpoint the catalog recognized apart from the type's default fallback, and use only the first for what is the provider's own (price tiers, prices). For the Anthropic endpoint, resolve it as the SDK does before deciding `IsClaudeApi`, or say in the options that the variable is not read.

## Where

`src/CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs`, `src/CodeAlta.Agent/ModelCatalog/AgentModelMetadataEnricher.cs`, `src/CodeAlta.Agent.Anthropic/AnthropicModelProviderRuntime.cs`.
