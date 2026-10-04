type Model = Readonly<{ id: string; efforts: readonly string[]; defaultEffort: string | null }>;

/**
 * The model a new session starts with, as the terminal picks it: the provider's configured default model when
 * the provider offers it, otherwise the first model it lists. Null while no model is known.
 */
export function defaultModelId(models: readonly Model[], configured: string | null | undefined): string | null {
  if (models.length === 0) return null;
  return configured && models.some(model => model.id === configured) ? configured : models[0].id;
}

/**
 * The reasoning effort a model starts with, as the terminal picks it: the configured effort when the model
 * supports it, then High, then the model's own default, then its first effort. Null leaves it to the model.
 */
export function defaultReasoningEffort(model: Model | undefined, configured?: string | null): string | null {
  if (!model) return null;
  const supported = (effort: string | null | undefined) => effort ? model.efforts.find(value => value.toLowerCase() === effort.toLowerCase()) : undefined;
  if (model.efforts.length === 0) return null;
  return supported(configured) ?? supported("high") ?? supported(model.defaultEffort) ?? model.efforts[0];
}
