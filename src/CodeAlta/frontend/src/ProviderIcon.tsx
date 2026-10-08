import { createContext, useContext } from "react";
import type { ConfigurationSnapshot } from "#neoastra";
import { AppIcon, type IconName } from "./AppIcon";
import { BrandIcon, brandTitle } from "./BrandIcon";
import { modelBrand, providerBrand, type Brand, type ProviderBrandSource } from "./brands";
import { symbolIcons } from "./symbolIcons";

/** What the configuration says of each provider, by its key in lower case. */
export type ProviderBrands = ReadonlyMap<string, ProviderBrandSource>;

const empty: ProviderBrands = new Map();

/** The providers of the configuration, for every logo of a provider that the window draws. */
export const ProviderBrandsContext = createContext<ProviderBrands>(empty);

/**
 * The providers of a configuration by key: every definition of the file, so that a session of a provider that
 * is disabled or has no credentials keeps its logo, and the registered providers for what the file leaves out.
 */
export function providerBrands(snapshot: ConfigurationSnapshot | null | undefined): ProviderBrands {
  if (!snapshot) return empty;
  const brands = new Map<string, ProviderBrandSource>();
  for (const provider of snapshot.providers) brands.set(provider.id.toLowerCase(), { key: provider.id, type: provider.type, name: provider.name });
  for (const provider of snapshot.providerBrands ?? []) brands.set(provider.key.toLowerCase(), provider);
  return brands;
}

/** The logo of a provider, from what the configuration says of it and from what the caller knows of it. */
export function useProviderBrand(providerKey: string | null | undefined, known?: ProviderBrandSource): Brand {
  const configured = useContext(ProviderBrandsContext).get(providerKey?.toLowerCase() ?? "");
  return providerBrand({ key: providerKey, ...configured, ...defined(known) });
}

function defined(source: ProviderBrandSource | undefined): ProviderBrandSource {
  return source ? Object.fromEntries(Object.entries(source).filter(([, value]) => value !== undefined && value !== null && value !== "")) : {};
}

/** The icon of a brand as it was found: its logo, the general icon chosen in its place, or the icon the caller falls back to. */
export function Logo({ brand, size, fallback, labelled }: { brand: Brand; size: number; fallback: IconName; labelled?: boolean }) {
  if (brand.icon) return <BrandIcon name={brand.icon} size={size} color={brand.color} title={labelled ? brandTitle(brand.icon) : undefined} />;
  const style = brand.color ? { color: brand.color } : undefined;
  if (!brand.symbol) return <AppIcon name={fallback} size={size} style={style} />;
  const Symbol = symbolIcons[brand.symbol];
  return <Symbol className="symbol-icon" aria-hidden="true" focusable="false" strokeWidth={1.8} size={size} style={style} />;
}

/**
 * The logo of a provider, by its key: the one its definition names, else the one of the brand its key, name
 * or type names. A provider of no known brand is shown with the icon of a provider.
 */
export function ProviderIcon({ providerKey, known, size = 14, fallback = "provider", labelled }: {
  providerKey: string | null | undefined; known?: ProviderBrandSource; size?: number; fallback?: IconName; labelled?: boolean;
}) {
  return <Logo brand={useProviderBrand(providerKey, known)} size={size} fallback={fallback} labelled={labelled} />;
}

/** The logo of a model: the brand of its family, else the logo of the provider that serves it. */
export function ModelIcon({ modelId, providerKey, size = 14, fallback = "model" }: { modelId: string | null | undefined; providerKey?: string | null; size?: number; fallback?: IconName }) {
  return <Logo brand={modelBrand(modelId, useProviderBrand(providerKey))} size={size} fallback={fallback} />;
}
