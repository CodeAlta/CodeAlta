import { useId, type CSSProperties } from "react";
import { brandIcons, type BrandIconData } from "./brandIcons.gen";

/** The id of a brand whose logo the app can draw, as a provider names it with `icon` in the configuration. */
export type BrandIconName = keyof typeof brandIcons;

const table: Readonly<Record<string, BrandIconData>> = brandIcons;

/** Every brand, by name. */
export const brandIconNames = (Object.keys(brandIcons) as BrandIconName[]).sort((left, right) => table[left].title.localeCompare(table[right].title));

/** Whether a text is the id of a brand. */
export function isBrandIcon(name: string | null | undefined): name is BrandIconName {
  return typeof name === "string" && Object.hasOwn(table, name);
}

/** The name of a brand, as its owner writes it. */
export function brandTitle(name: BrandIconName): string {
  return table[name].title;
}

/**
 * The logo of a brand. Without a color it is the colored drawing of the brand, or its one-color mark in the
 * color of the brand, or in the color of the text when the brand has no color of its own (a black mark is
 * drawn light on a dark background). A color draws the one-color mark in that color.
 */
export function BrandIcon({ name, size = 16, color, title, className }: { name: BrandIconName; size?: number; color?: string; title?: string; className?: string }) {
  const icon = table[name];
  const colored = !color && icon.color !== undefined;
  // The gradients and masks of a drawing are named: each one drawn gets names of its own, so that a logo
  // does not depend on another one of the same brand that the page hides.
  const scope = useId().replace(/[^a-zA-Z0-9]/g, "");
  const drawing = colored ? icon.color!.replaceAll("lobe-icons-", `brand-${scope}-`) : icon.mono;
  const tint = color ?? (colored ? undefined : icon.tint);
  const style = tint === undefined ? undefined
    : { "--brand-tint": tint, "--brand-tint-dark": color ?? icon.darkTint ?? tint } as CSSProperties;
  return <svg className={className ? `brand-icon ${className}` : "brand-icon"} data-brand={name} viewBox="0 0 24 24" width={size} height={size}
    fill="currentColor" fillRule={!colored && icon.evenodd ? "evenodd" : undefined} style={style} focusable="false"
    role={title ? "img" : undefined} aria-label={title} aria-hidden={title ? undefined : true} dangerouslySetInnerHTML={{ __html: drawing }} />;
}
